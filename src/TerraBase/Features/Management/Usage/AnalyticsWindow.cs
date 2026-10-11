using TerraBase.Errors;

namespace TerraBase.Features.Management.Usage;

/// <summary>A fixed dashboard range and its automatic UTC bucketing.</summary>
public sealed record AnalyticsWindow(string Range, UsageWindow Usage)
{
    /// <summary>Public query contract, shared by replication and analytics endpoints.</summary>
    public const string Description = "Query: range=1h|24h|7d|30d (default 24h), database (catalog id, on project routes), region (observing region). "
        + "UTC buckets are 1 minute, 1 hour, 6 hours and 1 day respectively; the current bucket is partial. Empty buckets are included. "
        + "Responses contain this regional catalog's observations; ts-go reads each region through its Atlas endpoint. Unobserved remote placement regions are omitted. "
        + "Counters are best-effort observations, flushed every 10 seconds, not billing. Requests are bounded to 100 databases, 16384 database-region buckets and 5 seconds.";

    private static readonly UsageGranularity SixHours = new("hour", 6, 140, "yyyy-MM-dd'T'HH");

    /// <summary>Validates the range and region query parameters.</summary>
    public static Valid<AnalyticsWindow> Parse(IQueryCollection query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        var range = query["range"].ToString() is "" ? "24h" : query["range"].ToString();
        var (granularity, count) = range switch
        {
            "1h" => (UsageWindow.Minute, 60),
            "24h" => (UsageWindow.Hour, 24),
            "7d" => (SixHours, 28),
            "30d" => (UsageWindow.Day, 30),
            _ => (null, 0),
        };
        if (granularity is null || query["region"].ToString() is { Length: > 0 } region && !UsageMeter.ValidRegion(region)
            || query["range"].Count > 1 || query["region"].Count > 1 || query["database"].Count > 1)
        {
            return new Validation("range must be 1h, 24h, 7d or 30d and region must be a region name", "invalid_query");
        }

        var end = now.ToUnixTimeSeconds() / (granularity.Minutes * 60) + 1;
        return new AnalyticsWindow(range, new(granularity, end - count, count));
    }
}
