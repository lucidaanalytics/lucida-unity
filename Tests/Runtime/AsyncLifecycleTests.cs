using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Lucida.Tests
{
    /// <summary>Task API and lifecycle rules over the engine-free core (FakeHttp, ManualClock, MemoryStore from CoreTests.cs).</summary>
    public class AsyncLifecycleTests
    {
        ManualClock clock;
        FakeHttp http;

        LucidaCore NewCore(int flushAt = 20)
        {
            clock = new ManualClock();
            http = new FakeHttp();
            return new LucidaCore(new CoreConfig
            {
                ApiKey = "lc_pk_x", ApiHost = "https://h.example/", Http = http, Store = new MemoryStore(), Clock = clock,
                ConsentMode = ConsentMode.OptOut, FlushAt = flushAt, Random = () => 0.5,
            });
        }

        [Test]
        public void FlushAsyncCompletesTrueWhenAcknowledged()
        {
            var lc = NewCore();
            lc.Capture("a");
            var t = lc.FlushAsync();
            Assert.IsTrue(t.IsCompleted);
            Assert.IsTrue(t.Result);
            Assert.AreEqual(0, lc.Queue.Count);
            Assert.IsTrue(lc.FlushAsync().Result, "nothing queued completes at once");
        }

        [Test]
        public void FlushAsyncReportsFalseWhileRetryingThenTrue()
        {
            var lc = NewCore();
            http.Script.Enqueue(new HttpResult(503));
            lc.Capture("a");
            Assert.IsFalse(lc.FlushAsync().Result, "503 leaves the batch queued for backoff");
            Assert.AreEqual(1, lc.Queue.Count);
            Assert.IsTrue(lc.FlushAsync().Result, "the retry succeeds");
        }

        [Test]
        public void CaptureAsyncDeliversTheEvent()
        {
            var lc = NewCore();
            Assert.IsTrue(lc.CaptureAsync("played", new Dictionary<string, object> { { "level", 3 } }).Result);
            Assert.AreEqual("played", (string)http.Events()[0]["event"]);
        }

        [Test]
        public void FlagTasksReloadThenRead()
        {
            var lc = NewCore();
            http.Script.Enqueue(new HttpResult(200, "{\"flags\":{\"boss\":{\"value\":\"hard\"}},\"request_id\":\"r\"}"));
            var flags = new Flags(lc);
            Assert.AreEqual("hard", flags.GetFlagAsync("boss").Result);
        }

        [Test]
        public void LifecycleOpensBackgroundsAndFlushesOnce()
        {
            var lc = NewCore();
            var life = new Lifecycle(lc);
            life.Start();
            life.OnPause(true);
            life.OnFocus(false); // second half of the same mobile transition: no duplicate
            Assert.AreEqual(1, http.Requests.Count, "backgrounding flushes");
            life.OnFocus(true);
            life.OnPause(false);
            life.OnQuit();
            var names = http.Events().ConvertAll(e => (string)e["event"]);
            Assert.AreEqual(new[] { "$app_opened", "$app_backgrounded", "$app_opened", "$app_backgrounded" }, names.ToArray());
            Assert.AreEqual(2, http.Requests.Count, "one flush on background, one on quit");
        }
    }
}
