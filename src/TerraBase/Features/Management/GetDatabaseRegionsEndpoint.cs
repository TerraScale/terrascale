using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class GetDatabaseRegionsEndpoint(DatabaseAccess access, IClusterCoordinator coordinator, TerraBaseOptions options, TimeProvider time)
    : Endpoint<DatabaseInput, Result<DatabaseReplicationBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(ManagementRoute.Database + "/regions");
        Name("getDatabaseReplication");
        Summary("Per-region replication state; every region accepts writes");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<DatabaseReplicationBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await access.ManagedAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken) switch
        {
            ResolvedDatabase resolved => await access.OpenAsync(resolved.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => Body(resolved.Database, session),
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    private DatabaseReplicationBody Body(DatabaseRecord database, ISqlDatabaseSession session)
    {
        var applied = coordinator.CurrentBookmark(database.Id).Merge(session.CurrentBookmark);
        var peers = coordinator.Peers;
        List<RegionReplicationBody> regions = [];
        foreach (var region in database.Regions.Append(options.Region).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (region == options.Region)
            {
                regions.Add(new RegionReplicationBody(region, applied.Encode(), 0, Healthy: true));
                continue;
            }

            var health = peers.Where(peer => peer.Peer.Region == region).ToArray();
            var healthy = health.Any(static peer => peer.Status == PeerStatus.Healthy);
            var lag = health.Length == 0 ? -1 : (long)health.Min(static peer => peer.LastRoundTrip).TotalMilliseconds;
            regions.Add(new RegionReplicationBody(region, applied.Encode(), lag, healthy));
        }

        return new DatabaseReplicationBody(database.Id.ToString(), time.GetUtcNow(), regions);
    }
}
