using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed class RegionSetPlacement(IManagementService management, TerraBaseOptions options, IHomeApply apply, HomeMoves moves, IRegionPeers peers)
{
    public async ValueTask<IResult> UpdateAsync(HttpContext context, ManagedDatabase database, IReadOnlyList<string> selected, CancellationToken cancellationToken)
    {
        var user = ManagementRoute.UserId(context);
        var organization = ManagementRoute.OrganizationId(context);
        var home = database.Home(options.Region);
        if (selected.Contains(home, StringComparer.Ordinal))
        {
            return Answer(await SetAsync(user, organization, database, DatabaseRegions.AllBut(selected, home), false, cancellationToken).ConfigureAwait(false));
        }

        IReadOnlyList<string> candidates = [.. selected.Order(StringComparer.Ordinal)];
        var staged = false;
        if (!database.AllRegions && candidates.Any(region => !database.Replicas.Contains(region, StringComparer.Ordinal)))
        {
            SetDatabaseRegionsResult added;
            try
            {
                added = await SetAsync(user, organization, database,
                    DatabaseRegions.AllBut(database.Replicas.Concat(candidates), home), false, cancellationToken).ConfigureAwait(false);
            }
            catch (StorageBusyException)
            {
                var restored = await SetAsync(user, organization, database, database.Replicas, database.AllRegions, CancellationToken.None).ConfigureAwait(false);
                return restored is ManagedDatabase ? ErrorResponse.From(HomeMoves.ReplicaNotReady) : Answer(restored);
            }

            if (added is not ManagedDatabase)
            {
                return Answer(added);
            }

            staged = true;
        }

        var tenant = new TenantContext(organization, PlanTier.Hobby, database.ProjectId, database.Id);
        foreach (var target in candidates)
        {
            var result = await moves.MoveAsync(tenant, target, cancellationToken).ConfigureAwait(false);
            if (result is Unavailable refused && refused == HomeMoves.ReplicaNotReady)
            {
                continue;
            }

            return result switch
            {
                ManagedDatabase moved => await ContinueAsync(moved, selected, cancellationToken).ConfigureAwait(false),
                Unavailable error => ErrorResponse.From(error),
                NotFound error => ManagementRoute.DatabaseNotFound(error),
                Validation error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
            };
        }

        if (staged)
        {
            var restored = await SetAsync(user, organization, database, database.Replicas, database.AllRegions, CancellationToken.None).ConfigureAwait(false);
            if (restored is not ManagedDatabase)
            {
                return Answer(restored);
            }
        }

        return ErrorResponse.From(HomeMoves.ReplicaNotReady);
    }

    private async ValueTask<IResult> ContinueAsync(ManagedDatabase database, IReadOnlyList<string> selected, CancellationToken cancellationToken)
    {
        var home = database.Home(options.Region);
        var peer = (await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(region => region.Region == home);
        return peer is null ? DatabaseRegions.HomeRegion(home, selected) : new ContinuePlacement(new HomeNode(peer.Region, peer.Address), selected);
    }

    private async ValueTask<SetDatabaseRegionsResult> SetAsync(string user, string organization, ManagedDatabase database, IReadOnlyList<string> replicas,
        bool allRegions, CancellationToken cancellationToken) =>
        await apply.PrepareReplicatedAsync(organization, options.Region,
            await management.SetDatabaseRegionsAsync(user, organization, database.ProjectId, database.Id, replicas, cancellationToken, allRegions).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    private IResult Answer(SetDatabaseRegionsResult result) => result switch
    {
        ManagedDatabase value => TypedResults.Ok(ManagedDatabaseBody.From(value, options.Region)),
        NotFound error => ManagementRoute.DatabaseNotFound(error),
        Forbidden error => ErrorResponse.From(error),
        Conflict error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
    };

    private sealed class ContinuePlacement(HomeNode home, IReadOnlyList<string> selected) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => HomeForwarding.ForwardAsync(httpContext, home,
            JsonSerializer.SerializeToUtf8Bytes(new DatabaseRegionsRequest(null, null, null) { Regions = selected }, ManagementJsonContext.Default.DatabaseRegionsRequest));
    }
}
