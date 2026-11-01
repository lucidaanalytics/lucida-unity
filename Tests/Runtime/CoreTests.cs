using System.Collections.Generic;
using NUnit.Framework;

namespace Lucida.Tests
{
    sealed class ManualClock : IClock
    {
        public long Now = 1_700_000_000_000;
        public long NowMs() { return Now; }
    }

    /// <summary>Scripted IHttp: answers each request with the next queued status (default 200) synchronously.</summary>
    sealed class FakeHttp : IHttp
    {
        public readonly List<HttpRequest> Requests = new List<HttpRequest>();
        public readonly Queue<HttpResult> Script = new Queue<HttpResult>();

        public void Send(HttpRequest request, System.Action<HttpResult> done)
        {
            Requests.Add(request);
            done(Script.Count > 0 ? Script.Dequeue() : new HttpResult(200, "{}"));
        }

        public List<Dictionary<string, object>> Events()
        {
            var all = new List<Dictionary<string, object>>();
            foreach (var r in Requests)
            {
                if (!r.Url.Contains("/v1/batch")) continue;
                foreach (var e in (List<object>)Json.TryParseObject(r.Body)["batch"]) all.Add((Dictionary<string, object>)e);
            }
            return all;
        }
    }

    public class CoreTests
    {
        ManualClock clock;
        FakeHttp http;
        MemoryStore store;

        [SetUp]
        public void SetUp()
        {
            clock = new ManualClock();
            http = new FakeHttp();
            store = new MemoryStore();
        }

        LucidaCore NewCore(ConsentMode mode = ConsentMode.OptOut, int flushAt = 20, bool queueBefore = false)
        {
            return new LucidaCore(new CoreConfig
            {
                ApiKey = "lc_pk_x", ApiHost = "https://h.example/", Http = http, Store = store, Clock = clock,
                ConsentMode = mode, QueueBeforeConsent = queueBefore, FlushAt = flushAt, Random = () => 0.5,
            });
        }

        [Test]
        public void JsonRoundTrips()
        {
            var o = Json.TryParseObject("{\"a\":[1,2.5,\"x\\n\\u0041\"],\"b\":null,\"c\":true,\"d\":{\"e\":-3e2}}");
            Assert.AreEqual("{\"a\":[1,2.5,\"x\\nA\"],\"b\":null,\"c\":true,\"d\":{\"e\":-300}}", Json.Serialize(o));
            Assert.IsNull(Json.TryParseObject("{bad"));
        }

        [Test]
        public void CaptureBuildsWireEventAndBatchesAtFlushAt()
        {
            var lc = NewCore(flushAt: 2);
            lc.Capture("a", new Dictionary<string, object> { { "k", 1 } });
            Assert.AreEqual(0, http.Requests.Count);
            lc.Capture("b");
            Assert.AreEqual(1, http.Requests.Count);
            Assert.AreEqual("https://h.example/v1/batch?compression=gzip", http.Requests[0].Url);
            Assert.IsTrue(http.Requests[0].Gzip);
            var body = Json.TryParseObject(http.Requests[0].Body);
            Assert.AreEqual("lc_pk_x", body["api_key"]);
            var ev = http.Events();
            Assert.AreEqual(2, ev.Count);
            Assert.AreNotEqual(ev[0]["uuid"], ev[1]["uuid"]);
            Assert.AreEqual(lc.DistinctId, ev[0]["distinct_id"]);
            var props = (Dictionary<string, object>)ev[0]["properties"];
            Assert.AreEqual("lucida-unity", props["$lib"]);
            Assert.AreEqual(1.0, props["k"]);
            Assert.AreEqual(0, lc.Queue.Count);
        }

        [Test]
        public void RetriesRetryableFailureWithBackoffAndKeepsUuid()
        {
            var lc = NewCore(flushAt: 1);
            http.Script.Enqueue(new HttpResult(503));
            lc.Capture("a");
            Assert.AreEqual(1, lc.Queue.Count);
            lc.Tick();
            Assert.AreEqual(1, http.Requests.Count, "backoff not elapsed");
            clock.Now += 60_000;
            lc.Tick();
            Assert.AreEqual(2, http.Requests.Count);
            Assert.AreEqual(0, lc.Queue.Count);
            var ev = http.Events();
            Assert.AreEqual(ev[0]["uuid"], ev[1]["uuid"]);
        }

        [Test]
        public void NonRetryableStatusDropsBatch()
        {
            var dropped = 0;
            var lc = new LucidaCore(new CoreConfig
            {
                ApiKey = "k", Http = http, Store = store, Clock = clock, FlushAt = 1,
                OnDrop = (items, reason) => dropped += items.Count,
            });
            http.Script.Enqueue(new HttpResult(400));
            lc.Capture("a");
            Assert.AreEqual(0, lc.Queue.Count);
            Assert.AreEqual(1, dropped);
        }

        [Test]
        public void TickFlushesAfterInterval()
        {
            var lc = NewCore();
            lc.Capture("a");
            lc.Tick();
            Assert.AreEqual(0, http.Requests.Count);
            clock.Now += 5000;
            lc.Tick();
            Assert.AreEqual(1, http.Requests.Count);
        }

        [Test]
        public void QueueSurvivesRestartUntilAcknowledged()
        {
            var lc = NewCore();
            lc.Capture("a");
            var lc2 = NewCore();
            Assert.AreEqual(1, lc2.Queue.Count);
            lc2.Flush();
            Assert.AreEqual(0, lc2.Queue.Count);
            Assert.IsNull(store.Get("lc_queue"));
        }

        [Test]
        public void OptInProjectWritesNothingBeforeConsent()
        {
            var lc = NewCore(ConsentMode.OptIn);
            lc.Identify("user-1");
            Assert.IsNull(lc.Capture("a"));
            lc.Group("company", "acme");
            Assert.AreEqual(0, store.Data.Count, "no storage before consent");
            lc.OptIn();
            Assert.IsNotNull(store.Get("lc_identity"));
            Assert.IsNotNull(store.Get("lc_consent"));
            Assert.AreEqual("user-1", lc.DistinctId);
        }

        [Test]
        public void QueueBeforeConsentHoldsEventsInMemoryThenReleasesThem()
        {
            var lc = NewCore(ConsentMode.OptIn, queueBefore: true);
            lc.Capture("early");
            lc.Flush();
            Assert.AreEqual(0, http.Requests.Count);
            Assert.IsNull(store.Get("lc_queue"));
            lc.OptIn();
            lc.Flush();
            var names = new List<object>();
            foreach (var e in http.Events()) names.Add(e["event"]);
            CollectionAssert.AreEqual(new object[] { "early", "$opt_in" }, names);
        }

        [Test]
        public void OptOutSendsOptOutThenClearsQueue()
        {
            var lc = NewCore();
            lc.Capture("a");
            lc.OptOut();
            var names = new List<object>();
            foreach (var e in http.Events()) names.Add(e["event"]);
            CollectionAssert.AreEqual(new object[] { "a", "$opt_out" }, names);
            Assert.IsNull(store.Get("lc_queue"));
            Assert.IsNull(lc.Capture("after"));
        }

        [Test]
        public void PriorAnonIdIsSetOnlyAfterIdentify()
        {
            var lc = NewCore();
            Assert.IsNull(lc.Identity.PriorAnonId);
            var anon = lc.DistinctId;
            lc.Identify("u1");
            Assert.AreEqual(anon, lc.Identity.PriorAnonId);
        }

        [Test]
        public void IdentifyLinksAnonymousIdOnce()
        {
            var lc = NewCore();
            var anon = lc.DistinctId;
            lc.Identify("u1");
            lc.Identify("u1");
            lc.Flush();
            var ev = http.Events();
            Assert.AreEqual(1, ev.Count);
            Assert.AreEqual("$identify", ev[0]["event"]);
            Assert.AreEqual("u1", ev[0]["distinct_id"]);
            Assert.AreEqual(anon, ((Dictionary<string, object>)ev[0]["properties"])["$anon_distinct_id"]);
        }

        [Test]
        public void SessionIdMatchesEventsAndRotatesAfterIdle()
        {
            var lc = NewCore();
            var sid = lc.SessionId;
            Assert.AreEqual(sid, ((Dictionary<string, object>)lc.Capture("e")["properties"])["$session_id"]);
            clock.Now += 29 * 60_000;
            Assert.AreEqual(sid, lc.SessionId);
            clock.Now += 31 * 60_000;
            Assert.AreNotEqual(sid, lc.SessionId);
            var before = lc.SessionId;
            lc.Reset();
            Assert.AreNotEqual(before, lc.SessionId);
        }

        [Test]
        public void SetPersonPropertiesSendsSetEventAndIsOptedOutTracksConsent()
        {
            var lc = NewCore();
            Assert.IsFalse(lc.IsOptedOut);
            lc.SetPersonProperties(new Dictionary<string, object> { { "plan", "pro" } });
            lc.SetPersonProperties();
            lc.Flush();
            var ev = http.Events();
            Assert.AreEqual(1, ev.Count, "empty call sends nothing");
            Assert.AreEqual("$set", ev[0]["event"]);
            Assert.AreEqual("pro", ((Dictionary<string, object>)ev[0]["$set"])["plan"]);
            lc.OptOut();
            Assert.IsTrue(lc.IsOptedOut);
        }

        [Test]
        public void RemoteFlagsApplyAndCache()
        {
            var lc = NewCore();
            http.Script.Enqueue(new HttpResult(200,
                "{\"request_id\":\"r1\",\"flags\":{\"a\":true,\"b\":{\"variant\":\"x\",\"payload\":{\"n\":1}},\"c\":{\"enabled\":false}}}"));
            var flags = new Flags(lc);
            var ok = false;
            flags.Reload(r => ok = r);
            Assert.IsTrue(ok);
            Assert.AreEqual("https://h.example/v1/flags", http.Requests[0].Url);
            Assert.AreEqual(true, flags.Get("a"));
            Assert.AreEqual("x", flags.Get("b"));
            Assert.IsFalse(flags.IsEnabled("c"));
            Assert.IsFalse(flags.IsEnabled("missing"));
            Assert.IsNotNull(flags.Payload("b"));
            Assert.AreEqual("r1", flags.RequestId);
            Assert.IsNotNull(store.Get("lc_flags"));
            Assert.AreEqual("x", new Flags(lc).Get("b"), "cold start reads the cache");
            lc.Flush();
            var called = http.Events().FindAll(e => (string)e["event"] == "$feature_flag_called");
            Assert.AreEqual(5, called.Count, "once per key and value across the instances: a, b, c, missing, b");
        }

        [Test]
        public void EventsCarryFlagValuesUnlessDisabled()
        {
            var lc = NewCore();
            var defs = (List<object>)Json.Parse(
                "[{\"key\":\"on\",\"type\":\"boolean\",\"active\":true,\"salt\":\"s\",\"aggregation\":{\"kind\":\"person\"},\"conditions\":[{\"rollout_pct\":100}]}," +
                "{\"key\":\"off\",\"type\":\"boolean\",\"active\":true,\"salt\":\"s\",\"aggregation\":{\"kind\":\"person\"},\"conditions\":[{\"rollout_pct\":0}]}]");
            var flags = new Flags(lc, defs);
            flags.Reload();
            var e = lc.Capture("clicked", new Dictionary<string, object> { { "$feature/on", "mine" } });
            var p = (Dictionary<string, object>)e["properties"];
            Assert.AreEqual("mine", p["$feature/on"], "the caller's property wins");
            Assert.AreEqual(false, p["$feature/off"]);
            Assert.AreEqual(new List<object> { "on" }, p["$active_feature_flags"]);
            var snap = (Dictionary<string, object>)lc.Capture("$snapshot")["properties"];
            Assert.IsFalse(snap.ContainsKey("$active_feature_flags"));

            var lc2 = NewCore();
            var quiet = new Flags(lc2, defs, null, null, false);
            quiet.Reload();
            Assert.IsFalse(((Dictionary<string, object>)lc2.Capture("clicked")["properties"]).ContainsKey("$active_feature_flags"));
        }

        [Test]
        public void EvaluationContextsDropOtherContextsFlags()
        {
            var lc = NewCore();
            var defs = (List<object>)Json.Parse(
                "[{\"key\":\"web\",\"type\":\"boolean\",\"active\":true,\"salt\":\"s\",\"app_contexts\":[\"web\"],\"aggregation\":{\"kind\":\"person\"},\"conditions\":[{\"rollout_pct\":100}]},"
                + "{\"key\":\"game\",\"type\":\"boolean\",\"active\":true,\"salt\":\"s\",\"app_contexts\":[\"game\"],\"aggregation\":{\"kind\":\"person\"},\"conditions\":[{\"rollout_pct\":100}]}]");
            var flags = new Flags(lc, defs, null, null, true, new List<string> { "game" });
            flags.Reload();
            Assert.IsTrue(flags.IsEnabled("game"));
            Assert.IsFalse(flags.IsEnabled("web"));
        }

        [Test]
        public void LocalDefinitionsEvaluateWithoutNetwork()
        {
            var lc = NewCore();
            var defs = (List<object>)Json.Parse(
                "[{\"key\":\"on\",\"type\":\"boolean\",\"active\":true,\"salt\":\"s\",\"aggregation\":{\"kind\":\"person\"},\"conditions\":[{\"rollout_pct\":100}]}]");
            var flags = new Flags(lc, defs);
            flags.Reload();
            Assert.AreEqual(0, http.Requests.Count);
            Assert.IsTrue(flags.IsEnabled("on"));
        }
    }
}
