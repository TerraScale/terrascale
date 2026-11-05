using System.Diagnostics;
using System.Globalization;
using TerraBase.Configuration;

namespace TerraBase.Hosting;

// Bounded stage names only: no SQL, tenant identifiers or authentication data in response headers.
internal sealed class SqlRequestTiming
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _previous = Stopwatch.GetTimestamp();
    private readonly List<string> _stages = [];

    internal static WebApplication UseSqlRequestTiming(WebApplication app)
    {
        app.Use((context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method)
                && context.Request.Path.Value?.EndsWith("/sql/execute", StringComparison.Ordinal) == true)
            {
                var timing = new SqlRequestTiming();
                context.Features.Set(timing);
                context.Response.OnStarting(() =>
                {
                    timing.Mark("execute");
                    var region = context.RequestServices.GetRequiredService<TerraBaseOptions>().Region;
                    context.Response.Headers.Append("Server-Timing", string.Join(", ", timing._stages));
                    context.Response.Headers.Append("Server-Timing", string.Create(CultureInfo.InvariantCulture,
                        $"app;dur={Stopwatch.GetElapsedTime(timing._started).TotalMilliseconds:0.00};desc=\"{region}\""));
                    return Task.CompletedTask;
                });
            }

            return next(context);
        });
        return app;
    }

    internal static void Checkpoint(WebApplication app, string stage) => app.Use((context, next) =>
    {
        context.Features.Get<SqlRequestTiming>()?.Mark(stage);
        return next(context);
    });

    internal static void Mark(HttpContext context, string stage) => context.Features.Get<SqlRequestTiming>()?.Mark(stage);

    private void Mark(string stage)
    {
        var now = Stopwatch.GetTimestamp();
        _stages.Add(string.Create(CultureInfo.InvariantCulture, $"{stage};dur={Stopwatch.GetElapsedTime(_previous, now).TotalMilliseconds:0.00}"));
        _previous = now;
    }
}
