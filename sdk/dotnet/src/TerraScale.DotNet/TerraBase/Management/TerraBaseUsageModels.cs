using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseUsagePeriod(DateTimeOffset From, DateTimeOffset To, string Granularity, long Reads, long Writes);

public sealed record TerraBaseUsagePoint(DateTimeOffset Start, long Reads, long Writes);

public sealed record TerraBaseDatabaseUsage(string DatabaseId, long StorageBytes, long LogicalBytes,
    long Reads, long Writes, TerraBaseUsagePeriod Period, IReadOnlyList<TerraBaseUsagePoint> Series) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Series);
}

public sealed record TerraBaseProjectUsage(string ProjectId, long Databases, long StorageBytes, long LogicalBytes,
    long Reads, long Writes, TerraBaseUsagePeriod Period, IReadOnlyList<TerraBaseUsagePoint> Series, IReadOnlyList<TerraBaseDatabaseUsage> Items) : IJsonOnDeserialized
{
    public long FileBytes { get; init; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        TerraBaseJsonContext.RequireNonNullItems(Series);
        TerraBaseJsonContext.RequireNonNullItems(Items);
    }
}
