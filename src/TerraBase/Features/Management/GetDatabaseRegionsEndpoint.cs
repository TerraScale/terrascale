using System.Diagnostics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Contracts.Management;

namespace TerraBase.Features.Management;

internal sealed class GetDatabaseRegionsEndpoint(IManagementService management, IDatabaseRevisions revisions, IRegionCatalog regions, IReplicaStatus status,
    TerraBaseOptions options, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/regions");
        Name("getDatabaseRegions");
        Summary("Reads a database's regions and sync progress");
        Description("regions lists every selected region in region-id order, with role writer on the current writer entry. Its order carries no meaning. "
            + "Transitional: homeRegion and replicas keep their behavior until category C23 of batch db4a5ba7 removes the old placement fields once every consumer sends regions. "
            + "Writer-observed successful page pulls, not application or checksum acknowledgements. lagMs is the age of the oldest known commit "
            + "missing from the last served revision, zero when caught up, and null without sufficient observations. lastPulledAt is when a complete "
            + "pull was served. lastVerified is null until checksum verification reports a result. Observations reset when the database reopens. "
            + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<TerraBaseDatabaseRegions>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var found = await management.GetDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext),
            HttpContext.RouteValue("project"), HttpContext.RouteValue("database"), cancellationToken).ConfigureAwait(false);
        if (found is not ManagedDatabase record)
        {
            return found switch
            {
                NotFound error => ManagementRoute.DatabaseNotFound(error),
                Unavailable error => ErrorResponse.From(error),
                ManagedDatabase => throw new UnreachableException(),
            };
        }

        var home = record.Home(options.Region);
        IReadOnlyList<string> replicas = record.AllRegions
            ? [.. (await regions.ServingAsync(cancellationToken).ConfigureAwait(false)).Where(region => region != home)]
            : record.Replicas;
        if (home != options.Region)
        {
            await DatabaseRegions.ProxyAsync(HttpContext, home).ConfigureAwait(false);
            return TypedResults.Empty;
        }

        var tenant = new TenantContext(ManagementRoute.OrganizationId(HttpContext), PlanTier.Hobby, record.ProjectId, record.Id);
        var observations = await revisions.ReplicaStatusAsync(tenant, replicas, cancellationToken).ConfigureAwait(false);
        var verification = status.Local(tenant, replicas).ToDictionary(row => row.Region, row => row.VerifiedAt, StringComparer.Ordinal);
        IReadOnlyList<TerraBaseReplicaStatus> observed = [.. observations.Select(row => new TerraBaseReplicaStatus(row.Region, row.LagMs, row.LastPulledAt,
            verification.GetValueOrDefault(row.Region), row.Revision))];
        IReadOnlyList<TerraBaseReplicaStatus> placed = [.. observed.Append(new TerraBaseReplicaStatus(home, Role: "writer")).OrderBy(row => row.Region, StringComparer.Ordinal)];
        return TypedResults.Json(new TerraBaseDatabaseRegions(record.Id, home, time.GetUtcNow(),
            observed, placed),
            ManagementJson.Default.TerraBaseDatabaseRegions);
    }
}
