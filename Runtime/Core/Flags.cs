using System;
using System.Collections.Generic;

namespace Lucida
{
    /// <summary>
    /// Feature flags: local evaluation over optional bootstrap definitions, server evaluation (POST /v1/flags) for flags
    /// that need it (cohorts) or when no definitions are present. The last response is cached in (consent-gated) storage
    /// so cold starts have values offline. <see cref="Reload"/> is asynchronous through <see cref="IHttp"/>.
    /// </summary>
    public sealed class Flags
    {
        const string CacheKey = "lc_flags";

        readonly LucidaCore core;
        readonly Dictionary<string, object> deviceProperties;
        readonly string appVersion;
        readonly List<object> evaluationContexts = new List<object>();
        readonly HashSet<string> called = new HashSet<string>();
        IList<object> defs = new List<object>();
        Dictionary<string, object> values = new Dictionary<string, object>();
        Dictionary<string, object> payloads = new Dictionary<string, object>();
        Dictionary<string, object> local = new Dictionary<string, object>();
        int seq;

        public string RequestId { get; private set; }

        public Flags(LucidaCore core, IList<object> definitions = null, Dictionary<string, object> deviceProperties = null, string appVersion = null,
            bool sendFeatureFlags = true, IList<string> evaluationContexts = null)
        {
            this.core = core;
            if (evaluationContexts != null) foreach (var c in evaluationContexts) if (!string.IsNullOrEmpty(c)) this.evaluationContexts.Add(c);
            this.deviceProperties = deviceProperties ?? new Dictionary<string, object>();
            this.appVersion = appVersion;
            if (definitions != null) defs = definitions;
            // flag values ride on every event (not $snapshot) unless sendFeatureFlags is off; the caller's properties win
            if (sendFeatureFlags) core.Enricher = ev => ev == "$snapshot" ? null : FlagEnrich.Props(values);
            var raw = core.Storage.Get(CacheKey);
            var cached = raw == null ? null : Json.TryParseObject(raw);
            if (cached == null) return;
            values = (cached.ContainsKey("values") ? cached["values"] as Dictionary<string, object> : null) ?? values;
            payloads = (cached.ContainsKey("payloads") ? cached["payloads"] as Dictionary<string, object> : null) ?? payloads;
        }

        public void SetDefinitions(IList<object> d) { defs = d; }

        Dictionary<string, object> Context()
        {
            var groups = new Dictionary<string, object>();
            foreach (var kv in core.Groups) groups[kv.Key] = kv.Value;
            var c = new Dictionary<string, object>
            {
                { "distinct_id", core.DistinctId },
                { "groups", groups },
                { "person_properties", new Dictionary<string, object>() },
                { "device_properties", deviceProperties },
            };
            if (appVersion != null) c["app_version"] = appVersion;
            // Flags scoped to other app contexts are dropped locally and by the server (G227).
            if (evaluationContexts.Count > 0) c["app_contexts"] = evaluationContexts;
            return c;
        }

        /// <summary>Local pass; true when the server must decide (or there are no definitions).</summary>
        bool EvalLocal()
        {
            if (defs.Count == 0) return true;
            var server = false;
            var v = new Dictionary<string, object>();
            foreach (var kv in FlagEvaluator.EvaluateAll(defs, Context()))
            {
                if (kv.Value.Reason == "app_context_excluded") continue;
                if (kv.Value.RequiresServer) { server = true; continue; }
                v[kv.Key] = kv.Value.Value;
                if (kv.Value.Payload != null) payloads[kv.Key] = kv.Value.Payload;
            }
            local = v;
            foreach (var kv in v) values[kv.Key] = kv.Value;
            return server;
        }

        Dictionary<string, object> RemoteBody(Dictionary<string, object> groups)
        {
            var body = new Dictionary<string, object>
            {
                { "api_key", core.Config.ApiKey }, { "distinct_id", core.DistinctId }, { "groups", groups },
                { "person_properties", new Dictionary<string, object>() },
            };
            if (evaluationContexts.Count > 0) body["evaluation_contexts"] = evaluationContexts;
            var anon = core.Identity.PriorAnonId;
            if (anon != null) body["anon_distinct_id"] = anon;
            return body;
        }

        /// <summary>Evaluates locally, then asks the server when needed (and consent allows). <c>done(ok)</c> is optional.</summary>
        public void Reload(Action<bool> done = null)
        {
            var mine = ++seq;
            if (!EvalLocal() || !core.Consent.Has("analytics")) { if (done != null) done(true); return; }
            var groups = new Dictionary<string, object>();
            foreach (var kv in core.Groups) groups[kv.Key] = kv.Value;
            var req = new HttpRequest
            {
                Method = "POST",
                Url = core.Host + "/v1/flags",
                Body = Json.Serialize(RemoteBody(groups)),
            };
            req.Headers["Content-Type"] = "application/json";
            core.Config.Http.Send(req, res =>
            {
                var ok = res != null && res.Status >= 200 && res.Status <= 299 && Apply(mine, res.Body);
                if (done != null) done(ok);
            });
        }

        bool Apply(int mine, string body)
        {
            var j = Json.TryParseObject(body);
            if (j == null) return false;
            var v = new Dictionary<string, object>();
            var p = new Dictionary<string, object>();
            var flags = j.ContainsKey("flags") ? j["flags"] as Dictionary<string, object> : null;
            if (flags != null)
            {
                foreach (var kv in flags)
                {
                    var m = kv.Value as Dictionary<string, object>;
                    if (m == null) { v[kv.Key] = kv.Value; continue; }
                    object x;
                    if (m.TryGetValue("value", out x) && x != null) v[kv.Key] = x;
                    else if (m.TryGetValue("variant", out x) && x != null) v[kv.Key] = x;
                    else v[kv.Key] = m.TryGetValue("enabled", out x) && x is bool && (bool)x;
                    if (m.TryGetValue("payload", out x) && x != null) p[kv.Key] = x;
                }
            }
            if (mine != seq) return false; // a newer reload superseded this one
            RequestId = j.ContainsKey("request_id") ? j["request_id"] as string : null;
            values = new Dictionary<string, object>(local);
            foreach (var kv in v) values[kv.Key] = kv.Value;
            payloads = p;
            core.Storage.Set(CacheKey, Json.Serialize(new Dictionary<string, object> { { "values", values }, { "payloads", payloads } }));
            return true;
        }

        /// <summary>false | true | variant key. Emits $feature_flag_called once per key/value.</summary>
        public object Get(string key)
        {
            object v;
            if (!values.TryGetValue(key, out v) || v == null) v = false;
            if (called.Add(key + "=" + v))
            {
                core.Capture("$feature_flag_called", new Dictionary<string, object>
                {
                    { "$feature_flag", key }, { "$feature_flag_response", v },
                });
            }
            return v;
        }

        public bool IsEnabled(string key)
        {
            var v = Get(key);
            return !(v is bool) || (bool)v;
        }

        public object Payload(string key)
        {
            object p;
            return payloads.TryGetValue(key, out p) ? p : null;
        }

        public Dictionary<string, object> All() { return new Dictionary<string, object>(values); }
    }
}
