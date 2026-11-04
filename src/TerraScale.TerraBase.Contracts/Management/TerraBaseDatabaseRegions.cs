using MessagePack;

namespace TerraScale.TerraBase.Contracts.Management;

/// <summary>Current placement and home-observed replica pulls, sampled at <paramref name="ObservedAt"/>.</summary>
[MessagePackObject(true)]
public sealed record TerraBaseDatabaseRegions(string DatabaseId, string HomeRegion, DateTimeOffset ObservedAt,
    IReadOnlyList<TerraBaseReplicaStatus> Replicas);

/// <summary>Unknown observations are null. A served pull does not attest that the replica applied or verified it.</summary>
[MessagePackObject(true)]
public sealed record TerraBaseReplicaStatus(string Region, double? LagMs = null, DateTimeOffset? LastPulledAt = null,
    DateTimeOffset? LastVerified = null, string? Revision = null);
