using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Lucida.Tests
{
    /// <summary>Wireframe format and replay recorder gates over the engine-free core (fakes from CoreTests.cs).</summary>
    public class ReplayTests
    {
        sealed class FakeSource : IWireframeSource
        {
            public WireSnapshot Snap;
            public int Reads;
            public WireSnapshot Capture() { Reads++; return Snap; }
        }

        ManualClock clock;
        FakeHttp http;
        FakeSource source;

        static WireSnapshot Screen(string name, string label = "Play now", string typed = "secret 1")
        {
            var root = new WireNode { Kind = WireKind.Container, Width = 400, Height = 800, Name = "Canvas" };
            root.Children.Add(new WireNode { Kind = WireKind.Text, X = 10, Y = 20, Width = 200, Height = 30, Text = label });
            var panel = new WireNode { Kind = WireKind.Container, X = 0, Y = 100, Width = 400, Height = 300, Name = "Panel" };
            panel.Children.Add(new WireNode { Kind = WireKind.Input, X = 10, Y = 10, Width = 300, Height = 40, Text = typed });
            panel.Children.Add(new WireNode { Kind = WireKind.Input, X = 10, Y = 60, Width = 300, Height = 40, Text = "hunter2", Password = true, Unmask = true });
            panel.Children.Add(new WireNode { Kind = WireKind.Image, X = 10, Y = 110, Width = 64, Height = 64 });
            var blocked = new WireNode { Kind = WireKind.Container, X = 100, Y = 110, Width = 64, Height = 64, Block = true };
            blocked.Children.Add(new WireNode { Kind = WireKind.Text, Text = "card 4242" });
            panel.Children.Add(blocked);
            root.Children.Add(panel);
            return new WireSnapshot { Root = root, Screen = name, Width = 400, Height = 800 };
        }

        LucidaCore NewCore(ConsentMode mode = ConsentMode.OptOut)
        {
            clock = new ManualClock();
            http = new FakeHttp();
            return new LucidaCore(new CoreConfig
            {
                ApiKey = "lc_pk_x", ApiHost = "https://h.example/", Http = http, Store = new MemoryStore(), Clock = clock,
                ConsentMode = mode, Random = () => 0.5,
            });
        }

        ReplayRecorder NewRecorder(LucidaCore lc, ReplayOptions o = null)
        {
            source = new FakeSource { Snap = Screen("Lobby") };
            return new ReplayRecorder(lc, source, o);
        }

        List<HttpRequest> ReplayRequests() { return http.Requests.Where(r => r.Url.Contains("/v1/replay")).ToList(); }

        static Dictionary<string, object> Body(HttpRequest r) { return Json.TryParseObject(r.Body); }

        static IEnumerable<Dictionary<string, object>> Walk(Dictionary<string, object> n)
        {
            yield return n;
            object kids;
            if (n.TryGetValue("childNodes", out kids))
                foreach (var k in (List<object>)kids)
                    foreach (var d in Walk((Dictionary<string, object>)k)) yield return d;
        }

        static List<Dictionary<string, object>> Nodes(List<object> events)
        {
            var full = (Dictionary<string, object>)events.Cast<Dictionary<string, object>>().First(e => (double)e["type"] == 2)["data"];
            return Walk((Dictionary<string, object>)full["node"]).ToList();
        }

        static Dictionary<string, object> Attrs(Dictionary<string, object> n) { return (Dictionary<string, object>)n["attributes"]; }

        [Test]
        public void StarsKeepWhitespace()
        {
            Assert.AreEqual("**** ***\n*", WireframeFrame.Stars("Play now\nx"));
        }

        [Test]
        public void SnapshotHasTheMobileRrwebShapeAndMasksByDefault()
        {
            var lc = NewCore();
            var rec = NewRecorder(lc);
            rec.Tick();
            var reqs = ReplayRequests();
            Assert.AreEqual(1, reqs.Count);
            Assert.AreEqual("https://h.example/v1/replay?compression=gzip", reqs[0].Url);
            Assert.IsTrue(reqs[0].Gzip);
            var b = Body(reqs[0]);
            Assert.AreEqual("lc_pk_x", b["api_key"]);
            Assert.AreEqual(lc.SessionId, b["session_id"]);
            Assert.AreEqual(rec.WindowId, b["window_id"]);
            Assert.AreEqual(0.0, b["seq"]);
            var events = (List<object>)b["events"];
            var types = events.Cast<Dictionary<string, object>>().Select(e => (double)e["type"]).ToList();
            CollectionAssert.AreEqual(new[] { 4.0, 5.0, 2.0 }, types, "meta, navigation, full snapshot");
            var meta = (Dictionary<string, object>)((Dictionary<string, object>)events[0])["data"];
            Assert.AreEqual("app://Lobby", meta["href"]);
            Assert.AreEqual(400.0, meta["width"]);

            var nodes = Nodes(events);
            var body = nodes.First(n => n.ContainsKey("tagName") && (string)n["tagName"] == "body");
            Assert.AreEqual(5.0, body["id"]);
            var texts = nodes.Where(n => (double)n["type"] == 3).Select(n => (string)n["textContent"]).ToList();
            CollectionAssert.AreEqual(new[] { "**** ***" }, texts, "text starred; blocked subtree never serialised");
            var inputs = nodes.Where(n => n.ContainsKey("tagName") && (string)n["tagName"] == "input").ToList();
            Assert.AreEqual("****** *", Attrs(inputs[0])["value"]);
            Assert.AreEqual("*******", Attrs(inputs[1])["value"], "password stays masked even when unmasked");
            Assert.AreEqual("password", Attrs(inputs[1])["type"]);
            var img = nodes.First(n => n.ContainsKey("attributes") && Attrs(n).ContainsKey("data-lc-image"));
            Assert.IsTrue(Attrs(img).ContainsKey("data-lc-mask"));
            Assert.IsTrue(nodes.Any(n => n.ContainsKey("attributes") && Attrs(n).ContainsKey("data-lc-block")));
            Assert.IsFalse(reqs[0].Body.Contains("secret") || reqs[0].Body.Contains("hunter2") || reqs[0].Body.Contains("4242") || reqs[0].Body.Contains("Play"));
        }

        [Test]
        public void UnmaskedTextIsSentWhenTheAppOptsOut()
        {
            var lc = NewCore();
            var rec = NewRecorder(lc, new ReplayOptions { Mask = new WireMask { Text = false, Inputs = false } });
            rec.Tick();
            var body = ReplayRequests()[0].Body;
            StringAssert.Contains("Play now", body);
            StringAssert.Contains("secret 1", body);
            Assert.IsFalse(body.Contains("hunter2"), "passwords are never unmasked");
        }

        [Test]
        public void NothingIsReadBeforeAnalyticsAndReplayConsent()
        {
            var lc = NewCore(ConsentMode.OptIn);
            var rec = NewRecorder(lc);
            rec.Tick();
            Assert.AreEqual(0, source.Reads);
            lc.SetConsent(new Dictionary<string, bool> { { "analytics", true } });
            clock.Now += 2000;
            rec.Tick();
            Assert.AreEqual(0, source.Reads, "replay consent is separate");
            lc.SetConsent(new Dictionary<string, bool> { { "replay", true } });
            clock.Now += 2000;
            rec.Tick();
            Assert.AreEqual(1, source.Reads);
            Assert.AreEqual(1, ReplayRequests().Count);
        }

        [Test]
        public void UnchangedUiIsNotResentAndChangesAre()
        {
            var lc = NewCore();
            var rec = NewRecorder(lc);
            rec.Tick();
            clock.Now += 500;
            rec.Tick();
            Assert.AreEqual(1, source.Reads, "throttled to the snapshot interval");
            clock.Now += 1000;
            rec.Tick();
            Assert.AreEqual(1, ReplayRequests().Count, "same structure: nothing sent");
            source.Snap = Screen("Lobby", "Play later");
            clock.Now += 1000;
            rec.Tick();
            var reqs = ReplayRequests();
            Assert.AreEqual(2, reqs.Count);
            var b = Body(reqs[1]);
            Assert.AreEqual(1.0, b["seq"]);
            var types = ((List<object>)b["events"]).Cast<Dictionary<string, object>>().Select(e => (double)e["type"]).ToList();
            CollectionAssert.AreEqual(new[] { 4.0, 2.0 }, types, "same screen: no navigation event");
            source.Snap = Screen("Shop");
            clock.Now += 1000;
            rec.Tick();
            types = ((List<object>)Body(ReplayRequests()[2])["events"]).Cast<Dictionary<string, object>>().Select(e => (double)e["type"]).ToList();
            CollectionAssert.AreEqual(new[] { 4.0, 5.0, 2.0 }, types);
        }

        [Test]
        public void TapsHitTheDeepestNodeAndRideTheNextChunk()
        {
            var lc = NewCore();
            var rec = NewRecorder(lc);
            rec.Tap(5, 5);
            rec.Tick();
            rec.Tap(20, 115); // inside the first input (absolute 10..310, 110..150)
            clock.Now += 1000;
            rec.Tick();
            var reqs = ReplayRequests();
            Assert.AreEqual(2, reqs.Count, "a tap alone is still sent");
            var events = ((List<object>)Body(reqs[1])["events"]).Cast<Dictionary<string, object>>().ToList();
            Assert.AreEqual(1, events.Count, "the tap before any snapshot was dropped");
            var data = (Dictionary<string, object>)events[0]["data"];
            Assert.AreEqual(2.0, data["source"]);
            Assert.AreEqual(2.0, data["type"]);
            var input = Nodes((List<object>)Body(reqs[0])["events"]).First(n => n.ContainsKey("tagName") && (string)n["tagName"] == "input");
            Assert.AreEqual(input["id"], data["id"]);
        }

        [Test]
        public void FailedSendResendsAFullSnapshotAndPauseAndSamplingStopCapture()
        {
            var lc = NewCore();
            var rec = NewRecorder(lc, new ReplayOptions { UseRemoteConfig = false }); // the first scripted answer is for the replay POST
            http.Script.Enqueue(new HttpResult(503));
            rec.Tick();
            clock.Now += 1000;
            rec.Tick();
            Assert.AreEqual(2, ReplayRequests().Count, "unchanged UI resent after a failure");
            rec.Paused = true;
            clock.Now += 1000;
            rec.Tick();
            Assert.AreEqual(2, source.Reads);

            var off = NewRecorder(NewCore(), new ReplayOptions { SampleRate = 0 });
            off.Tick();
            Assert.AreEqual(0, ReplayRequests().Count);
        }

        [Test]
        public void RemoteConfigCanOnlyTightenAndAKillSwitchStopsRecording()
        {
            var lc = NewCore();
            http.Script.Enqueue(new HttpResult(200, "{\"session_recording\":false}"));
            var rec = NewRecorder(lc);
            rec.Tick();
            Assert.AreEqual(0, ReplayRequests().Count, "killed project sends nothing");
            Assert.AreEqual(1, http.Requests.Count(r => r.Url.Contains("/v1/config?api_key=lc_pk_x")));
            clock.Now += 1000;
            rec.Tick();
            Assert.AreEqual(1, http.Requests.Count(r => r.Url.Contains("/v1/config")), "config is fetched once");
        }

        [Test]
        public void MinDurationHoldsChunksAndLinkedFlagAndScreensGate()
        {
            var lc = NewCore();
            var rec = NewRecorder(lc, new ReplayOptions { UseRemoteConfig = false, MinDurationMs = 3000 });
            rec.Tick();
            Assert.AreEqual(0, ReplayRequests().Count, "held until the session was recorded long enough");
            source.Snap = Screen("Lobby", "Changed");
            clock.Now += 4000;
            rec.Tick();
            Assert.AreEqual(2, ReplayRequests().Count, "held chunk and the new one go out together");

            var flagOn = false;
            var linked = new ReplayRecorder(NewCore(), new FakeSource { Snap = Screen("Lobby") }, new ReplayOptions { UseRemoteConfig = false, LinkedFlag = "rec" }, k => flagOn);
            linked.Tick();
            Assert.AreEqual(0, ReplayRequests().Count, "flag off: nothing recorded");
            flagOn = true;
            clock.Now += 1000;
            linked.Tick();
            Assert.AreEqual(1, ReplayRequests().Count);

            var denied = NewRecorder(NewCore(), new ReplayOptions { UseRemoteConfig = false, ScreenDenyList = new[] { "Lobby" } });
            denied.Tick();
            Assert.AreEqual(0, ReplayRequests().Count, "deny list wins");
            var notAllowed = NewRecorder(NewCore(), new ReplayOptions { UseRemoteConfig = false, ScreenAllowList = new[] { "Shop" } });
            notAllowed.Tick();
            Assert.AreEqual(0, ReplayRequests().Count);
        }

        [Test]
        public void SourceExceptionsNeverEscape()
        {
            var lc = NewCore();
            var rec = new ReplayRecorder(lc, new ThrowingSource());
            Assert.DoesNotThrow(() => rec.Tick());
            Assert.AreEqual(0, ReplayRequests().Count);
        }

        sealed class ThrowingSource : IWireframeSource
        {
            public WireSnapshot Capture() { throw new System.InvalidOperationException("walk failed"); }
        }
    }
}
