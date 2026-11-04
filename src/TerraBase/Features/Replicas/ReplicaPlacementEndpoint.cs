using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

/// <summary>
/// <c>GET /v1/sync/organizations/{organization}/projects/{project}/databases/{database}/placement</c>: the placement of a database
/// homed on this node, so a replica region tells a database that left its placement from one that was deleted. Internal
/// listener only, behind the peer credential.
/// </summary>
internal sealed class ReplicaPlacementEndpoint(IReplicaCatalog catalog, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/placement");
        Name("getReplicatedDatabasePlacement");
        Summary("Reads the placement of a database homed here");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. The database's name and placement, or 404 when this "
            + "node's catalog does not record it as homed here (it was deleted).");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<ReplicaCatalogEntry>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (organization, project, id) = (Value("organization"), Value("project"), Value("database"));
        if (!EntityIds.IsValid(organization) || !EntityIds.IsValid(project) || !EntityIds.IsValid(id))
        {
            return ErrorResponse.From(new NotFound("the database does not exist"));
        }

        var database = new TenantContext(organization, PlanTier.Hobby, project, id);
        HttpContext.Response.Headers.CacheControl = "no-store";
        return await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) switch
        {
            TenantDatabaseRecord { Placement: var placement } record when placement.Home == options.Region => TypedResults.Json(
                new ReplicaCatalogEntry(organization, project, id, record.Name, placement.Home, placement.Replicas, placement.Version),
                TerraBaseJsonContext.Default.ReplicaCatalogEntry),
            Unavailable error => ErrorResponse.From(error),
            _ => ErrorResponse.From(new NotFound("the database does not exist")),
        };
    }

    private string Value(string name) => HttpContext.Request.RouteValues[name] as string ?? string.Empty;
}
