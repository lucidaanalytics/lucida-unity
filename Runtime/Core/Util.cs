using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lucida
{
    public static class Uuid
    {
        /// <summary>UUIDv7: 48-bit ms timestamp, version 7, random remainder.</summary>
        public static string V7(long nowMs)
        {
            var b = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            for (var i = 0; i < 6; i++) b[i] = (byte)(nowMs >> (8 * (5 - i)));
            b[6] = (byte)(0x70 | (b[6] & 0x0f));
            b[8] = (byte)(0x80 | (b[8] & 0x3f));
            var sb = new StringBuilder(36);
            for (var i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10) sb.Append('-');
                sb.Append(b[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static string Iso(long ms)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
    }

    public static class Retry
    {
        /// <summary>Network failure, timeout, rate limit, server errors.</summary>
        public static bool IsRetryable(int status)
        {
            return status == 0 || status == 408 || status == 429 || status >= 500;
        }

        /// <summary>Exponential backoff with jitter: 50%..100% of the capped exponential.</summary>
        public static long DelayMs(int attempt, Func<double> random, long baseMs = 1000, long maxMs = 30000)
        {
            var cap = Math.Min(maxMs, baseMs << Math.Min(attempt, 20));
            return (long)Math.Round(cap * (0.5 + 0.5 * random()));
        }
    }
}
