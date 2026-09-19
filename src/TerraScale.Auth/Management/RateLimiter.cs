namespace TerraScale.Auth.Management;

using System.Collections.Concurrent;
using System.Globalization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;

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

    /// <summary>Live bucket count — test observability for the eviction behavior.</summary>
    internal int BucketCount => counts.Count;
}

/// <summary>
/// Applies the Management API rate limit uniformly: every request that presents
/// an sk_ bearer passes through here, no matter which endpoint module serves it,
/// so no module can accidentally ship outside the limit. Register once in App.
/// </summary>
public static class ManagementRateLimit
{
    private const int WindowSeconds = 300;
    private static readonly RateLimiter Limiter = new(WindowSeconds);

    /// <summary>Installs the rate-limit middleware. Invalid or non-sk_ bearers pass
    /// through untouched — the endpoint's own auth answers those with 401.</summary>
    public static void Use(WebApplication app) =>
        app.Use(async (context, next) =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && authorization["Bearer ".Length..].TrimStart().StartsWith("sk_", StringComparison.Ordinal))
            {
                var resolved = await context.RequestServices.GetRequiredService<InstanceResolver>()
                    .ResolveAsync(context.Request, context.RequestAborted);
                if (resolved is not null)
                {
                    var limit = context.RequestServices.GetRequiredService<AuthOptions>().RateLimitPerWindow;
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (!Limiter.Allow(resolved.ApiKeyId!, limit, now, out var remaining))
                    {
                        var retryAfter = Limiter.RetryAfterSeconds(now);
                        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                        context.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString(CultureInfo.InvariantCulture);
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = new { code = "rate_limited", message = $"Management API rate limit exceeded; retry after {retryAfter} seconds." },
                        });
                        return;
                    }
                }
            }

            await next();
        });
}
