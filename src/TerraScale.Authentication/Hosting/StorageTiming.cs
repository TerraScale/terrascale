using System.Diagnostics;
using System.Globalization;

namespace TerraScale.Authentication.Hosting;

/// <summary>
/// Server-Timing for bearer-authenticated API calls (<c>/v1/…</c> and the UserInfo endpoint) and token requests:
/// <c>app;dur=…, storage;dur=…;desc="N calls, W writes"</c>, so a caller such as the dashboard can tell ts-auth's storage
/// time from its own transport, and how many storage writes (each waits for another region) a request made. Hosted pages
/// never get it: storage call counts there could hint whether an account exists. A token request's counts reveal nothing
/// the client does not already hold: it presents the code, refresh token or credential itself.
/// Concurrent storage calls add up, so <c>storage</c> can exceed <c>app</c>.
/// </summary>
public sealed class StorageTiming
{
    private static readonly AsyncLocal<StorageTiming?> Scope = new();

    private long ticks;

    private int calls;

    private int writes;

    /// <summary>Times API requests and writes their Server-Timing header before the response starts.</summary>
    public static IApplicationBuilder Use(IApplicationBuilder app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        var token = HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.Value?.TrimEnd('/').EndsWith("/oauth/token", StringComparison.OrdinalIgnoreCase) == true;
        if (!token && (!context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || !(context.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Value?.EndsWith("/oauth/userinfo", StringComparison.OrdinalIgnoreCase) == true)))
        {
            await next(context);
            return;
        }

        var timing = new StorageTiming();
        var started = Stopwatch.GetTimestamp();
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Append("Server-Timing", string.Create(CultureInfo.InvariantCulture,
                $"app;dur={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}, storage;dur={TimeSpan.FromTicks(Interlocked.Read(ref timing.ticks)).TotalMilliseconds:F1};desc=\"{Volatile.Read(ref timing.calls)} calls, {Volatile.Read(ref timing.writes)} writes\""));
            return Task.CompletedTask;
        });
        // Set inside this async method, so the scope ends with the request and never leaks to the caller.
        Scope.Value = timing;
        await next(context);
    });

    /// <summary>Wraps the storage transport so each call made while timing a request is counted.</summary>
    public static HttpMessageHandler Wrap(HttpMessageHandler inner) => new Handler { InnerHandler = inner };

    private sealed class Handler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Scope.Value is not { } timing)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Add(ref timing.ticks, Stopwatch.GetElapsedTime(started).Ticks);
                Interlocked.Increment(ref timing.calls);
                if (request.Method != HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/query", StringComparison.Ordinal) != true)
                {
                    Interlocked.Increment(ref timing.writes);
                }
            }
        }
    }
}
