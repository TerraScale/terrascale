using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TerraScale.Dashboard;

/// <summary>Measures Razor result execution without buffering its response.
/// The histogram is available through standard .NET Meter listeners.</summary>
internal sealed class TimedPageResult(IResult result) : IResult
{
    private static readonly Meter Meter = new("TerraScale.Dashboard");
    private static readonly Histogram<double> RenderDuration =
        Meter.CreateHistogram<double>("dashboard.render.duration", "ms");

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await result.ExecuteAsync(httpContext);
        }
        finally
        {
            // Endpoint display names have bounded cardinality; URLs may contain
            // private identifiers and must not become metric tags.
            RenderDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("endpoint", httpContext.GetEndpoint()?.DisplayName));
        }
    }
}
