using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lucida
{
    /// <summary>
    /// Task-based wrappers over the callback core, for <c>await</c> in game code. Completion happens on whatever thread
    /// the <see cref="IHttp"/> adapter calls back on (Unity's adapter calls back on the main thread, so awaits resume there).
    /// </summary>
    public static class LucidaAsync
    {
        /// <summary>Sends everything queued; true once all of it was acknowledged, false if a batch is retrying or was dropped.</summary>
        public static Task<bool> FlushAsync(this LucidaCore core)
        {
            var tcs = new TaskCompletionSource<bool>();
            core.Queue.Flush(ok => tcs.TrySetResult(ok));
            return tcs.Task;
        }

        /// <summary>Captures an event and waits until it was delivered.</summary>
        public static Task<bool> CaptureAsync(this LucidaCore core, string name, IDictionary<string, object> properties = null)
        {
            core.Capture(name, properties);
            return core.FlushAsync();
        }

        /// <summary>Re-evaluates flags (local, then the server when needed); false when the request failed.</summary>
        public static Task<bool> ReloadAsync(this Flags flags)
        {
            var tcs = new TaskCompletionSource<bool>();
            flags.Reload(ok => tcs.TrySetResult(ok));
            return tcs.Task;
        }

        /// <summary>Reloads, then reads <paramref name="key"/> (false | true | variant key). Emits <c>$feature_flag_called</c> like <see cref="Flags.Get"/>.</summary>
        public static async Task<object> GetFlagAsync(this Flags flags, string key)
        {
            await flags.ReloadAsync().ConfigureAwait(false);
            return flags.Get(key);
        }

        public static async Task<bool> IsEnabledAsync(this Flags flags, string key)
        {
            await flags.ReloadAsync().ConfigureAwait(false);
            return flags.IsEnabled(key);
        }
    }
}
