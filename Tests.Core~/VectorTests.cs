using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Lucida.Tests
{
    /// <summary>Shared flag conformance vectors (testdata/flags); needs the repo checkout, so it lives outside the UPM package.</summary>
    public class VectorTests
    {
        // Context fields each `needs` entry lets the evaluator see.
        static readonly Dictionary<string, string[]> CtxFields = new Dictionary<string, string[]>
        {
            { "distinct_id", new[] { "distinct_id", "bucketing_key" } },
            { "groups", new[] { "groups" } },
            { "person_properties", new[] { "person_properties" } },
            { "group_properties", new[] { "group_properties" } },
            { "device_properties", new[] { "device_properties" } },
            { "geo_properties", new[] { "geo_properties" } },
            { "app_version", new[] { "app_version" } },
        };

        static string FlagsDir()
        {
            var env = Environment.GetEnvironmentVariable("LUCIDA_TESTDATA");
            if (!string.IsNullOrEmpty(env)) return Path.Combine(env, "flags");
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var p = Path.Combine(dir.FullName, "testdata", "flags");
                if (Directory.Exists(p)) return p;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("testdata/flags not found above " + AppContext.BaseDirectory);
        }

        static Dictionary<string, object> Supplied(Dictionary<string, object> vec)
        {
            var ctx = (Dictionary<string, object>)vec["context"];
            if (!vec.ContainsKey("needs")) return ctx;
            var keep = new HashSet<string>(((List<object>)vec["needs"]).SelectMany(n =>
            {
                string[] f;
                return CtxFields.TryGetValue((string)n, out f) ? f : new string[0];
            }));
            return ctx.Where(kv => keep.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        // Key-sorted serialization so payload comparison ignores object key order.
        static string Canon(object o)
        {
            var d = o as Dictionary<string, object>;
            if (d != null) return "{" + string.Join(",", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Json.Serialize(kv.Key) + ":" + Canon(kv.Value))) + "}";
            var l = o as List<object>;
            if (l != null) return "[" + string.Join(",", l.Select(Canon)) + "]";
            return Json.Serialize(o);
        }

        static int Check(string file)
        {
            var d = (Dictionary<string, object>)Json.Parse(File.ReadAllText(Path.Combine(FlagsDir(), file)));
            foreach (Dictionary<string, object> h in (List<object>)d["hash_vectors"])
                Assert.AreEqual((double)h["hash"], FlagOps.Hash01((string)h["input"]), (string)h["input"]);

            var flags = (List<object>)d["flags"];
            var vectors = (List<object>)d["vectors"];
            foreach (Dictionary<string, object> v in vectors)
            {
                var name = (string)v["name"];
                var e = (Dictionary<string, object>)v["expected"];
                var r = FlagEvaluator.EvaluateAll(flags, Supplied(v))[(string)v["flag"]];
                Assert.AreEqual(e["value"], r.Value, name + " value");
                Assert.AreEqual(e.ContainsKey("variant") ? e["variant"] : null, r.Variant, name + " variant");
                Assert.AreEqual(e.ContainsKey("payload") ? Canon(e["payload"]) : "null", Canon(r.Payload), name + " payload");
                Assert.AreEqual((string)e["reason"], r.Reason, name + " reason");
                Assert.AreEqual((int)(double)e["condition_index"], r.ConditionIndex, name + " condition_index");
            }
            return vectors.Count;
        }

        [Test] public void Basic() { Assert.Greater(Check("vectors_basic.json"), 20); }
        [Test] public void DeviceNamespaceHoldout() { Assert.Greater(Check("vectors_g10.json"), 15); }
        [Test] public void Sticky() { Assert.GreaterOrEqual(Check("vectors_sticky.json"), 19); }
        [Test] public void SpecV2Operators() { Assert.GreaterOrEqual(Check("vectors_ops.json"), 100); }
        [Test] public void Generated() { Assert.AreEqual(10000, Check("vectors.json")); }
    }
}
