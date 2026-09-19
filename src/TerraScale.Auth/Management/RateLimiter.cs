namespace TerraScale.Auth.Management;

using System.Collections.Concurrent;

/// <summary>
/// Fixed-window rate limiter for the Management API: counts requests per API
/// key per window; the counter resets when the window rolls over.
/// </summary>
public sealed class RateLimiter(int windowSeconds)
{
    private readonly ConcurrentDictionary<(string ApiKeyId, long Window), int> counts = new();

    /// <summary>Seconds until the current window closes — the Retry-After value.</summary>
    public int RetryAfterSeconds(long nowUnixSeconds)
    {
        var windowEnd = (nowUnixSeconds / windowSeconds + 1) * windowSeconds;
        return (int)(windowEnd - nowUnixSeconds);
    }

    /// <summary>Counts one request for <paramref name="apiKeyId"/> in its current window; false when the limit is exceeded.</summary>
    public bool Allow(string apiKeyId, int limit, long nowUnixSeconds, out int remaining)
    {
        var window = nowUnixSeconds / windowSeconds;

        // ponytail: prune on every access is O(total counters); switch to an occasional sweep if key count grows large.
        foreach (var key in counts.Keys)
            if (key.Window < window - 2)
                counts.TryRemove(key, out _);

        var used = counts.AddOrUpdate((apiKeyId, window), 1, static (_, count) => count + 1);
        remaining = Math.Max(0, limit - used);
        return used <= limit;
    }
}
