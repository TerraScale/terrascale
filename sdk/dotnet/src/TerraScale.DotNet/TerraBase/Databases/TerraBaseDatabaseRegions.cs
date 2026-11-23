using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseDatabaseRegions(string DatabaseId, DateTimeOffset ObservedAtUtc,
    IReadOnlyList<TerraBaseReplicaStatus> Regions) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Regions);
}

public sealed record TerraBaseReplicaStatus(string Region, string AppliedBookmark, long LagMs, bool Healthy);
