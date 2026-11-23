using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Per-region replication state of one database. Every region is equal and accepts writes.</summary>
/// <param name="DatabaseId">Database ULID, distinct from its route name.</param>
/// <param name="ObservedAtUtc">UTC time the answering region sampled the state.</param>
/// <param name="Regions">Every region of the database in region-id order; order carries no meaning.</param>
public sealed record TerraBaseDatabaseRegions(string DatabaseId, DateTimeOffset ObservedAtUtc,
    IReadOnlyList<TerraBaseReplicaStatus> Regions) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Regions);
}

/// <summary>Replication state of one region as the answering region observes it.</summary>
/// <param name="Region">Region id.</param>
/// <param name="AppliedBookmark">
/// The <c>TerraScale-Bookmark</c> of the writes applied as observed by the answering region; pass it to
/// <see cref="TerraBaseSqlDatabase.ObserveBookmark"/> to read at least that state elsewhere.
/// </param>
/// <param name="LagMs">Round-trip time to the region in milliseconds, 0 for the answering region and -1 when unknown.</param>
/// <param name="Healthy">Whether the answering region reaches the region.</param>
/// <param name="Role">An optional role label of the region; null when the server names none.</param>
public sealed record TerraBaseReplicaStatus(string Region, string AppliedBookmark, long LagMs, bool Healthy, string? Role = null);
