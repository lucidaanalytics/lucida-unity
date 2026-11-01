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
    /// shortcut: rewrites the whole JSON list on every enqueue (O(n), n &lt;= MaxQueueSize); use an append log if profiling says so.
    /// </summary>
    public sealed class EventQueue
    {
        const string Key = "lc_queue";
        const int MaxBatch = 1000;

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
            var raw = store.Get(Key);
            if (raw == null) return;
            try
            {
                var saved = Json.Parse(raw) as List<object>;
                if (saved != null) items.AddRange(saved);
            }
            catch (Exception) { /* corrupt queue file: start empty */ }
        }

        public int Count { get { return items.Count; } }

        void Persist()
        {
            if (items.Count == 0) store.Remove(Key); else store.Set(Key, Json.Serialize(items));
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
            }
            Persist();
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
            Persist();
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
            store.Remove(Key);
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
