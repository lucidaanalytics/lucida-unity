namespace Lucida
{
    /// <summary>
    /// App lifecycle rules, engine-free: <c>$app_opened</c> at start and on every return from the background,
    /// <c>$app_backgrounded</c> + flush when the app pauses or loses focus, flush + shutdown on quit. The Unity driver
    /// forwards <c>OnApplicationPause/Focus/Quit</c> here. Pause and focus both fire on mobile for one transition;
    /// the state machine makes the pair count once.
    /// </summary>
    public sealed class Lifecycle
    {
        readonly LucidaCore core;
        bool background;

        public Lifecycle(LucidaCore core)
        {
            this.core = core;
        }

        public bool InBackground { get { return background; } }

        /// <summary>Records <c>$app_opened</c> (cold start). Call once after init.</summary>
        public void Start()
        {
            background = false;
            core.Capture("$app_opened", new System.Collections.Generic.Dictionary<string, object> { { "from_background", false } });
        }

        public void OnPause(bool paused)
        {
            if (paused) EnterBackground(); else EnterForeground();
        }

        public void OnFocus(bool focused)
        {
            if (focused) EnterForeground(); else EnterBackground();
        }

        public void OnQuit()
        {
            if (!background) core.Capture("$app_backgrounded");
            background = true;
            core.Shutdown();
        }

        void EnterBackground()
        {
            if (background) return;
            background = true;
            core.Capture("$app_backgrounded");
            core.Flush();
        }

        void EnterForeground()
        {
            if (!background) return;
            background = false;
            core.Capture("$app_opened", new System.Collections.Generic.Dictionary<string, object> { { "from_background", true } });
        }
    }
}
