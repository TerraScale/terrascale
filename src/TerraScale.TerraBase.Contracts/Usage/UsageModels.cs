using MessagePack;

namespace TerraScale.TerraBase.Contracts.Usage
{
    /// <summary>The UsagePeriod REST representation.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseUsagePeriod(DateTimeOffset From, DateTimeOffset To, string Granularity, long Reads, long Writes);

    /// <summary>The UsagePoint REST representation.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseUsagePoint(DateTimeOffset Start, long Reads, long Writes);

    /// <summary>The UsageMonth REST representation.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseUsageMonth(string Month, long ReadUnits, long WriteUnits, long Units, long? OrganizationUnits = null);

    /// <summary>The DatabaseUsage REST representation.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDatabaseUsage(string DatabaseId, long StorageBytes, long LogicalBytes, long Collections, long Documents, long Reads, long Writes, TerraBaseUsagePeriod Period, IReadOnlyList<TerraBaseUsagePoint> Series);

    /// <summary>The ProjectUsage REST representation.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseProjectUsage(string ProjectId, long Databases, long Collections, long StorageBytes, long LogicalBytes, long Documents, long Reads, long Writes, TerraBaseUsagePeriod Period, IReadOnlyList<TerraBaseUsagePoint> Series, IReadOnlyList<TerraBaseDatabaseUsage> Items, long FileBytes = 0, TerraBaseUsageMonth? Month = null);


}
