using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Contracts.Management;

namespace TerraBase.Features.Management;

/// <summary>Placement and successful page pulls observed by this database's home.</summary>
internal sealed class GetDatabaseRegionsEndpoint(IManagementService management, IDatabaseRevisions revisions,
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
                _ => throw new InvalidOperationException("unexpected database lookup result"),
            };
        }

        if (record.Home(options.Region) != options.Region)
        {
            return DatabaseRegions.HomeRegion(record.Home(options.Region), record.Replicas.Prepend(record.Home(options.Region)));
        }

        var tenant = new TenantContext(ManagementRoute.OrganizationId(HttpContext), PlanTier.Hobby, record.ProjectId, record.Id);
        var observations = await revisions.ReplicaStatusAsync(tenant, record.Replicas, cancellationToken).ConfigureAwait(false);
        return TypedResults.Json(new TerraBaseDatabaseRegions(record.Id, record.Home(options.Region), time.GetUtcNow(),
            [.. observations.Select(row => new TerraBaseReplicaStatus(row.Region, row.LagMs, row.LastPulledAt, Revision: row.Revision))]),
            ManagementJson.Default.TerraBaseDatabaseRegions);
    }
}
