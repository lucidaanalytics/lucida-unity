using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lucida
{
    /// <summary>
    /// Minimal JSON (UnityEngine.JsonUtility cannot do dictionaries). Objects are
    /// Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double, plus bool, string, null.
    /// </summary>
    public static class Json
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static object Parse(string s)
        {
            var p = new Parser(s);
            var v = p.Value();
            p.Ws();
            if (p.I != s.Length) throw new FormatException("trailing characters");
            return v;
        }

        /// <summary>Parses an object, or returns null for anything else / invalid input.</summary>
        public static Dictionary<string, object> TryParseObject(string s)
        {
            try { return Parse(s) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        public static string Serialize(object v)
        {
            var sb = new StringBuilder();
            Write(sb, v);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string str: WriteStr(sb, str); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double d: WriteNum(sb, d); break;
                case float f: WriteNum(sb, f); break;
                case int _: case long _: case short _: case byte _: case uint _: case ulong _: case ushort _: case sbyte _: case decimal _:
                    sb.Append(Convert.ToString(v, Inv));
                    break;
                case IDictionary dict:
                    sb.Append('{');
                    var first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteStr(sb, Convert.ToString(e.Key, Inv));
                        sb.Append(':');
                        Write(sb, e.Value);
                    }
                    sb.Append('}');
                    break;
                case IEnumerable en:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var x in en)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Write(sb, x);
                    }
                    sb.Append(']');
                    break;
                default: WriteStr(sb, Convert.ToString(v, Inv)); break;
            }
        }

        static void WriteNum(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
            else if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(Inv));
            else sb.Append(d.ToString("R", Inv));
        }

        static void WriteStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Parser
        {
            readonly string s;
            public int I;

            public Parser(string s) { this.s = s; }

            public void Ws()
            {
                while (I < s.Length && char.IsWhiteSpace(s[I])) I++;
            }

            public object Value()
            {
                Ws();
                if (I >= s.Length) throw new FormatException("unexpected end");
                switch (s[I])
                {
                    case '{': return Object();
                    case '[': return Array();
                    case '"': return Str();
                    case 't': Lit("true"); return true;
                    case 'f': Lit("false"); return false;
                    case 'n': Lit("null"); return null;
                    default: return Num();
                }
            }

            void Lit(string w)
            {
                if (string.CompareOrdinal(s, I, w, 0, w.Length) != 0) throw new FormatException("bad literal");
                I += w.Length;
            }

            object Num()
            {
                var start = I;
                while (I < s.Length && "+-0123456789.eE".IndexOf(s[I]) >= 0) I++;
                if (start == I) throw new FormatException("bad value");
                return double.Parse(s.Substring(start, I - start), NumberStyles.Float, Inv);
            }

            string Str()
            {
                if (s[I] != '"') throw new FormatException("expected string");
                I++;
                var sb = new StringBuilder();
                while (true)
                {
                    var c = s[I++];
                    if (c == '"') break;
                    if (c != '\\') { sb.Append(c); continue; }
                    var e = s[I++];
                    switch (e)
                    {
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            sb.Append((char)Convert.ToInt32(s.Substring(I, 4), 16));
                            I += 4;
                            break;
                        default: sb.Append(e); break; // " \ /
                    }
                }
                return sb.ToString();
            }

            Dictionary<string, object> Object()
            {
                I++;
                var d = new Dictionary<string, object>();
                Ws();
                if (s[I] == '}') { I++; return d; }
                while (true)
                {
                    Ws();
                    var key = Str();
                    Ws();
                    if (s[I++] != ':') throw new FormatException("expected :");
                    d[key] = Value();
                    Ws();
                    var c = s[I++];
                    if (c == '}') return d;
                    if (c != ',') throw new FormatException("expected , or }");
                }
            }

            List<object> Array()
            {
                I++;
                var l = new List<object>();
                Ws();
                if (s[I] == ']') { I++; return l; }
                while (true)
                {
                    l.Add(Value());
                    Ws();
                    var c = s[I++];
                    if (c == ']') return l;
                    if (c != ',') throw new FormatException("expected , or ]");
                }
            }
        }
    }
}
