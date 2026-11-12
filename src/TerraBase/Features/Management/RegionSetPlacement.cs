using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed class RegionSetPlacement(IManagementService management, TerraBaseOptions options, IHomeApply apply, HomeMoves moves, IRegionPeers peers, IDatabaseRevisions revisions)
{
    internal static readonly Unavailable PlacementNotReady = new("requested regions remain members; retry when a requested region can take over", "replica_not_ready");

    public async ValueTask<IResult> UpdateAsync(HttpContext context, ManagedDatabase database, IReadOnlyList<string> selected, CancellationToken cancellationToken)
    {
        var user = ManagementRoute.UserId(context);
        var organization = ManagementRoute.OrganizationId(context);
        var placed = await apply.PrepareReplicatedAsync(organization, options.Region,
            await management.SetDatabaseRegionSetAsync(user, organization, database.ProjectId, database.Id, selected, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (placed is not ManagedDatabase current)
        {
            return Answer(placed);
        }

        var home = current.Home(options.Region);
        if (selected.Contains(home, StringComparer.Ordinal))
        {
            return Answer(current);
        }

        if (current.PlacementVersion != database.PlacementVersion)
        {
            return ErrorResponse.From(PlacementNotReady);
        }

        IReadOnlyList<string> candidates = [.. selected.Order(StringComparer.Ordinal)];
        var tenant = new TenantContext(organization, PlanTier.Hobby, database.ProjectId, database.Id);
        var observations = await revisions.ReplicaStatusAsync(tenant, current.Replicas, cancellationToken).ConfigureAwait(false);
        var target = candidates.FirstOrDefault(candidate => observations.Any(row => row.Region == candidate && row is { LagMs: not null, LastPulledAt: not null, Revision: not null }));
        if (target is null)
        {
            return ErrorResponse.From(PlacementNotReady);
        }

        var result = await moves.MoveAsync(tenant, target, selected, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ManagedDatabase moved => await ContinueAsync(moved, selected, cancellationToken).ConfigureAwait(false),
            Unavailable error => ErrorResponse.From(error == HomeMoves.ReplicaNotReady ? PlacementNotReady : error),
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Validation => ErrorResponse.From(PlacementNotReady),
            Conflict error => ErrorResponse.From(error),
        };
    }

    private async ValueTask<IResult> ContinueAsync(ManagedDatabase database, IReadOnlyList<string> selected, CancellationToken cancellationToken)
    {
        var home = database.Home(options.Region);
        var peer = (await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(region => region.Region == home);
        return peer is null ? DatabaseRegions.HomeRegion(home, selected) : new ContinuePlacement(new HomeNode(peer.Region, peer.Address), selected);
    }

    private IResult Answer(SetDatabaseRegionsResult result) => result switch
    {
        ManagedDatabase value => TypedResults.Ok(ManagedDatabaseBody.From(value, options.Region)),
        NotFound error => ManagementRoute.DatabaseNotFound(error),
        Forbidden error => ErrorResponse.From(error),
        Conflict error => ErrorResponse.From(error),
        Validation error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
    };

    private sealed class ContinuePlacement(HomeNode home, IReadOnlyList<string> selected) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => HomeForwarding.ForwardAsync(httpContext, home,
            JsonSerializer.SerializeToUtf8Bytes(new DatabaseRegionsRequest(null, null, null) { Regions = selected }, ManagementJsonContext.Default.DatabaseRegionsRequest));
    }
}
