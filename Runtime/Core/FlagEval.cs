using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lucida
{
    public sealed class FlagResult
    {
        /// <summary>false, true or a variant key.</summary>
        public object Value;
        public string Reason;
        public int ConditionIndex;
        public string Variant;
        public object Payload;

        public bool RequiresServer { get { return Reason == FlagEvaluator.RequiresServer; } }
    }

    /// <summary>
    /// Local flag evaluator; behaviour pinned by the shared testdata/flags vectors. Flags and context are the JSON shapes
    /// (Dictionary&lt;string, object&gt;). Supports person/group/device aggregation, namespace, holdout and sticky bucketing;
    /// multi-kind contexts (vectors_g18) are not supported.
    /// </summary>
    public static class FlagEvaluator
    {
        public const string RequiresServer = "requires_server";

        static FlagResult Off(string reason, int idx = -1)
        {
            return new FlagResult { Value = false, Reason = reason, ConditionIndex = idx };
        }

        // -- JSON-shape helpers --------------------------------------------------------------------

        static object Get(object m, string key)
        {
            var d = m as Dictionary<string, object>;
            object v;
            return d != null && key != null && d.TryGetValue(key, out v) ? v : null;
        }

        static bool Has(object m, string key)
        {
            var d = m as Dictionary<string, object>;
            return d != null && key != null && d.ContainsKey(key);
        }

        static string Str(object o)
        {
            if (o == null) return null;
            var s = o as string;
            return s ?? Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        static string S(object o) { return Str(o) ?? ""; }

        static bool True(object o) { return o is bool && (bool)o; }

        static double Num(object o) { return FlagOps.IsNum(o) ? Convert.ToDouble(o, CultureInfo.InvariantCulture) : 0; }

        static IList<object> Arr(object o) { return (o as IList<object>) ?? new object[0]; }

        // -- filters -------------------------------------------------------------------------------

        static bool PropMatch(object props, object fl, string op, object want, double now)
        {
            var key = S(Get(fl, "key"));
            return FlagOps.Match(op, Get(props, key), Has(props, key), want, false, now);
        }

        static bool FilterMatches(object fl, object c, Dictionary<string, FlagResult> resolved)
        {
            var op = S(Get(fl, "op"));
            var want = Get(fl, "value");
            var now = FlagOpsV2.ParseNow(Get(c, "now"));
            switch (Str(Get(fl, "type")))
            {
                case "app_version":
                    var v = S(Get(c, "app_version"));
                    return FlagOps.Match(op, v, v != "", want, true, now);
                case "flag":
                    FlagResult r;
                    return resolved.TryGetValue(S(Get(fl, "key")), out r) && FlagOps.Match(op, r.Value, true, want, false, now);
                case "person": return PropMatch(Get(c, "person_properties"), fl, op, want, now);
                case "group": return PropMatch(Get(Get(c, "group_properties"), Str(Get(fl, "group_type"))), fl, op, want, now);
                case "device": return PropMatch(Get(c, "device_properties"), fl, op, want, now);
                case "geo": return PropMatch(Get(c, "geo_properties"), fl, op, want, now);
                default: return false;
            }
        }

        static bool Undecidable(object fl, Dictionary<string, FlagResult> resolved)
        {
            var type = Str(Get(fl, "type"));
            if (type == "flag")
            {
                FlagResult r;
                return resolved.TryGetValue(S(Get(fl, "key")), out r) && r.RequiresServer;
            }
            return type == "cohort";
        }

        // -- evaluation ----------------------------------------------------------------------------

        static bool ValueMatches(object got, object want)
        {
            if (!(want is bool)) return Equals(got, want);
            return got is bool ? (bool)got == (bool)want : (bool)want;
        }

        static string BucketingKey(object f, object c) { return UnitKey(Get(f, "aggregation"), c); }

        static string UnitKey(object agg, object c)
        {
            switch (Str(Get(agg, "kind")))
            {
                case "group": return S(Get(Get(c, "groups"), Str(Get(agg, "group_type"))));
                case "device": return S(Get(c, "device_id"));
                default:
                    var k = Str(Get(c, "bucketing_key"));
                    return string.IsNullOrEmpty(k) ? S(Get(c, "distinct_id")) : k;
            }
        }

        static object PickVariant(object f, object cond, string key)
        {
            var variants = Arr(Get(f, "variants"));
            var over = Str(Get(cond, "variant_override"));
            if (!string.IsNullOrEmpty(over))
                foreach (var v in variants) if (Str(Get(v, "key")) == over) return v;
            var h = FlagOps.Hash01(S(Get(f, "key")) + "." + S(Get(f, "salt")) + ".variant." + key) * 100;
            double cum = 0;
            foreach (var v in variants)
            {
                cum += Num(Get(v, "rollout_pct"));
                if (h < cum) return v;
            }
            return variants[variants.Count - 1];
        }

        static FlagResult Won(object variant, string reason, int idx)
        {
            var k = Str(Get(variant, "key"));
            return new FlagResult { Value = k, Reason = reason, ConditionIndex = idx, Variant = k, Payload = Get(variant, "payload") };
        }

        static FlagResult Plain(object f, string reason, int idx)
        {
            return new FlagResult
            {
                Value = true, Reason = reason, ConditionIndex = idx,
                Payload = Str(Get(f, "type")) == "json" ? Get(f, "payload") : null,
            };
        }

        // A stored value of a sticky flag decides once a condition's filters passed (LC-EXT-29).
        static FlagResult Sticky(object f, object cond, int idx, object c)
        {
            if (!True(Get(f, "sticky"))) return null;
            var stored = Get(Get(c, "sticky_assignments"), Str(Get(f, "key")));
            if (stored == null) return null;
            var variants = Arr(Get(f, "variants"));
            if (Str(Get(f, "type")) == "multivariate" && variants.Count > 0)
            {
                var over = Str(Get(cond, "variant_override"));
                foreach (var v in variants) if (Str(Get(v, "key")) == over) return null; // the override wins
                var s = stored as string;
                foreach (var v in variants) if (s != null && Str(Get(v, "key")) == s) return Won(v, "sticky_bucket", idx);
                return null;
            }
            return True(stored) ? Plain(f, "sticky_bucket", idx) : null;
        }

        // A flag scoped to app contexts is dropped when the request declares contexts and shares none.
        static bool AppContextAllowed(object f, object c)
        {
            var want = Arr(Get(f, "app_contexts"));
            var have = Arr(Get(c, "app_contexts"));
            if (want.Count == 0 || have.Count == 0) return true;
            foreach (var w in want) foreach (var h in have) if (Str(w) == Str(h)) return true;
            return false;
        }

        public static FlagResult Evaluate(Dictionary<string, object> f, Dictionary<string, object> c, Dictionary<string, FlagResult> resolved)
        {
            if (!AppContextAllowed(f, c)) return Off("app_context_excluded");
            if (!True(Get(f, "active"))) return Off("inactive");
            if (True(Get(f, "kill_switch"))) return Off("kill_switch");

            var server = false;
            foreach (var p in Arr(Get(f, "prerequisites")))
            {
                FlagResult r;
                if (!resolved.TryGetValue(S(Get(p, "flag")), out r)) return Off("prerequisite_missing");
                if (r.RequiresServer) server = true;
                else if (r.Reason == "prerequisite_cycle" || !ValueMatches(r.Value, Get(p, "value"))) return Off("prerequisite_failed");
            }
            if (server) return Off(RequiresServer);

            var key = BucketingKey(f, c);
            var conds = Arr(Get(f, "conditions"));
            var perCondition = false;
            foreach (var cd in conds) if (Get(cd, "aggregation") is Dictionary<string, object>) perCondition = true;
            if (key == "" && (!perCondition || Get(f, "holdout") != null || Get(f, "namespace") != null)) return Off("no_bucketing_key");

            var ho = Get(f, "holdout");
            if (ho != null && FlagOps.Hash01("holdout." + S(Get(ho, "salt")) + "." + key) * 100 < Num(Get(ho, "percentage")))
                return Off("holdout");

            var ns = Get(f, "namespace");
            if (ns != null && Num(Get(ns, "total")) > 0)
            {
                var b = Math.Floor(FlagOps.Hash01("namespace." + S(Get(ns, "salt")) + "." + key) * Num(Get(ns, "total")));
                if (b < Num(Get(ns, "start")) || b >= Num(Get(ns, "end"))) return Off("namespace_excluded");
            }

            var skipped = 0;
            for (var i = 0; i < conds.Count; i++)
            {
                var cond = conds[i];
                var own = Get(cond, "aggregation") as Dictionary<string, object>;
                var ckey = own == null ? key : UnitKey(own, c);
                if (ckey == "") { skipped++; continue; }
                var filters = Arr(Get(cond, "filters"));
                var undecided = false;
                var allLocal = true;
                foreach (var fl in filters)
                {
                    if (Undecidable(fl, resolved)) undecided = true;
                    else if (!FilterMatches(fl, c, resolved)) { allLocal = false; break; }
                }
                if (!allLocal) continue;
                if (undecided) return Off(RequiresServer, i);

                var stuck = own == null ? Sticky(f, cond, i, c) : null; // the store keys on the flag-level unit
                if (stuck != null) return stuck;

                var rollKey = S(Get(f, "key")) + "." + S(Get(f, "salt")) + "." + ckey;
                if (FlagOps.Hash01(rollKey) * 100 >= Num(Get(cond, "rollout_pct")))
                {
                    if (True(Get(f, "early_exit"))) return Off("out_of_rollout", i);
                    continue;
                }

                if (Str(Get(f, "type")) == "multivariate" && Arr(Get(f, "variants")).Count > 0)
                    return Won(PickVariant(f, cond, ckey), "condition_match", i);
                return Plain(f, "condition_match", i);
            }
            return Off(conds.Count > 0 && skipped == conds.Count ? "no_bucketing_key" : "no_condition_match");
        }

        static List<string> Deps(object f)
        {
            var d = new List<string>();
            foreach (var p in Arr(Get(f, "prerequisites"))) d.Add(S(Get(p, "flag")));
            foreach (var cond in Arr(Get(f, "conditions")))
                foreach (var fl in Arr(Get(cond, "filters")))
                    if (Str(Get(fl, "type")) == "flag") d.Add(S(Get(fl, "key")));
            return d;
        }

        /// <summary>Evaluates every flag, resolving prerequisites and flag filters depth-first. Result is keyed by flag key.</summary>
        public static Dictionary<string, FlagResult> EvaluateAll(IList<object> flags, Dictionary<string, object> ctx)
        {
            var byKey = new Dictionary<string, Dictionary<string, object>>();
            foreach (var f in flags)
            {
                var m = f as Dictionary<string, object>;
                if (m != null) byKey[S(Get(m, "key"))] = m;
            }
            var output = new Dictionary<string, FlagResult>();
            var state = new Dictionary<string, int>();
            foreach (var f in flags) Visit(S(Get(f, "key")), byKey, ctx, output, state);
            return output;
        }

        static void Visit(string key, Dictionary<string, Dictionary<string, object>> byKey, Dictionary<string, object> ctx,
            Dictionary<string, FlagResult> output, Dictionary<string, int> state)
        {
            Dictionary<string, object> f;
            if (!byKey.TryGetValue(key, out f) || state.ContainsKey(key)) return;
            state[key] = 1;
            var cyc = false;
            foreach (var dep in Deps(f))
            {
                Visit(dep, byKey, ctx, output, state);
                int st;
                FlagResult dr;
                if ((state.TryGetValue(dep, out st) && st == 1) || (output.TryGetValue(dep, out dr) && dr.Reason == "prerequisite_cycle")) cyc = true;
            }
            output[key] = cyc ? Off("prerequisite_cycle") : Evaluate(f, ctx, output);
            state[key] = 2;
        }
    }
}
