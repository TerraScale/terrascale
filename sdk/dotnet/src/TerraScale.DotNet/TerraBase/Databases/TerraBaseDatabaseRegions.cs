using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Selected regions and writer-observed pull progress of one database.</summary>
/// <param name="DatabaseId">Database ULID, distinct from its route name.</param>
/// <param name="ObservedAt">UTC sample time.</param>
/// <param name="Regions">Every selected region in region-id order; order carries no meaning.</param>
public sealed record TerraBaseDatabaseRegions(string DatabaseId, DateTimeOffset ObservedAt,
    IReadOnlyList<TerraBaseReplicaStatus> Regions) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Regions);
}

/// <summary>Pull progress of one region; unknown observations are null.</summary>
/// <param name="Region">Region id.</param>
/// <param name="LagMs">Age of the oldest known commit missing from the last served revision; null without sufficient observations.</param>
/// <param name="LastPulledAt">When a complete pull was served; null before the first pull.</param>
/// <param name="LastVerified">Last checksum verification result; null until verification reports.</param>
/// <param name="Revision">Revision served by the last complete pull; null before the first pull.</param>
/// <param name="Role">"writer" for the region that commits writes, or null.</param>
public sealed record TerraBaseReplicaStatus(string Region, double? LagMs = null, DateTimeOffset? LastPulledAt = null,
    DateTimeOffset? LastVerified = null, string? Revision = null, string? Role = null);
