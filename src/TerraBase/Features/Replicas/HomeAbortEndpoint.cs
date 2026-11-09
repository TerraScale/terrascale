using TerraBase.Errors;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class HomeAbortEndpoint(HomeMoves moves) : Endpoint<HomeAbortRequest, IResult>
{
    public override void Configure()
    {
        Post(HomeMoveRoute.Database + "/promote/abort");
        Name("abortReplicaPromotion");
        Summary("Settles a home move with this region");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Answers whether this region became the home for the "
            + "move; when it did not, it refuses that move from then on.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<HomePromoteAnswer>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
    }

    public override async ValueTask<IResult> HandleAsync(HomeAbortRequest request, CancellationToken cancellationToken) =>
        ReplicaRoutes.Database(HttpContext) is not { } database ? ErrorResponse.From(new NotFound("the database does not exist"))
        : TypedResults.Json(await moves.AbortAsync(database, request, cancellationToken).ConfigureAwait(false), TerraBaseJsonContext.Default.HomePromoteAnswer);
}
