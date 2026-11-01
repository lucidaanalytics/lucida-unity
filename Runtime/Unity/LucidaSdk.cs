#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Lucida
{
    /// <summary>Hidden MonoBehaviour that drives the core: ticks the queue and forwards app lifecycle to <see cref="Lifecycle"/>.</summary>
    public sealed class LucidaDriver : MonoBehaviour
    {
        internal LucidaCore Core;
        internal Lifecycle Life;
        internal ReplayRecorder Replay;

        void Update()
        {
            if (Core != null) Core.Tick();
            if (Replay == null) return;
            if (TapDown(out var p)) Replay.Tap(p.x, UnityEngine.Screen.height - p.y);
            Replay.Tick();
        }

        // Tries the legacy Input Manager, then the new Input System (read by reflection so the package stays optional).
        static bool TapDown(out Vector3 p)
        {
            p = default;
            try
            {
                if (!Input.GetMouseButtonDown(0)) return false;
                p = Input.mousePosition;
                return true;
            }
            catch (InvalidOperationException) { return InputSystemTap.TapDown(out p); }
        }

        void OnApplicationPause(bool paused) { if (Life != null) Life.OnPause(paused); }

        void OnApplicationFocus(bool focused) { if (Life != null) Life.OnFocus(focused); }

        void OnApplicationQuit() { if (Life != null) Life.OnQuit(); }
    }

    public sealed class LucidaOptions
    {
        public string ApiKey = "";
        public string Host = "https://app.lucida.dev";
        public ConsentMode ConsentMode = ConsentMode.OptOut;
        public bool QueueBeforeConsent;
        /// <summary>Bootstrap definitions (the "flags" array of /v1/flags/definitions) for local evaluation.</summary>
        public IList<object> FlagDefinitions;
        public bool LoadFlagsOnInit = true;
        /// <summary>App contexts this build declares; flags scoped to other contexts are not served (sent as evaluation_contexts).</summary>
        public IList<string> EvaluationContexts;
        /// <summary>Capture <c>$app_opened</c> / <c>$app_backgrounded</c> and flush on pause, focus loss and quit.</summary>
        public bool CaptureAppLifecycle = true;
        /// <summary>Wireframe session replay of the uGUI hierarchy (needs analytics and replay consent). Off by default.</summary>
        public bool SessionReplay;
        public ReplayOptions Replay = new ReplayOptions();
    }

    /// <summary>Static entry point. Call <see cref="Init"/> once (main thread), then Capture/Identify/flags.</summary>
    public static class LucidaSdk
    {
        static LucidaCore core;
        static Flags flags;

        public static bool Initialized { get { return core != null; } }
        public static string DistinctId { get { return core.DistinctId; } }
        public static string SessionId { get { return core.SessionId; } }
        public static bool IsOptedOut { get { return core.IsOptedOut; } }

        public static void Init(LucidaOptions o)
        {
            if (core != null) return;
            var go = new GameObject("Lucida") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            var driver = go.AddComponent<LucidaDriver>();
            core = new LucidaCore(new CoreConfig
            {
                ApiKey = o.ApiKey,
                ApiHost = o.Host,
                Http = new UnityWebRequestHttp(driver),
                Store = new PlayerPrefsStore(),
                ConsentMode = o.ConsentMode,
                QueueBeforeConsent = o.QueueBeforeConsent,
            });
            driver.Core = core;
            driver.Life = new Lifecycle(core);
            if (o.CaptureAppLifecycle) driver.Life.Start();
            if (o.SessionReplay) driver.Replay = new ReplayRecorder(core, new UnityWireframeSource(o.Replay.CaptureScene, o.Replay.MaxSceneNodes), o.Replay, k => flags != null && flags.IsEnabled(k));
            flags = new Flags(core, o.FlagDefinitions,
                new Dictionary<string, object>
                {
                    { "os", SystemInfo.operatingSystem }, { "platform", Application.platform.ToString() },
                    { "model", SystemInfo.deviceModel },
                },
                Application.version, true, o.EvaluationContexts);
            if (o.LoadFlagsOnInit) flags.Reload();
        }

        public static void Capture(string name, IDictionary<string, object> properties = null) { core.Capture(name, properties); }
        public static void Identify(string id, IDictionary<string, object> set = null) { core.Identify(id, set); }
        public static void SetPersonProperties(IDictionary<string, object> set = null, IDictionary<string, object> setOnce = null) { core.SetPersonProperties(set, setOnce); }
        public static void Alias(string alias) { core.Alias(alias); }
        public static void Group(string type, string key, IDictionary<string, object> properties = null) { core.Group(type, key, properties); }
        public static void Screen(string name) { core.Screen(name); }
        public static void Reset() { core.Reset(); }
        public static void OptIn() { core.OptIn(); }
        public static void OptOut() { core.OptOut(); }
        public static void SetConsent(IDictionary<string, bool> update) { core.SetConsent(update); }
        public static void Flush() { core.Flush(); }

        /// <summary>Sends everything queued; true once all of it was acknowledged.</summary>
        public static Task<bool> FlushAsync() { return core.FlushAsync(); }
        public static Task<bool> CaptureAsync(string name, IDictionary<string, object> properties = null) { return core.CaptureAsync(name, properties); }

        public static object GetFlag(string key) { return flags.Get(key); }
        public static bool IsEnabled(string key) { return flags.IsEnabled(key); }
        public static object FlagPayload(string key) { return flags.Payload(key); }
        public static void ReloadFlags(Action<bool> done = null) { flags.Reload(done); }
        public static Task<bool> ReloadFlagsAsync() { return flags.ReloadAsync(); }
        public static Task<object> GetFlagAsync(string key) { return flags.GetFlagAsync(key); }
        public static Task<bool> IsEnabledAsync(string key) { return flags.IsEnabledAsync(key); }
    }
}
#endif
