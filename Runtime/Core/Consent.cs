using System.Collections.Generic;

namespace Lucida
{
    public enum ConsentMode { OptIn, OptOut }

    /// <summary>
    /// opt_in: every category starts denied. opt_out: granted unless a stored decision says otherwise.
    /// Only <see cref="Set"/> writes to the real store.
    /// </summary>
    public sealed class Consent
    {
        const string Key = "lc_consent";
        static readonly string[] Categories = { "analytics", "replay", "marketing" };

        readonly IKeyValueStore store;
        readonly Dictionary<string, bool> state = new Dictionary<string, bool>();

        public ConsentMode Mode { get; private set; }
        public bool QueueBeforeConsent { get; private set; }

        public Consent(IKeyValueStore store, ConsentMode mode = ConsentMode.OptOut, bool queueBeforeConsent = false)
        {
            this.store = store;
            Mode = mode;
            QueueBeforeConsent = queueBeforeConsent;
            foreach (var c in Categories) state[c] = mode == ConsentMode.OptOut;
            var raw = store.Get(Key);
            var saved = raw == null ? null : Json.TryParseObject(raw);
            if (saved == null) return;
            foreach (var c in Categories)
            {
                object v;
                if (saved.TryGetValue(c, out v) && v is bool) state[c] = (bool)v;
            }
        }

        public bool Has(string category)
        {
            bool v;
            return state.TryGetValue(category, out v) && v;
        }

        /// <summary>True while an opt_in project still waits for an analytics decision.</summary>
        public bool Pending { get { return Mode == ConsentMode.OptIn && !Has("analytics"); } }

        public void Set(IDictionary<string, bool> update)
        {
            foreach (var c in Categories)
            {
                bool v;
                if (update.TryGetValue(c, out v)) state[c] = v;
            }
            store.Set(Key, Json.Serialize(state));
        }
    }

    /// <summary>Keeps writes in memory until <c>allowed</c> is true; guarantees no storage before consent.</summary>
    public sealed class GatedStore : IKeyValueStore
    {
        readonly IKeyValueStore inner;
        readonly System.Func<bool> allowed;
        readonly Dictionary<string, string> mem = new Dictionary<string, string>(); // null value = pending removal

        public GatedStore(IKeyValueStore inner, System.Func<bool> allowed)
        {
            this.inner = inner;
            this.allowed = allowed;
        }

        public string Get(string key)
        {
            string v;
            return mem.TryGetValue(key, out v) ? v : inner.Get(key);
        }

        public void Set(string key, string value)
        {
            if (allowed()) inner.Set(key, value); else mem[key] = value;
        }

        public void Remove(string key)
        {
            if (allowed()) inner.Remove(key); else mem[key] = null;
        }

        public void FlushBuffered()
        {
            foreach (var kv in mem)
            {
                if (kv.Value == null) inner.Remove(kv.Key); else inner.Set(kv.Key, kv.Value);
            }
            mem.Clear();
        }

        public void DiscardBuffered() { mem.Clear(); }
    }
}
