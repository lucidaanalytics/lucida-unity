using System;
using System.Collections.Generic;

namespace Lucida
{
    public sealed class QueueConfig
    {
        public string Url = "";
        public string ApiKey = "";
        public int FlushAt = 20;
        public long FlushIntervalMs = 5000;
        public int MaxQueueSize = 1000;
        public int MaxRetries = 5;
        public Func<double> Random = new System.Random().NextDouble;
    }

    /// <summary>
    /// Persistent batching queue, single-threaded: the host calls <see cref="Tick"/> every frame. Flushes at N events or
    /// T ms, retries with backoff, drops oldest when full. Items are mirrored to the store and removed only once the
    /// server acknowledges them (at-least-once; the event uuid dedupes server-side).
    /// Storage layout: <c>lc_queue</c> is the index (<c>{"segs":n}</c>), <c>lc_queue.i</c> holds segment i (up to
    /// <see cref="SegmentSize"/> events). An enqueue rewrites only the last segment; an acknowledged batch or a drop of
    /// the oldest re-segments from the head.
    /// </summary>
    public sealed class EventQueue
    {
        const string Key = "lc_queue";
        const int MaxBatch = 1000;
        public const int SegmentSize = 50;

        readonly IHttp http;
        readonly IKeyValueStore store;
        readonly IClock clock;
        readonly QueueConfig cfg;
        readonly Action<IList<object>, string> onDrop;
        readonly List<object> items = new List<object>();
        bool inFlight;
        bool lostBatch;
        readonly List<Action<bool>> waiters = new List<Action<bool>>();
        bool closed;
        int attempt;
        int epoch;
        long dueAt;

        public EventQueue(IHttp http, IKeyValueStore store, IClock clock, QueueConfig cfg, Action<IList<object>, string> onDrop = null)
        {
            this.http = http;
            this.store = store;
            this.clock = clock;
            this.cfg = cfg;
            this.onDrop = onDrop ?? ((_, __) => { });
            Load();
        }

        void Load()
        {
            var raw = store.Get(Key);
            if (raw == null) return;
            try
            {
                var parsed = Json.Parse(raw);
                var legacy = parsed as List<object>;
                if (legacy != null)
                {
                    // the pre-segment layout: one JSON list under lc_queue
                    items.AddRange(legacy);
                    PersistAll();
                    return;
                }
                var index = parsed as Dictionary<string, object>;
                var n = Convert.ToInt32(index["segs"]);
                for (var i = 0; i < n; i++)
                {
                    var seg = store.Get(Key + "." + i);
                    if (seg == null) continue;
                    try
                    {
                        var saved = Json.Parse(seg) as List<object>;
                        if (saved != null) items.AddRange(saved);
                    }
                    catch (Exception) { /* a torn segment loses only itself */ }
                }
                segs = n;
            }
            catch (Exception) { /* corrupt index: start empty */ }
        }

        public int Count { get { return items.Count; } }

        int segs; // segments currently in the store

        int SegCount { get { return (items.Count + SegmentSize - 1) / SegmentSize; } }

        /// <summary>Rewrites every segment from the head (after items left the front) and drops stale ones.</summary>
        void PersistAll()
        {
            var n = SegCount;
            for (var i = 0; i < n; i++)
                store.Set(Key + "." + i, Json.Serialize(items.GetRange(i * SegmentSize, Math.Min(SegmentSize, items.Count - i * SegmentSize))));
            for (var i = n; i < segs; i++) store.Remove(Key + "." + i);
            segs = n;
            if (n == 0) store.Remove(Key);
            else store.Set(Key, "{\"segs\":" + n + "}");
        }

        /// <summary>After an append only the last segment changed.</summary>
        void PersistTail()
        {
            var n = SegCount;
            var from = (n - 1) * SegmentSize;
            store.Set(Key + "." + (n - 1), Json.Serialize(items.GetRange(from, items.Count - from)));
            if (n != segs)
            {
                segs = n;
                store.Set(Key, "{\"segs\":" + n + "}");
            }
        }

        public void Enqueue(Dictionary<string, object> e)
        {
            if (closed) return;
            items.Add(e);
            if (items.Count > cfg.MaxQueueSize)
            {
                var n = items.Count - cfg.MaxQueueSize;
                var dropped = items.GetRange(0, n);
                items.RemoveRange(0, n);
                onDrop(dropped, "queue_full");
                PersistAll();
            }
            else PersistTail();
            if (items.Count >= cfg.FlushAt) Flush();
            else if (dueAt == 0) dueAt = clock.NowMs() + cfg.FlushIntervalMs;
        }

        /// <summary>Call every frame: sends when the flush interval or a retry backoff has elapsed.</summary>
        public void Tick()
        {
            if (items.Count > 0 && !inFlight && dueAt != 0 && clock.NowMs() >= dueAt) Flush();
        }

        /// <summary>Sends everything queued, one batch at a time (the next batch follows each acknowledgement).</summary>
        public void Flush()
        {
            if (inFlight || items.Count == 0) return;
            var batch = items.GetRange(0, Math.Min(items.Count, MaxBatch));
            var body = Json.Serialize(new Dictionary<string, object>
            {
                { "api_key", cfg.ApiKey },
                { "batch", batch },
                { "sent_at", Uuid.Iso(clock.NowMs()) },
            });
            var req = new HttpRequest { Method = "POST", Url = cfg.Url + "?compression=gzip", Body = body, Gzip = true };
            req.Headers["Content-Type"] = "application/json";
            var mine = epoch;
            inFlight = true;
            http.Send(req, res => Settle(mine, batch, res == null ? 0 : res.Status));
        }

        /// <summary>
        /// Like <see cref="Flush"/>, then calls <c>done(true)</c> once everything queued was acknowledged, or
        /// <c>done(false)</c> when a batch is waiting on a retry backoff, was dropped or the queue was cleared.
        /// </summary>
        public void Flush(Action<bool> done)
        {
            if (items.Count == 0 && !inFlight) { done(true); return; }
            waiters.Add(done);
            Flush();
        }

        void Complete(bool ok)
        {
            if (waiters.Count == 0) return;
            var ready = waiters.ToArray();
            waiters.Clear();
            foreach (var w in ready) w(ok);
        }

        void Settle(int mine, List<object> batch, int status)
        {
            inFlight = false;
            if (mine != epoch) { Complete(false); return; } // cleared (consent revoked) while in flight
            if (status >= 200 && status <= 299)
            {
                RemoveSent(batch.Count);
            }
            else if (!Retry.IsRetryable(status) || attempt >= cfg.MaxRetries)
            {
                RemoveSent(batch.Count);
                lostBatch = true;
                onDrop(batch, "status_" + status);
            }
            else
            {
                dueAt = clock.NowMs() + Retry.DelayMs(attempt++, cfg.Random);
                Complete(false);
                return;
            }
            attempt = 0;
            dueAt = items.Count > 0 ? clock.NowMs() : 0;
            if (items.Count > 0) { Flush(); return; }
            var ok = !lostBatch;
            lostBatch = false;
            Complete(ok);
        }

        void RemoveSent(int n)
        {
            items.RemoveRange(0, Math.Min(n, items.Count));
            PersistAll();
        }

        /// <summary>Consent revoked: send what is queued (it includes $opt_out), then delete the rest.</summary>
        public void DrainThenClear()
        {
            Flush();
            Clear();
        }

        /// <summary>Discard everything (consent revoked). An in-flight request still completes; its result is ignored.</summary>
        public void Clear()
        {
            epoch++;
            items.Clear();
            PersistAll();
            dueAt = 0;
            attempt = 0;
        }

        public void Shutdown()
        {
            Flush();
            closed = true;
        }
    }
}
