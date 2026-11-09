using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Placement and home-observed replica pulls of one database, sampled at <paramref name="ObservedAt"/>.</summary>
/// <param name="DatabaseId">Database ULID, distinct from its route name.</param>
/// <param name="HomeRegion">Region that commits this database's writes.</param>
/// <param name="ObservedAt">UTC sample time of the home-observed pull statuses.</param>
/// <param name="Replicas">Replica progress as observed by the home region; excludes the home itself.</param>
public sealed record TerraBaseDatabaseRegions(string DatabaseId, string HomeRegion, DateTimeOffset ObservedAt,
    IReadOnlyList<TerraBaseReplicaStatus> Replicas) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Replicas);
}

/// <summary>Home-observed pull progress of one replica; unknown observations are null.</summary>
/// <param name="Region">Replica region id.</param>
/// <param name="LagMs">Age of the oldest known commit missing from the last served revision; null without sufficient observations.</param>
/// <param name="LastPulledAt">When a complete pull was served; null before the first pull.</param>
/// <param name="LastVerified">Last checksum verification result; null until verification reports.</param>
/// <param name="Revision">Revision served by the last complete pull; null before the first pull.</param>
public sealed record TerraBaseReplicaStatus(string Region, double? LagMs = null, DateTimeOffset? LastPulledAt = null,
    DateTimeOffset? LastVerified = null, string? Revision = null);
