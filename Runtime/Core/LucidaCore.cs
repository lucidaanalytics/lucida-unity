using System;
using System.Collections.Generic;

namespace Lucida
{
    public sealed class CoreConfig
    {
        public string ApiKey = "";
        public string ApiHost = "https://capture.lucidace.com";
        public IHttp Http;
        /// <summary>Real persistence; the core gates it so nothing is written before consent.</summary>
        public IKeyValueStore Store;
        public IClock Clock = new SystemClock();
        public ConsentMode ConsentMode = ConsentMode.OptOut;
        public bool QueueBeforeConsent;
        public string LibName = "lucida-unity";
        public string LibVersion = "0.1.0";
        public int FlushAt = 20;
        public long FlushIntervalMs = 5000;
        public int MaxQueueSize = 1000;
        public Func<double> Random = new System.Random().NextDouble;
        public Action<IList<object>, string> OnDrop;
    }

    /// <summary>
    /// Engine-free client: identity, consent gating, groups, batching queue. The host drives it by calling
    /// <see cref="Tick"/> every frame and <see cref="Flush"/> when the app pauses or quits.
    /// </summary>
    public sealed class LucidaCore
    {
        const string GroupsKey = "lc_groups";

        public readonly CoreConfig Config;
        public readonly Consent Consent;
        public readonly Identity Identity;
        public readonly SessionManager Session;
        public readonly EventQueue Queue;
        public readonly string Host;
        readonly IClock clock;
        readonly GatedStore gated;
        readonly List<Dictionary<string, object>> held = new List<Dictionary<string, object>>();
        readonly Dictionary<string, string> groups = new Dictionary<string, string>();

        public LucidaCore(CoreConfig cfg)
        {
            Config = cfg;
            clock = cfg.Clock;
            Host = cfg.ApiHost.TrimEnd('/');
            Consent = new Consent(cfg.Store, cfg.ConsentMode, cfg.QueueBeforeConsent);
            gated = new GatedStore(cfg.Store, () => Consent.Has("analytics"));
            Identity = new Identity(gated, clock);
            Session = new SessionManager(gated, clock);
            // The queue uses the raw store: events only reach it after consent, and a revoke must really delete it.
            Queue = new EventQueue(cfg.Http, cfg.Store, clock, new QueueConfig
            {
                Url = Host + "/v1/batch", ApiKey = cfg.ApiKey, FlushAt = cfg.FlushAt, FlushIntervalMs = cfg.FlushIntervalMs,
                MaxQueueSize = cfg.MaxQueueSize, Random = cfg.Random,
            }, cfg.OnDrop);
            var saved = Json.TryParseObject(gated.Get(GroupsKey) ?? "");
            if (saved != null) foreach (var kv in saved) if (kv.Value is string) groups[kv.Key] = (string)kv.Value;
        }

        public string DistinctId { get { return Identity.DistinctId; } }

        /// <summary>Current session id; reading it extends the idle window, like iOS `sessionId`.</summary>
        public string SessionId { get { return Session.Touch(); } }

        public bool IsOptedOut { get { return !Consent.Has("analytics"); } }

        /// <summary>Consent-gated storage for modules (flag cache).</summary>
        public IKeyValueStore Storage { get { return gated; } }

        /// <summary>Extra properties for an event name (flag values); the caller's own properties win. Null result adds nothing.</summary>
        public Func<string, IDictionary<string, object>> Enricher;

        public Dictionary<string, string> Groups { get { return new Dictionary<string, string>(groups); } }

        /// <summary>Returns the event, or null when consent forbids capturing.</summary>
        public Dictionary<string, object> Capture(string name, IDictionary<string, object> properties = null,
            IDictionary<string, object> set = null, IDictionary<string, object> setOnce = null)
        {
            var granted = Consent.Has("analytics");
            if (!granted && !(Consent.Pending && Consent.QueueBeforeConsent)) return null;
            var now = clock.NowMs();
            var props = new Dictionary<string, object>();
            if (properties != null) foreach (var kv in properties) props[kv.Key] = kv.Value;
            var extra = Enricher == null ? null : Enricher(name);
            if (extra != null) foreach (var kv in extra) if (!props.ContainsKey(kv.Key)) props[kv.Key] = kv.Value;
            props["$lib"] = Config.LibName;
            props["$lib_version"] = Config.LibVersion;
            props["$session_id"] = Session.Touch();
            var e = new Dictionary<string, object>
            {
                { "uuid", Uuid.V7(now) },
                { "event", name },
                { "distinct_id", Identity.DistinctId },
                { "timestamp", Uuid.Iso(now) },
                { "properties", props },
            };
            if (set != null && set.Count > 0) e["$set"] = set;
            if (setOnce != null && setOnce.Count > 0) e["$set_once"] = setOnce;
            if (groups.Count > 0) e["$groups"] = new Dictionary<string, string>(groups);
            if (granted) Queue.Enqueue(e);
            else if (held.Count < Config.MaxQueueSize) held.Add(e);
            return e;
        }

        public void Identify(string id, IDictionary<string, object> set = null, IDictionary<string, object> setOnce = null)
        {
            var r = Identity.Identify(id);
            if (r != null || set != null || setOnce != null) Capture("$identify", r, set, setOnce);
        }

        /// <summary>Sets person properties without changing identity (a `$set` event).</summary>
        public void SetPersonProperties(IDictionary<string, object> set = null, IDictionary<string, object> setOnce = null)
        {
            if ((set == null || set.Count == 0) && (setOnce == null || setOnce.Count == 0)) return;
            Capture("$set", null, set, setOnce);
        }

        /// <summary>Links alias to the current (or given) distinct id.</summary>
        public void Alias(string alias, string original = null)
        {
            Capture("$create_alias", new Dictionary<string, object>
            {
                { "alias", alias }, { "distinct_id", original ?? DistinctId },
            });
        }

        public void Group(string type, string key, IDictionary<string, object> properties = null)
        {
            groups[type] = key;
            gated.Set(GroupsKey, Json.Serialize(groups));
            Capture("$groupidentify", new Dictionary<string, object>
            {
                { "$group_type", type }, { "$group_key", key },
                { "$group_set", properties ?? new Dictionary<string, object>() },
            });
        }

        public void Screen(string name, IDictionary<string, object> properties = null)
        {
            var props = new Dictionary<string, object>();
            if (properties != null) foreach (var kv in properties) props[kv.Key] = kv.Value;
            props["$screen_name"] = name;
            Capture("$screen", props);
        }

        public void Reset()
        {
            Identity.Reset();
            Session.Reset();
            groups.Clear();
            gated.Remove(GroupsKey);
        }

        /// <summary>Records a consent decision. Granting analytics releases state and held events; revoking drops them.</summary>
        public void SetConsent(IDictionary<string, bool> update)
        {
            var was = Consent.Has("analytics");
            bool will;
            if (!update.TryGetValue("analytics", out will)) will = was;
            if (was && !will)
            {
                Capture("$opt_out");
                Queue.DrainThenClear();
            }
            var copy = new Dictionary<string, bool>(update);
            Consent.Set(copy);
            if (!was && will)
            {
                gated.FlushBuffered();
                foreach (var e in held) Queue.Enqueue(e);
                held.Clear();
                Capture("$opt_in");
            }
            if (!will)
            {
                held.Clear();
                gated.DiscardBuffered();
            }
        }

        public void OptIn() { SetConsent(All(true)); }
        public void OptOut() { SetConsent(All(false)); }

        static Dictionary<string, bool> All(bool v)
        {
            return new Dictionary<string, bool> { { "analytics", v }, { "replay", v }, { "marketing", v } };
        }

        public void Tick() { Queue.Tick(); }
        public void Flush() { Queue.Flush(); }
        public void Shutdown() { Queue.Shutdown(); }
    }
}
