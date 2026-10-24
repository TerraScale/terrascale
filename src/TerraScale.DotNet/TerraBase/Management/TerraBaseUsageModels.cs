using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Best-effort reads and writes over a UTC bucket period; not billing data.</summary>
/// <param name="From">Inclusive start of the first bucket.</param>
/// <param name="To">Exclusive end of the last bucket.</param>
/// <param name="Granularity">hour or day.</param>
/// <param name="Reads">Reads in the period.</param>
/// <param name="Writes">Writes in the period.</param>
public sealed record TerraBaseUsagePeriod(DateTimeOffset From, DateTimeOffset To, string Granularity, long Reads, long Writes);

/// <summary>One UTC hour or day of best-effort operation counts.</summary>
/// <param name="Start">Bucket start.</param>
/// <param name="Reads">Reads counted.</param>
/// <param name="Writes">Writes counted.</param>
public sealed record TerraBaseUsagePoint(DateTimeOffset Start, long Reads, long Writes);

/// <summary>Database storage and best-effort usage; counts can be lost on crash and are not billing data.</summary>
/// <param name="DatabaseId">Database ULID, distinct from its route name.</param>
/// <param name="StorageBytes">Bytes on disk.</param>
/// <param name="LogicalBytes">JSON bytes of live documents.</param>
/// <param name="Collections">Collection count.</param>
/// <param name="Documents">Live document count.</param>
/// <param name="Reads">Reads since creation.</param>
/// <param name="Writes">Writes since creation.</param>
/// <param name="Period">Counts over the requested period.</param>
/// <param name="Series">Oldest-first buckets; empty inside project Items.</param>
public sealed record TerraBaseDatabaseUsage(string DatabaseId, long StorageBytes, long LogicalBytes, long Collections, long Documents,
    long Reads, long Writes, TerraBaseUsagePeriod Period, IReadOnlyList<TerraBaseUsagePoint> Series) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Series);
}

/// <summary>Project storage and best-effort usage totals; not billing data.</summary>
/// <param name="ProjectId">Project ULID.</param>
/// <param name="Databases">Database count.</param>
/// <param name="StorageBytes">Total disk bytes.</param>
/// <param name="LogicalBytes">Total live JSON bytes.</param>
/// <param name="Documents">Live document count.</param>
/// <param name="Reads">Reads since each database's creation.</param>
/// <param name="Writes">Writes since each database's creation.</param>
/// <param name="Period">Counts over the requested period.</param>
/// <param name="Series">Oldest-first buckets.</param>
/// <param name="Items">Per-database usage in creation order.</param>
public sealed record TerraBaseProjectUsage(string ProjectId, long Databases, long StorageBytes, long LogicalBytes, long Documents,
    long Reads, long Writes, TerraBaseUsagePeriod Period, IReadOnlyList<TerraBaseUsagePoint> Series, IReadOnlyList<TerraBaseDatabaseUsage> Items) : IJsonOnDeserialized
{
    /// <summary>Logical file bytes including pending uploads, counted once across replicas. Included in project storage totals.</summary>
    public long FileBytes { get; init; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        TerraBaseJsonContext.RequireNonNullItems(Series);
        TerraBaseJsonContext.RequireNonNullItems(Items);
    }
}
