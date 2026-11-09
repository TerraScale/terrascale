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
        Summary("Reads a database's home and replica progress");
        Description("Home-observed successful page pulls, not application or checksum acknowledgements. lagMs is the age of the oldest known commit "
            + "missing from the last served revision, zero when caught up, and null without sufficient observations. lastPulledAt is when a complete "
            + "pull was served. lastVerified is null until checksum verification reports a result. Observations reset when the database reopens. "
            + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<TerraBaseDatabaseRegions>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var found = await management.GetDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext),
            ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"), cancellationToken).ConfigureAwait(false);
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
            return DatabaseRegions.HomeRegion(home, replicas.Prepend(home));
        }

        var tenant = new TenantContext(ManagementRoute.OrganizationId(HttpContext), PlanTier.Hobby, record.ProjectId, record.Id);
        var observations = await revisions.ReplicaStatusAsync(tenant, replicas, cancellationToken).ConfigureAwait(false);
        var verification = status.Local(tenant, replicas).ToDictionary(row => row.Region, row => row.VerifiedAt, StringComparer.Ordinal);
        return TypedResults.Json(new TerraBaseDatabaseRegions(record.Id, home, time.GetUtcNow(),
            [.. observations.Select(row => new TerraBaseReplicaStatus(row.Region, row.LagMs, row.LastPulledAt,
                verification.GetValueOrDefault(row.Region), row.Revision))]),
            ManagementJson.Default.TerraBaseDatabaseRegions);
    }
}
