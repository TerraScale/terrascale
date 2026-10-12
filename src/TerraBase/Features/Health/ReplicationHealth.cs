using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;

namespace TerraBase.Features.Health;

/// <summary>Public replication summary of one node: region codes and lag only, never IDs, tenants or databases.</summary>
/// <param name="Region">This node's region.</param>
/// <param name="Status"><c>ok</c>, or <c>degraded</c> when a region this node sees has no replication stream, lags more than
/// <see cref="ReplicationHealth.DegradedAfterSeconds"/> or diverged.</param>
/// <param name="MaxLagSeconds">The largest current backlog age over every replication stream, or null when none reports.</param>
/// <param name="Peers">The other regions this node sees, in region order.</param>
public sealed record ReplicationHealthBody(
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("maxLagSeconds")] double? MaxLagSeconds,
    [property: JsonPropertyName("peers")] IReadOnlyList<ReplicationPeerHealth> Peers);

/// <summary>Another region: its code and this node's replication backlog age towards it, or null without a stream.</summary>
/// <param name="Region">The peer region.</param>
/// <param name="Status"><c>ok</c> or <c>degraded</c>.</param>
/// <param name="LagSeconds">Current backlog age towards the region, or null when no stream to it reports.</param>
public sealed record ReplicationPeerHealth(
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lagSeconds")] double? LagSeconds);

/// <summary>Reads <see cref="ReplicationHealthBody"/> for <c>GET /health/replication</c>.</summary>
public interface IReplicationHealth
{
    /// <summary>The summary now.</summary>
    ValueTask<ReplicationHealthBody> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Regions from the membership this node sees (Fly's internal DNS), lag from its outbound replication status.</summary>
internal sealed class ReplicationHealth(IMembership membership, IReplicationStatus status, TerraBaseOptions options) : IReplicationHealth
{
    /// <summary>Backlog age beyond which a region is degraded; the Grafana send-lag alert (ops/grafana) uses the same bound.</summary>
    public const double DegradedAfterSeconds = 30;

    public async ValueTask<ReplicationHealthBody> ReadAsync(CancellationToken cancellationToken)
    {
        var view = await membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = status.Snapshot();
        var streams = snapshot.Initialized ? snapshot.Peers : [];
        var regions = (view.Elsewhere.IsDefault ? [] : view.Elsewhere).Select(member => member.Region)
            .Concat(streams.Select(peer => peer.Region))
            .Where(region => region.Length > 0 && region != options.Region)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var peers = regions.Select(region =>
        {
            var toRegion = streams.Where(peer => peer.Region == region).ToList();
            double? lag = toRegion.Count == 0 ? null : toRegion.SelectMany(peer => peer.Databases).Select(database => database.CatchupSeconds).DefaultIfEmpty(0).Max();
            var healthy = lag is <= DegradedAfterSeconds && !toRegion.SelectMany(peer => peer.Databases).Any(database => database.Diverged);
            return new ReplicationPeerHealth(region, healthy ? "ok" : "degraded", lag);
        }).ToList();
        double? maxLag = streams.Count == 0 ? null : streams.SelectMany(peer => peer.Databases).Select(database => database.CatchupSeconds).DefaultIfEmpty(0).Max();
        return new ReplicationHealthBody(options.Region, peers.All(peer => peer.Status == "ok") ? "ok" : "degraded", maxLag, peers);
    }
}
