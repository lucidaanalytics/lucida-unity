using System;
using System.Collections.Generic;

namespace Lucida
{
    /// <summary>Event enrichment with flag values (LC-FLG-28): <c>$feature/&lt;key&gt;</c> per flag plus sorted <c>$active_feature_flags</c>.</summary>
    public static class FlagEnrich
    {
        /// <summary>Properties for a set of flag values; empty when no flag is known.</summary>
        public static Dictionary<string, object> Props(IDictionary<string, object> values)
        {
            var o = new Dictionary<string, object>();
            if (values == null || values.Count == 0) return o;
            var active = new List<string>();
            foreach (var kv in values)
            {
                o["$feature/" + kv.Key] = kv.Value;
                if (!(kv.Value is bool) || (bool)kv.Value) active.Add(kv.Key);
            }
            active.Sort(StringComparer.Ordinal);
            o["$active_feature_flags"] = new List<object>(active);
            return o;
        }

        /// <summary>Adds <see cref="Props"/> to <paramref name="props"/> without overwriting anything the caller set.</summary>
        public static void AddTo(IDictionary<string, object> props, IDictionary<string, object> values)
        {
            foreach (var kv in Props(values)) if (!props.ContainsKey(kv.Key)) props[kv.Key] = kv.Value;
        }
    }
}
