using TerraBase.Errors;
using TerraBase.Features.UserAuth;

namespace TerraBase.Features.Management.Usage;

/// <summary>Sent and received bytes of an observing region; neither includes the other direction.</summary>
public sealed record BandwidthUsage(long BytesSent, long BytesReceived);

/// <summary>Replication traffic by source, destination and kind, as observed at the sender and receiver.</summary>
public sealed record ReplicationBandwidth(string SourceRegion, string DestinationRegion, string Kind, long BytesSent, long BytesReceived);

/// <summary>
/// Approximate latency percentiles in milliseconds, interpolated linearly within merged power-of-two histogram bins (0.25 ms to
/// 65536 ms); a percentile in the last, open-ended bin is its lower bound, 32768 ms. Null without requests.
/// </summary>
public sealed record LatencyPercentiles(double? P50Ms, double? P95Ms, double? P99Ms);

/// <summary>One UTC analytics bucket. Storage is the average of sampled on-disk bytes, or null before sampling.</summary>
public sealed record AnalyticsPoint(DateTimeOffset Start, IReadOnlyDictionary<string, long> Requests, IReadOnlyDictionary<string, long> Operations,
    long Errors, LatencyPercentiles Latency, long? StorageBytes, BandwidthUsage ClientBandwidth, BandwidthUsage ReplicationBandwidth);

/// <summary>A database and observing region's analytics, oldest bucket first.</summary>
public sealed record DatabaseAnalytics(string DatabaseId, string DatabaseName, string Region, IReadOnlyList<AnalyticsPoint> Series);

/// <summary>Project analytics, with automatic UTC buckets and optional database and region filters.</summary>
public sealed record ProjectAnalytics(string ProjectId, string Range, DateTimeOffset From, DateTimeOffset To, int BucketSeconds,
    IReadOnlyList<DatabaseAnalytics> Items);

/// <summary>One replication period bucket, including bytes by traffic kind and directed region pair.</summary>
public sealed record ReplicationPoint(DateTimeOffset Start, long ReplicatedOps, IReadOnlyList<ReplicationBandwidth> Bandwidth);

/// <summary>A database and observing region's replication. Null lag or divergence means no recent observation.</summary>
public sealed record DatabaseReplication(string DatabaseId, string DatabaseName, string Region, string Status, long ReplicatedOps,
    double? LagMs, bool? Divergence, double? ConvergenceMs, DateTimeOffset? LastApplied, IReadOnlyList<ReplicationBandwidth> Bandwidth,
    IReadOnlyList<ReplicationPoint> Series);

/// <summary>Replication usage and the latest observed health per database and region of an authorized project.</summary>
public sealed record ProjectReplication(string ProjectId, string Range, DateTimeOffset From, DateTimeOffset To, int BucketSeconds,
    IReadOnlyList<DatabaseReplication> Items);

/// <summary>An analytics read; NotFound hides a project or database the grant does not reach, and Unavailable is a missed deadline.</summary>
/// <typeparam name="T">The analytics read.</typeparam>
public readonly union AnalyticsRead<T>(T, NotFound, Validation, Unavailable);

/// <summary>Reads the usage-owned analytics and replication observations within a verified project-token grant.</summary>
public interface IUsageAnalytics
{
    /// <summary>
    /// Reads analytics; database is a catalog id filter, or null for all databases the grant reaches. <paramref name="retain"/>
    /// takes the memory admission of a returned result, to release once the result has been sent.
    /// </summary>
    ValueTask<AnalyticsRead<ProjectAnalytics>> AnalyticsAsync(
        ProjectAccess access, AnalyticsWindow window, string? database, string? region, Action<IDisposable> retain, CancellationToken cancellationToken);

    /// <summary>Reads replication usage and health with the same authorization, filters and result admission.</summary>
    ValueTask<AnalyticsRead<ProjectReplication>> ReplicationAsync(
        ProjectAccess access, AnalyticsWindow window, string? database, string? region, Action<IDisposable> retain, CancellationToken cancellationToken);
}
