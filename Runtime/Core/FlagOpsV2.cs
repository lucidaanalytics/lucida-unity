using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Lucida
{
    /// <summary>Spec v2 operators (testdata/flags/README.md, "Spec v2"); port of go/internal/flageval/ops_v2.go. Instants are unix seconds.</summary>
    public static class FlagOpsV2
    {
        static readonly Regex Rel = new Regex(@"^-?(\d{1,6})([hdwmy])$");
        static readonly Regex Rfc3339 = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$");
        static readonly Regex DateOnly = new Regex(@"^\d{4}-\d{2}-\d{2}$");
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static double WallClock()
        {
            return (DateTime.UtcNow - Epoch).TotalSeconds;
        }

        static double? RelativeSeconds(string s)
        {
            var m = Rel.Match(s.Trim());
            if (!m.Success) return null;
            double unit;
            switch (m.Groups[2].Value)
            {
                case "h": unit = 3600; break;
                case "d": unit = 86400; break;
                case "w": unit = 7 * 86400; break;
                case "m": unit = 30 * 86400; break;
                default: unit = 365 * 86400; break;
            }
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * unit;
        }

        static double? Rfc(string s)
        {
            DateTimeOffset t;
            if (!Rfc3339.IsMatch(s) || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return null;
            return (t.UtcDateTime - Epoch).TotalSeconds;
        }

        /// <summary>The evaluation clock: context "now" (RFC 3339) when it parses, else the wall clock.</summary>
        public static double ParseNow(object ctxNow)
        {
            var s = ctxNow as string;
            return (s == null ? null : Rfc(s)) ?? WallClock();
        }

        static double? Instant(object v, double now, bool rel)
        {
            if (FlagOps.IsNum(v)) return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            var str = v as string;
            if (str == null) return null;
            var s = str.Trim();
            if (rel)
            {
                var secs = RelativeSeconds(s);
                if (secs != null) return now - secs.Value;
            }
            var t = Rfc(s);
            if (t != null) return t;
            DateTime d;
            if (DateOnly.IsMatch(s) && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d))
                return (d - Epoch).TotalSeconds;
            return null;
        }

        static int CmpParts(List<int> a, List<int> b)
        {
            var n = Math.Max(a.Count, b.Count);
            for (var i = 0; i < n; i++)
            {
                var c = (i < a.Count ? a[i] : 0).CompareTo(i < b.Count ? b[i] : 0);
                if (c != 0) return c;
            }
            return 0;
        }

        static List<int> Big(params int[] v)
        {
            var l = new List<int>();
            foreach (var x in v) l.Add(x);
            return l;
        }

        /// <summary>Half-open [lo, hi) of a tilde, caret or wildcard value; false when the value is unusable.</summary>
        static bool Range(string op, object want, out List<int> lo, out List<int> hi)
        {
            lo = hi = null;
            var str = want as string;
            if (str == null) return false;
            var s = str.Trim();
            if (s.StartsWith("v", StringComparison.Ordinal)) s = s.Substring(1);
            var parts = s.Split('.');
            if (op == "semver_wildcard")
            {
                var last = parts[parts.Length - 1];
                if (last != "*" && last != "x" && last != "X") return false;
                if (parts.Length == 1)
                {
                    lo = Big(0);
                    hi = Big(1 << 30);
                    return true;
                }
                lo = FlagOps.SemVerParts(string.Join(".", parts, 0, parts.Length - 1));
                if (lo == null) return false;
                hi = new List<int>(lo);
                hi[hi.Count - 1] += 1;
                return true;
            }
            lo = FlagOps.SemVerParts(want);
            if (lo == null) return false;
            while (lo.Count < 3) lo.Add(0);
            int a = lo[0], b = lo[1], c = lo[2];
            if (op == "semver_tilde" && parts.Length == 1) hi = new List<int> { a + 1, 0, 0 };
            else if (op == "semver_tilde" || (a == 0 && b > 0)) hi = new List<int> { a, b + 1, 0 };
            else if (a > 0) hi = new List<int> { a + 1, 0, 0 };
            else hi = new List<int> { 0, 0, c + 1 };
            return true;
        }

        /// <summary>Applies a v2 operator to a set property. Unknown operators never match.</summary>
        public static bool Match(string op, object prop, object want, double now)
        {
            switch (op)
            {
                case "icontains":
                case "not_icontains":
                case "starts_with":
                case "not_starts_with":
                case "ends_with":
                case "not_ends_with":
                    var ps = prop as string;
                    var ws = want as string;
                    if (ps == null || ws == null) return false;
                    if (op == "icontains" || op == "not_icontains") return ps.ToLowerInvariant().Contains(ws.ToLowerInvariant()) == (op == "icontains");
                    if (op == "starts_with" || op == "not_starts_with") return ps.StartsWith(ws, StringComparison.Ordinal) == (op == "starts_with");
                    return ps.EndsWith(ws, StringComparison.Ordinal) == (op == "ends_with");
                case "between":
                case "not_between":
                    var x = FlagOps.ToFloatPublic(prop);
                    var list = want as IList<object>;
                    if (x == null || list == null || list.Count != 2 || !FlagOps.IsNum(list[0]) || !FlagOps.IsNum(list[1])) return false;
                    var lo2 = Convert.ToDouble(list[0], CultureInfo.InvariantCulture);
                    var hi2 = Convert.ToDouble(list[1], CultureInfo.InvariantCulture);
                    return (x.Value >= lo2 && x.Value <= hi2) == (op == "between");
                case "is_date_before":
                case "is_date_after":
                    var p = Instant(prop, now, false);
                    var w = Instant(want, now, true);
                    if (p == null || w == null) return false;
                    return op == "is_date_before" ? p.Value < w.Value : p.Value > w.Value;
                case "semver_eq":
                case "semver_neq":
                case "semver_gt":
                case "semver_gte":
                case "semver_lt":
                case "semver_lte":
                    var c = FlagOps.CmpSemVerPublic(prop, want);
                    if (c == null) return false;
                    switch (op)
                    {
                        case "semver_eq": return c.Value == 0;
                        case "semver_neq": return c.Value != 0;
                        case "semver_gt": return c.Value > 0;
                        case "semver_gte": return c.Value >= 0;
                        case "semver_lt": return c.Value < 0;
                        default: return c.Value <= 0;
                    }
                case "semver_tilde":
                case "semver_caret":
                case "semver_wildcard":
                    var v = FlagOps.SemVerParts(prop);
                    List<int> lo, hi;
                    return v != null && Range(op, want, out lo, out hi) && CmpParts(v, lo) >= 0 && CmpParts(v, hi) < 0;
                default:
                    return false;
            }
        }
    }
}
