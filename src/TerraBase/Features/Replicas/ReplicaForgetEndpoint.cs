using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

/// <summary>
/// <c>DELETE /v1/sync/organizations/{organization}/projects/{project}/databases/{database}</c>: a database's home
/// deleted it, so this region forgets its record and stops its replica. Internal listener only, behind the peer
/// credential. Idempotent: a database this region does not record answers 204 as well.
/// </summary>
internal sealed class ReplicaForgetEndpoint(RegionReplication replication, IReplicaCatalog catalog) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}");
        Name("forgetReplicatedDatabase");
        Summary("Forgets a database its home deleted");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Stops this region's "
            + "replica of the database and deletes its record of it, because the home region deleted the database. "
            + "A database this region does not record, or homes itself, is already forgotten. 204 either way.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (ReplicaRoutes.Database(HttpContext) is not { } database)
        {
            return ErrorResponse.From(new NotFound("the database does not exist"));
        }

        await replication.DeleteShreddedAsync(database, cancellationToken).ConfigureAwait(false);
        return await catalog.ForgetAsync(database, cancellationToken).ConfigureAwait(false) switch
        {
            DatabaseForgotten => TypedResults.NoContent(),
            Unavailable error => ErrorResponse.From(error),
            _ => throw new InvalidOperationException(),
        };
    }
}
