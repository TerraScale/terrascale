using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class ReplicaPlacementEndpoint(IReplicaCatalog catalog, TerraBaseOptions options, RegionalOwners owners) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/placement");
        Name("getReplicatedDatabasePlacement");
        Summary("Reads the placement this node records for a database");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. The database's name and the placement this node "
            + "records, which names its home after a move. Only the regional writer can answer; other nodes return 503. A missing record at the writer returns 404.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<ReplicaCatalogEntry>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (!owners.Writer)
        {
            return ErrorResponse.From(new Unavailable("this machine is not the regional writer", "not_writer"));
        }

        if (ReplicaRoutes.Database(HttpContext) is not { } database)
        {
            return ErrorResponse.From(new NotFound("the database does not exist"));
        }

        return await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) switch
        {
            TenantDatabaseRecord { Placement: var placement } record => TypedResults.Json(
                new ReplicaCatalogEntry(database.OrganizationId, database.ProjectId!, database.DatabaseId!, record.Name,
                    placement.Home, placement.Replicas, placement.Version, placement.AllRegions, placement.Home == options.Region ? record.Identity : null),
                ReplicasJsonContext.Default.ReplicaCatalogEntry),
            Unavailable error => ErrorResponse.From(error),
            NotFound when !owners.Writer => ErrorResponse.From(new Unavailable("this machine is not the regional writer", "not_writer")),
            NotFound => ErrorResponse.From(new NotFound("the database does not exist")),
        };
    }
}
