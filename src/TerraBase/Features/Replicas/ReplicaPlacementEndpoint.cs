using TerraBase.Errors;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class ReplicaPlacementEndpoint(IReplicaCatalog catalog) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/placement");
        Name("getReplicatedDatabasePlacement");
        Summary("Reads the placement this node records for a database");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. The database's name and the placement this node "
            + "records, which names its home after a move, or 404 when this node's catalog does not record it (it was deleted).");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<ReplicaCatalogEntry>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (ReplicaRoutes.Database(HttpContext) is not { } database)
        {
            return ErrorResponse.From(new NotFound("the database does not exist"));
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        return await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) switch
        {
            TenantDatabaseRecord { Placement: var placement } record => TypedResults.Json(
                new ReplicaCatalogEntry(database.OrganizationId, database.ProjectId!, database.DatabaseId!, record.Name,
                    placement.Home, placement.Replicas, placement.Version, placement.AllRegions),
                ReplicasJsonContext.Default.ReplicaCatalogEntry),
            Unavailable error => ErrorResponse.From(error),
            _ => ErrorResponse.From(new NotFound("the database does not exist")),
        };
    }
}
