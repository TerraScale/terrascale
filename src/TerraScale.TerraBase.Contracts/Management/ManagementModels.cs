using MessagePack;

namespace TerraScale.TerraBase.Contracts.Management
{
    /// <summary>A database identity and its current placement.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseManagedDatabase(string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt, string Region = "", string HomeRegion = "", IReadOnlyList<string>? ReplicaRegions = null, bool AllRegions = false, string? PreviousName = null, string? ConflictOf = null, DateTimeOffset? ExpiresAt = null, IReadOnlyList<string>? Regions = null);

    /// <summary>One page of databases in creation order.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDatabasePage(IReadOnlyList<TerraBaseManagedDatabase> Items, string? Next);

    /// <summary>A database create, rename or placement request.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDatabasePlacement(string? Name = null, string? HomeRegion = null, IReadOnlyList<string>? ReplicaRegions = null, bool? AllRegions = null, long? TtlSeconds = null, IReadOnlyList<string>? Regions = null);

    /// <summary><c>PUT /v1/projects/{project}/databases/{database}/ttl</c> request.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDatabaseTtl(long TtlSeconds);

    /// <summary>A move of a database's home to one of its replica regions.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseMoveHome(string HomeRegion);
}
