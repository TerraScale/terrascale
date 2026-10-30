using MessagePack;

namespace TerraScale.TerraBase.Contracts.Management
{
    /// <summary>A database identity and its current placement.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseManagedDatabase(string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt, string Region = "", string HomeRegion = "", IReadOnlyList<string>? ReplicaRegions = null, bool AllRegions = false, string? PreviousName = null, string? ConflictOf = null);

    /// <summary>One page of databases in creation order.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDatabasePage(IReadOnlyList<TerraBaseManagedDatabase> Items, string? Next);

    /// <summary>A database create, rename or placement request.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDatabasePlacement(string? Name = null, string? HomeRegion = null, IReadOnlyList<string>? ReplicaRegions = null, bool? AllRegions = null);

    /// <summary>A secondary index.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseIndex(string Name, string Collection, IReadOnlyList<string> Fields, string State);

    /// <summary>The indexes in a database.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseIndexPage(IReadOnlyList<TerraBaseIndex> Items);

    /// <summary>A secondary index definition.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseCreateIndex(string Collection, IReadOnlyList<string> Fields);

    /// <summary>Live collection counts and logical bytes.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseCollectionStats(string Name, long Documents, long Bytes);

    /// <summary>Collection statistics for a database.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseCollectionStatsPage(IReadOnlyList<TerraBaseCollectionStats> Items);


}
