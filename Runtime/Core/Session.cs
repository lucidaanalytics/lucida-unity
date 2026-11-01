using System.Collections.Generic;

namespace Lucida
{
    /// <summary>UUIDv7 session rotated after 30 min idle or 24 h total; persisted through the (gated) store.</summary>
    public sealed class SessionManager
    {
        const string Key = "lc_session";
        readonly IKeyValueStore store;
        readonly IClock clock;
        readonly long idleMs;
        readonly long maxMs;
        readonly object gate = new object();

        public SessionManager(IKeyValueStore store, IClock clock, long idleMs = 30 * 60_000L, long maxMs = 24 * 3_600_000L)
        {
            this.store = store;
            this.clock = clock;
            this.idleMs = idleMs;
            this.maxMs = maxMs;
        }

        /// <summary>Returns the current session id, starting a new one when idle or too old, and extends the idle window.</summary>
        public string Touch()
        {
            lock (gate)
            {
                var t = clock.NowMs();
                var s = Json.TryParseObject(store.Get(Key) ?? "");
                object id = null, last = null, start = null;
                var fresh = s == null || !s.TryGetValue("id", out id) || !(id is string)
                    || !s.TryGetValue("last", out last) || !(last is double)
                    || !s.TryGetValue("start", out start) || !(start is double)
                    || t - (long)(double)last > idleMs || t - (long)(double)start > maxMs;
                var sid = fresh ? Uuid.V7(t) : (string)id;
                var begin = fresh ? t : (long)(double)start;
                store.Set(Key, Json.Serialize(new Dictionary<string, object> { { "id", sid }, { "start", begin }, { "last", t } }));
                return sid;
            }
        }

        public void Reset() { store.Remove(Key); }
    }
}
