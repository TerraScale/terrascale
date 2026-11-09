using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class ReplicaCatalogEndpoint(IReplicaCatalog catalog, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    private const int PageSize = 100;
    // Catalog pages read per answer, so a region with few replicated databases among many still answers promptly.
    private const int MaxScannedPages = 16;

    public override void Configure()
    {
        Get("/v1/sync/regions/{region}/databases");
        Name("listReplicatedDatabases");
        Summary("Lists the databases homed here that a region replicates");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. One page of the databases whose home is this region and "
            + "whose placement names {region} as a replica, with their names and placements. Pass the previous page's next as after.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<ReplicaCatalogPage>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var region = HttpContext.Request.RouteValues["region"] as string;
        if (!TerraBaseOptions.IsRegion(region))
        {
            return ErrorResponse.From(new Validation("region must name a region"));
        }

        TenantContext? after = null;
        if (HttpContext.Request.Query["after"] is [{ Length: > 0 } cursor])
        {
            if (cursor.Split(':') is not [var organization, var project, var database]
                || !EntityIds.IsValid(organization) || !EntityIds.IsValid(project) || !EntityIds.IsValid(database))
            {
                return ErrorResponse.From(new Validation("after must be the next of an earlier page"));
            }

            after = new TenantContext(organization, PlanTier.Hobby, project, database);
        }

        var entries = new List<ReplicaCatalogEntry>(PageSize);
        for (var scanned = 0; entries.Count < PageSize && scanned < MaxScannedPages; scanned++)
        {
            if (await catalog.ListHomedAsync(options.Region, after, cancellationToken).ConfigureAwait(false) is not TenantDatabasePage page)
            {
                return ErrorResponse.From(new Unavailable("the catalog cannot be read"));
            }

            foreach (var database in page.Databases)
            {
                switch (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false))
                {
                    case TenantDatabaseRecord { Placement: var placement } record when placement.Home == options.Region && (region == options.Region || placement.Includes(region)):
                        entries.Add(new ReplicaCatalogEntry(database.OrganizationId, database.ProjectId!, database.DatabaseId!, record.Name,
                            placement.Home, placement.Replicas, placement.Version, placement.AllRegions));
                        break;
                    case Unavailable error:
                        return ErrorResponse.From(error);
                }
            }

            after = page.Next;
            if (after is null)
            {
                break;
            }
        }

        var next = after is null ? null : $"{after.OrganizationId}:{after.ProjectId}:{after.DatabaseId}";
        HttpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Json(new ReplicaCatalogPage(entries, next), TerraBaseJsonContext.Default.ReplicaCatalogPage);
    }
}
