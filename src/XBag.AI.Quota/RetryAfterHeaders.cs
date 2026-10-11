using System.Globalization;

namespace XBag.AI.Quota;

/// <summary>Reads how long a provider asked the client to wait, from the response headers of a 429 or 503.</summary>
public static class RetryAfterHeaders
{
    /// <summary>The headers read, in order: Azure's millisecond headers first (more precise), then the standard one.</summary>
    public static IReadOnlyList<string> Names { get; } = ["retry-after-ms", "x-ms-retry-after-ms", "Retry-After"];

    /// <summary>Reads the wait from the first header in <see cref="Names"/> that has a valid value.</summary>
    /// <param name="getHeader">Returns a header value by name (case-insensitive), or <see langword="null"/> when it is absent.</param>
    /// <param name="now">The current time, to turn an HTTP-date <c>Retry-After</c> into a wait.</param>
    /// <returns>The wait (never negative), or <see langword="null"/> when no header has a valid value.</returns>
    /// <remarks>
    /// <c>retry-after-ms</c> and <c>x-ms-retry-after-ms</c> are milliseconds (Azure). <c>Retry-After</c> is either
    /// delta-seconds (<c>"20"</c>) or an HTTP-date (<c>"Sun, 11 Oct 2026 09:00:20 GMT"</c>), per RFC 9110. Provider-specific
    /// reset headers (OpenAI <c>x-ratelimit-reset-tokens</c>, Anthropic <c>anthropic-ratelimit-*-reset</c>) are not read.
    /// </remarks>
    public static TimeSpan? Parse(Func<string, string?> getHeader, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(getHeader);

        foreach (string name in Names)
        {
            string? value = getHeader(name)?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (name.EndsWith("-ms", StringComparison.Ordinal))
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) && ms >= 0 && ms < TimeSpan.MaxValue.TotalMilliseconds)
                {
                    return TimeSpan.FromTicks((long)Math.Ceiling(ms * TimeSpan.TicksPerMillisecond));
                }

                continue;
            }

            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
            {
                return seconds <= (long)TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue;
            }

            if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTimeOffset date))
            {
                TimeSpan wait = date - now;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }

        return null;
    }
}
