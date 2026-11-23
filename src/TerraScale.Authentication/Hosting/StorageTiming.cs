using System.Diagnostics;
using System.Globalization;

namespace TerraScale.Authentication.Hosting;

public sealed class StorageTiming
{
    private static readonly AsyncLocal<StorageTiming?> Scope = new();

    private long ticks;

    private int calls;

    private int writes;

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
        Scope.Value = timing;
        await next(context);
    });

    public static HttpMessageHandler Wrap(HttpMessageHandler inner) => new Handler { InnerHandler = inner };

    private sealed class Handler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Scope.Value is not { } timing)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                return await base.SendAsync(request, cancellationToken);
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
