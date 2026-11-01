#if UNITY_5_3_OR_NEWER
using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Lucida
{
    /// <summary>IKeyValueStore on PlayerPrefs. Only ever written through the consent gate (see GatedStore).</summary>
    public sealed class PlayerPrefsStore : IKeyValueStore
    {
        const string Prefix = "lucida.";

        public string Get(string key)
        {
            return PlayerPrefs.HasKey(Prefix + key) ? PlayerPrefs.GetString(Prefix + key) : null;
        }

        public void Set(string key, string value)
        {
            PlayerPrefs.SetString(Prefix + key, value);
            PlayerPrefs.Save();
        }

        public void Remove(string key)
        {
            PlayerPrefs.DeleteKey(Prefix + key);
            PlayerPrefs.Save();
        }
    }

    /// <summary>IHttp on UnityWebRequest; requests run as coroutines on <c>runner</c>, callbacks arrive on the main thread.</summary>
    public sealed class UnityWebRequestHttp : IHttp
    {
        readonly MonoBehaviour runner;

        public UnityWebRequestHttp(MonoBehaviour runner) { this.runner = runner; }

        public void Send(HttpRequest request, Action<HttpResult> done)
        {
            runner.StartCoroutine(Run(request, done));
        }

        static IEnumerator Run(HttpRequest r, Action<HttpResult> done)
        {
            using (var web = new UnityWebRequest(r.Url, r.Method))
            {
                web.downloadHandler = new DownloadHandlerBuffer();
                web.timeout = 15;
                if (r.Body != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(r.Body);
                    if (r.Gzip) bytes = Gzip(bytes);
                    web.uploadHandler = new UploadHandlerRaw(bytes);
                }
                foreach (var h in r.Headers) web.SetRequestHeader(h.Key, h.Value);
                yield return web.SendWebRequest();
                // ConnectionError / DataProcessingError -> 0 (retryable network failure); HTTP errors keep their status.
                var status = web.result == UnityWebRequest.Result.ConnectionError ? 0 : (int)web.responseCode;
                done(new HttpResult(status, web.downloadHandler != null ? web.downloadHandler.text : ""));
            }
        }

        static byte[] Gzip(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress)) gz.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }
    }
}
#endif
