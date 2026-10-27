using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TerraScale.Authentication.Store;

/// <summary>Storage read timings without document keys, tenant ids, claims or credentials.</summary>
public static class AuthStorageTelemetry
{
    /// <summary>The meter and activity source registered by the host.</summary>
    public const string SourceName = "TerraScale.Authentication.Storage";

    private static readonly ActivitySource Activities = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("ts_auth.storage.read.duration", "s");
    private static readonly string Region = Environment.GetEnvironmentVariable("TS_REGION") switch
    {
        "gru" => "gru",
        "sjc" => "sjc",
        "fra" => "fra",
        _ => "unknown",
    };

    internal static async Task<T> ReadAsync<T>(string collection, string operation, Func<Task<T>> read)
    {
        using var activity = Activities.StartActivity("auth.storage.read");
        activity?.SetTag("db.collection.name", collection);
        activity?.SetTag("db.operation.name", operation);
        activity?.SetTag("cloud.region", Region);
        var started = Stopwatch.GetTimestamp();
        var success = false;
        try
        {
            var result = await read().ConfigureAwait(false);
            success = true;
            return result;
        }
        finally
        {
            if (!success)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
            }

            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
                new("collection", collection), new("operation", operation), new("region", Region), new("outcome", success ? "completed" : "failed"));
        }
    }
}
