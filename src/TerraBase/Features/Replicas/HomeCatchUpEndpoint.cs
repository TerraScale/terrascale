using TerraBase.Errors;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class HomeCatchUpEndpoint(HomeMoves moves) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post(HomeMoveRoute.Database + "/catch-up");
        Name("catchUpReplica");
        Summary("Pulls this region's replica of a database now");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. The first step of moving a database's home here: "
            + "answers 204 once this region's replica holds the home's newest revision, 503 when it holds no replica.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ReplicaRoutes.Database(HttpContext) is not { } database ? ErrorResponse.From(new NotFound("the database does not exist"))
        : await moves.CatchUpAsync(database, cancellationToken).ConfigureAwait(false) ? TypedResults.NoContent()
        : ErrorResponse.From(HomeMoves.ReplicaNotReady);
}
