using System.Text.Json.Serialization;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Queries;

/// <summary>Aggregated observations of one canonical query shape in a collection.</summary>
/// <param name="Id">Lowercase hexadecimal SHA-256 of the UTF-8 canonical shape.</param>
/// <param name="Collection">Queried collection.</param>
/// <param name="Shape">Canonical query with literal values removed.</param>
/// <param name="Queries">Number of observed queries.</param>
/// <param name="TotalLatencyMs">Sum of observed query latencies in milliseconds.</param>
/// <param name="MaxLatencyMs">Longest observed query latency in milliseconds.</param>
/// <param name="RowsScanned">Total rows the queries scanned.</param>
/// <param name="LastSeenAt">Time of the latest observation.</param>
/// <param name="LatencyBuckets">
/// Observed latencies per power-of-two millisecond bucket: bucket 0 is under 1 ms, bucket i (1 to 18) is under 2^i ms and
/// bucket 19 is the rest. Absent for statistics recorded before buckets existed.
/// </param>
public sealed record QueryShapeStats(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Collection,
    [property: JsonRequired] string Shape,
    [property: JsonRequired] long Queries,
    [property: JsonRequired] double TotalLatencyMs,
    [property: JsonRequired] double MaxLatencyMs,
    [property: JsonRequired] long RowsScanned,
    [property: JsonRequired] DateTimeOffset LastSeenAt,
    IReadOnlyList<long>? LatencyBuckets = null)
{
    /// <summary>Number of <see cref="LatencyBuckets"/>.</summary>
    public const int BucketCount = 20;

    /// <summary>The 95th percentile latency: its bucket's upper bound, at most the maximum; null without buckets.</summary>
    public double? P95LatencyMs
    {
        get
        {
            if (LatencyBuckets is not { Count: BucketCount } buckets || buckets.Sum() is not (> 0 and var total))
            {
                return null;
            }

            long seen = 0;
            var bucket = 0;
            while ((seen += buckets[bucket]) < Math.Ceiling(total * 0.95))
            {
                bucket++;
            }

            return Math.Min(MaxLatencyMs, Math.Pow(2, bucket));
        }
    }

    /// <summary>The bucket of one observed latency.</summary>
    public static int Bucket(double milliseconds) =>
        milliseconds < 1 ? 0 : Math.Min(BucketCount - 1, (int)Math.Floor(Math.Log2(milliseconds)) + 1);
}

/// <param name="Items">Most queried first, then collection and shape id in ordinal order.</param>
public sealed record QueryShapeListBody(IReadOnlyList<QueryShapeStats> Items);

/// <summary>Statistics, or an unavailable memory budget or persisted format.</summary>
public readonly union QueryShapeReadResult(QueryShapeRead, Unavailable);

/// <summary>Best-effort workload statistics for canonical queries, without retaining literal values.</summary>
internal interface IQueryShapeTracker
{
    void Record(TenantContext database, string collection, string shape, TimeSpan latency, long rowsScanned);

    ValueTask<QueryShapeReadResult> ReadAsync(TenantContext database, CancellationToken cancellationToken);
}
