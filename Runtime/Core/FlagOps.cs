using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucida
{
    /// <summary>Operator semantics of testdata/flags/README.md. Values are JSON-shaped (double, string, bool, list, null).</summary>
    public static class FlagOps
    {
        const double Unit = 4503599627370496.0; // 16^13

        /// <summary>sha256(text)[0..13) hex digits as a fraction of 16^13.</summary>
        public static double Hash01(string text)
        {
            byte[] h;
            using (var sha = SHA256.Create()) h = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            ulong n = 0;
            for (var i = 0; i < 7; i++) n = (n << 8) | h[i];
            return (n >> 4) / Unit;
        }

        public static bool IsNum(object o)
        {
            return o is double || o is int || o is long || o is float || o is decimal || o is short || o is byte;
        }

        static double Dbl(object o) { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }

        static bool Equal(object a, object b)
        {
            if (IsNum(a) || IsNum(b)) return IsNum(a) && IsNum(b) && Dbl(a) == Dbl(b);
            return Equals(a, b);
        }

        public static double? ToFloatPublic(object v) { return ToFloat(v); }

        static double? ToFloat(object v)
        {
            if (IsNum(v)) return Dbl(v);
            var s = v as string;
            double d;
            if (s != null && s.IndexOf('_') < 0 &&
                double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) &&
                !double.IsNaN(d) && !double.IsInfinity(d)) return d;
            return null;
        }

        public static List<int> SemVerParts(object v) { return SemVer(v); }

        static List<int> SemVer(object v)
        {
            var str = v as string;
            if (str == null) return null;
            var s = str.Trim();
            if (s.StartsWith("v", StringComparison.Ordinal)) s = s.Substring(1);
            var cut = s.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0) s = s.Substring(0, cut);
            var parts = s.Split('.');
            var res = new List<int>();
            foreach (var p in parts)
            {
                int n;
                if (p.Length == 0 || p.Length > 9 || !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return null;
                res.Add(n);
            }
            return res;
        }

        public static int? CmpSemVerPublic(object a, object b) { return CmpSemVer(a, b); }

        static int? CmpSemVer(object a, object b)
        {
            var pa = SemVer(a);
            var pb = SemVer(b);
            if (pa == null || pb == null) return null;
            var n = Math.Max(pa.Count, pb.Count);
            for (var i = 0; i < n; i++)
            {
                var x = i < pa.Count ? pa[i] : 0;
                var y = i < pb.Count ? pb[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        static int? CmpNum(object a, object b)
        {
            var x = ToFloat(a);
            var y = ToFloat(b);
            if (x == null || y == null) return null;
            return x.Value < y.Value ? -1 : x.Value > y.Value ? 1 : 0;
        }

        static bool? RegexMatch(string pattern, string s)
        {
            try { return Regex.IsMatch(s, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { return null; }
            catch (RegexMatchTimeoutException) { return null; }
        }

        /// <summary>A missing or null property matches only is_not_set; every other op is false.</summary>
        public static bool Match(string op, object prop, bool present, object want, bool semver, double now = double.NaN)
        {
            var isSet = present && prop != null;
            if (op == "is_set") return isSet;
            if (op == "is_not_set") return !isSet;
            if (!isSet) return false;
            switch (op)
            {
                case "eq":
                case "neq":
                    if (semver)
                    {
                        var c = CmpSemVer(prop, want);
                        return c != null && ((c.Value == 0) == (op == "eq"));
                    }
                    return Equal(prop, want) == (op == "eq");
                case "contains":
                case "ncontains":
                case "regex":
                case "nregex":
                    var ps = prop as string;
                    var ws = want as string;
                    if (ps == null || ws == null) return false;
                    if (op == "contains" || op == "ncontains") return ps.Contains(ws) == (op == "contains");
                    var r = RegexMatch(ws, ps);
                    return r != null && (r.Value == (op == "regex"));
                case "gt":
                case "gte":
                case "lt":
                case "lte":
                    var k = semver ? CmpSemVer(prop, want) : CmpNum(prop, want);
                    if (k == null) return false;
                    if (op == "gt") return k.Value > 0;
                    if (op == "gte") return k.Value >= 0;
                    if (op == "lt") return k.Value < 0;
                    return k.Value <= 0;
                case "in":
                case "not_in":
                    var list = want as IList<object>;
                    if (list == null) return false;
                    var found = false;
                    foreach (var w in list) if (Equal(prop, w)) { found = true; break; }
                    return found == (op == "in");
                default:
                    return FlagOpsV2.Match(op, prop, want, double.IsNaN(now) ? FlagOpsV2.WallClock() : now);
            }
        }
    }
}
