#if UNITY_5_3_OR_NEWER
using UnityEngine;

namespace Lucida
{
    /// <summary>
    /// Project-wide settings edited under Project Settings > Lucida and stored at
    /// <c>Assets/Resources/LucidaSettings.asset</c>. With <see cref="AutoInit"/> the SDK starts before the first scene
    /// loads, so no bootstrap script is needed. Holds the public project key only (lc_pk_...), never a secret.
    /// </summary>
    public sealed class LucidaSettings : ScriptableObject
    {
        public const string ResourceName = "LucidaSettings";

        public string ApiKey = "";
        public string Host = "https://app.lucida.dev";
        public ConsentMode ConsentMode = ConsentMode.OptOut;
        public bool AutoInit;
        public bool SessionReplay;
        public bool CaptureScene = true;

        public static LucidaSettings Load() { return Resources.Load<LucidaSettings>(ResourceName); }

        /// <summary>The options these settings describe.</summary>
        public LucidaOptions ToOptions()
        {
            var o = new LucidaOptions { ApiKey = ApiKey, Host = Host, ConsentMode = ConsentMode, SessionReplay = SessionReplay };
            o.Replay.CaptureScene = CaptureScene;
            return o;
        }

        /// <summary>Problems that stop the settings from working, or null.</summary>
        public string Validate()
        {
            if (!string.IsNullOrEmpty(ApiKey) && !ApiKey.StartsWith("lc_pk_")) return "Use the public project key (lc_pk_...): secret keys must never ship in a game build.";
            if (!Host.StartsWith("https://") && !Host.StartsWith("http://")) return "Host must be an http(s) URL.";
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoStart()
        {
            var s = Load();
            if (s == null || !s.AutoInit || string.IsNullOrEmpty(s.ApiKey) || s.Validate() != null) return;
            LucidaSdk.Init(s.ToOptions());
        }
    }
}
#endif
