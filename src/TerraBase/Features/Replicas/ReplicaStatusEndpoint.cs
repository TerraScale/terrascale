using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class ReplicaStatusEndpoint(IReplicaCatalog catalog, IReplicaStatus status, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/replica-status");
        Name("getReplicaStatus");
        Summary("Reads the replica status of a database homed here");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. One entry per replica region with its last reported "
            + "state, lag, last pull and last completed verification.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<IReadOnlyList<ReplicaStatusBody>>();
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
            TenantDatabaseRecord { Placement: var placement } when placement.Home == options.Region =>
                TypedResults.Json(status.Local(database, placement.Replicas), TerraBaseJsonContext.Default.IReadOnlyListReplicaStatusBody),
            Unavailable error => ErrorResponse.From(error),
            _ => ErrorResponse.From(new NotFound("the database does not exist")),
        };
    }
}
