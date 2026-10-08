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
public sealed record QueryShapeStats(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Collection,
    [property: JsonRequired] string Shape,
    [property: JsonRequired] long Queries,
    [property: JsonRequired] double TotalLatencyMs,
    [property: JsonRequired] double MaxLatencyMs,
    [property: JsonRequired] long RowsScanned,
    [property: JsonRequired] DateTimeOffset LastSeenAt);

/// <summary>The bounded observed query shapes of a database or dashboard scope, including pending observations.</summary>
/// <param name="Items">Most queried first, then collection and shape id in ordinal order.</param>
public sealed record QueryShapeListBody(IReadOnlyList<QueryShapeStats> Items);

/// <summary>Statistics, or an unavailable memory budget or persisted format.</summary>
public readonly union QueryShapeReadResult(QueryShapeRead, Unavailable);

/// <summary>Best-effort workload statistics for canonical queries, without retaining literal values.</summary>
public interface IQueryShapeTracker
{
    /// <summary>
    /// Records a canonical shape without catalog I/O. Shapes larger than 8 KiB in UTF-8 and new shapes beyond the
    /// pending item or memory bounds are dropped; the caller must remove literals before recording.
    /// A null database selects the management-service-only dashboard document space.
    /// </summary>
    void Record(TenantContext? database, string collection, string shape, TimeSpan latency, long rowsScanned);

    /// <summary>Reads admitted statistics with pending observations added; the caller executes or disposes the read. Null selects the dashboard scope.</summary>
    ValueTask<QueryShapeReadResult> ReadAsync(TenantContext? database, CancellationToken cancellationToken);
}
