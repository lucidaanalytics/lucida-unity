using System;
using System.Collections.Generic;

namespace Lucida
{
    /// <summary>Status 0 means a network failure.</summary>
    public sealed class HttpResult
    {
        public int Status;
        public string Body = "";

        public HttpResult(int status, string body = "")
        {
            Status = status;
            Body = body ?? "";
        }
    }

    public sealed class HttpRequest
    {
        public string Method = "GET";
        public string Url = "";
        public string Body;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        /// <summary>Asks the adapter to gzip the body.</summary>
        public bool Gzip;
    }

    /// <summary>Port: how bytes leave the SDK. <c>done</c> may be called synchronously or later (never twice).</summary>
    public interface IHttp
    {
        void Send(HttpRequest request, Action<HttpResult> done);
    }

    /// <summary>Port: synchronous key/value persistence (PlayerPrefs, file, memory).</summary>
    public interface IKeyValueStore
    {
        string Get(string key);
        void Set(string key, string value);
        void Remove(string key);
    }

    public sealed class MemoryStore : IKeyValueStore
    {
        public readonly Dictionary<string, string> Data = new Dictionary<string, string>();

        public string Get(string key) { string v; return Data.TryGetValue(key, out v) ? v : null; }
        public void Set(string key, string value) { Data[key] = value; }
        public void Remove(string key) { Data.Remove(key); }
    }

    public interface IClock
    {
        long NowMs();
    }

    public sealed class SystemClock : IClock
    {
        public long NowMs() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    }
}
