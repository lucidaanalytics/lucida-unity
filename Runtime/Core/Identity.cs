using System.Collections.Generic;

namespace Lucida
{
    /// <summary>Anonymous UUIDv7 until Identify(); persisted through the (gated) store.</summary>
    public sealed class Identity
    {
        const string Key = "lc_identity";
        readonly IKeyValueStore store;
        readonly IClock clock;

        public string DistinctId { get; private set; }
        public bool Identified { get; private set; }
        string anonId = "";

        /// <summary>Pre-identify id, sent as anon_distinct_id to /v1/flags so continuity flags keep their bucket (LC-FLG-34).</summary>
        public string PriorAnonId { get { return Identified && anonId.Length > 0 && anonId != DistinctId ? anonId : null; } }

        public Identity(IKeyValueStore store, IClock clock)
        {
            this.store = store;
            this.clock = clock;
            var raw = store.Get(Key);
            var saved = raw == null ? null : Json.TryParseObject(raw);
            object id;
            object identified;
            if (saved != null && saved.TryGetValue("distinct_id", out id) && id is string)
            {
                DistinctId = (string)id;
                Identified = saved.TryGetValue("identified", out identified) && identified is bool && (bool)identified;
                object anon;
                anonId = saved.TryGetValue("anon_id", out anon) && anon is string ? (string)anon : (Identified ? "" : DistinctId);
            }
            else
            {
                DistinctId = Uuid.V7(clock.NowMs());
                anonId = DistinctId;
                Save();
            }
        }

        void Save()
        {
            store.Set(Key, Json.Serialize(new Dictionary<string, object>
            {
                { "distinct_id", DistinctId }, { "identified", Identified }, { "anon_id", anonId },
            }));
        }

        /// <summary>
        /// Returns the $identify properties to send, or null when nothing changed. The previous id is linked as
        /// $anon_distinct_id only while anonymous (the gateway refuses to merge two identified persons).
        /// </summary>
        public Dictionary<string, object> Identify(string newId)
        {
            if (string.IsNullOrEmpty(newId) || newId == DistinctId)
            {
                if (!string.IsNullOrEmpty(newId) && !Identified)
                {
                    Identified = true;
                    Save();
                }
                return null;
            }
            var prev = DistinctId;
            var wasAnon = !Identified;
            DistinctId = newId;
            Identified = true;
            Save();
            var props = new Dictionary<string, object>();
            if (wasAnon) props["$anon_distinct_id"] = prev;
            return props;
        }

        public void Reset()
        {
            DistinctId = Uuid.V7(clock.NowMs());
            anonId = DistinctId;
            Identified = false;
            Save();
        }
    }
}
