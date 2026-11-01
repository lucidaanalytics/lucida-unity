using System;
using System.Collections.Generic;

namespace Lucida
{
    /// <summary>Port: reads the current UI into a <see cref="WireSnapshot"/> (null = nothing to record).</summary>
    public interface IWireframeSource
    {
        WireSnapshot Capture();
    }

    public sealed class ReplayOptions
    {
        public WireMask Mask = new WireMask();
        /// <summary>0..1, decided per session from the session id.</summary>
        public double SampleRate = 1.0;
        /// <summary>Minimum time between captures; the driver may tick more often.</summary>
        public long SnapshotIntervalMs = 1000;
        /// <summary>Largest chunk sent (the gateway accepts 10 MB decompressed).</summary>
        public int MaxBytes = 5_000_000;
        /// <summary>Record tap positions (never what was typed).</summary>
        public bool CaptureTouches = true;
    }

    /// <summary>
    /// Wireframe session replay for Unity (LC-EXP-57). Each <see cref="Tick"/> (throttled to
    /// <see cref="ReplayOptions.SnapshotIntervalMs"/>) reads the UI through <see cref="IWireframeSource"/>; when the
    /// structure changed (or the session rotated) it sends Meta + FullSnapshot, preceded by a <c>navigation</c> event
    /// on a screen change. Chunks go to <c>POST /v1/replay?compression=gzip</c> as
    /// <c>{api_key, session_id, window_id, seq, events}</c>; <c>seq</c> is monotonic per window and resets when the session rotates.
    /// Needs analytics AND replay consent: the UI is not read before both are granted.
    /// </summary>
    public sealed class ReplayRecorder
    {
        const int MaxTouches = 500;
        readonly LucidaCore core;
        readonly IWireframeSource source;
        readonly ReplayOptions options;
        readonly List<Dictionary<string, object>> touches = new List<Dictionary<string, object>>();
        WireframeFrame lastFrame;
        string lastFingerprint, lastScreen, lastSession;
        long lastCaptureAt = long.MinValue;
        int seq;

        /// <summary>In memory only: one window per app run.</summary>
        public readonly string WindowId;
        public bool Paused;

        public ReplayRecorder(LucidaCore core, IWireframeSource source, ReplayOptions options = null)
        {
            this.core = core;
            this.source = source;
            this.options = options ?? new ReplayOptions();
            WindowId = Uuid.V7(core.Config.Clock.NowMs());
        }

        bool Consented { get { return core.Consent.Has("analytics") && core.Consent.Has("replay"); } }

        bool Sampled(string sessionId)
        {
            var r = options.SampleRate;
            if (r >= 1) return true;
            if (r <= 0) return false;
            return FlagOps.Hash01("replay." + sessionId) < r;
        }

        void Forget()
        {
            lastFingerprint = null;
            lastFrame = null;
            touches.Clear();
        }

        /// <summary>A tap at screen point (x, y), top-left origin; needs consent and a sent snapshot.</summary>
        public void Tap(float x, float y)
        {
            if (!options.CaptureTouches || lastFrame == null || Paused || !Consented) return;
            if (touches.Count < MaxTouches) touches.Add(lastFrame.Tap(x, y, core.Config.Clock.NowMs()));
        }

        /// <summary>One capture pass. The Unity driver calls it every frame; it returns at once until the interval passed.</summary>
        public void Tick()
        {
            if (!Consented)
            {
                Forget(); // revoking drops state so a re-grant starts from a fresh snapshot
                return;
            }
            var now = core.Config.Clock.NowMs();
            if (lastCaptureAt != long.MinValue && now - lastCaptureAt < options.SnapshotIntervalMs) return;
            lastCaptureAt = now;
            if (Paused) { Forget(); return; }
            var sid = core.Session.Touch();
            if (!Sampled(sid)) return;
            WireSnapshot snap;
            try { snap = source.Capture(); }
            catch (Exception) { return; } // a failing walk must never affect the game
            if (snap == null || snap.Root == null) return;
            var rotated = sid != lastSession;
            if (rotated)
            {
                seq = 0;
                lastSession = sid;
            }
            var events = new List<Dictionary<string, object>>(touches);
            touches.Clear();
            var frame = WireframeFrame.Build(snap, now, options.Mask);
            // Fingerprint what would be sent, with a fixed timestamp, so only real changes resend.
            var fp = snap.Screen + "|" + Json.Serialize(WireframeFrame.Build(snap, 0, options.Mask).Events);
            if (rotated || fp != lastFingerprint)
            {
                lastFingerprint = fp;
                events.Add(WireframeFrame.Meta(snap, now));
                if (snap.Screen != lastScreen)
                {
                    events.Add(WireframeFrame.Navigation(snap, now));
                    lastScreen = snap.Screen;
                }
                events.AddRange(frame.Events);
                lastFrame = frame;
            }
            if (events.Count > 0) Send(events, sid);
        }

        void Send(List<Dictionary<string, object>> events, string sid)
        {
            var body = Json.Serialize(new Dictionary<string, object>
            {
                { "api_key", core.Config.ApiKey }, { "session_id", sid }, { "window_id", WindowId }, { "seq", seq++ }, { "events", events },
            });
            if (body.Length > options.MaxBytes) return;
            var req = new HttpRequest { Method = "POST", Url = core.Host + "/v1/replay?compression=gzip", Body = body, Gzip = true };
            req.Headers["Content-Type"] = "application/json";
            // seq advances at send time: the HTTP port may answer later, after the next chunk went out.
            core.Config.Http.Send(req, r =>
            {
                if (r.Status < 200 || r.Status > 299) lastFingerprint = null; // resend as a fresh snapshot next tick
            });
        }
    }
}
