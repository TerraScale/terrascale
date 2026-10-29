using TerraBase.Errors;
using TerraBase.Features.UserAuth;

namespace TerraBase.Features.Management.Usage;

public sealed record BandwidthUsage(long BytesSent, long BytesReceived);

public sealed record LatencyPercentiles(double? P50Ms, double? P95Ms, double? P99Ms);

public sealed record StatementUsage(long Statements, long Errors, LatencyPercentiles Latency);

public sealed record AnalyticsPoint(DateTimeOffset Start, IReadOnlyDictionary<string, long> Requests, IReadOnlyDictionary<string, long> Operations,
    long Errors, LatencyPercentiles Latency, long? StorageBytes, BandwidthUsage ClientBandwidth,
    IReadOnlyDictionary<string, StatementUsage> Statements);

public sealed record DatabaseAnalytics(string DatabaseId, string DatabaseName, string Region, IReadOnlyList<AnalyticsPoint> Series);

public sealed record ProjectAnalytics(string ProjectId, string Range, DateTimeOffset From, DateTimeOffset To, int BucketSeconds,
    IReadOnlyList<DatabaseAnalytics> Items);

public readonly union AnalyticsRead<T>(T, NotFound, Validation, Unavailable);

public interface IUsageAnalytics
{
    ValueTask<AnalyticsRead<ProjectAnalytics>> AnalyticsAsync(
        ProjectAccess access, AnalyticsWindow window, string? database, string? region, Action<IDisposable> retain, CancellationToken cancellationToken);

}
