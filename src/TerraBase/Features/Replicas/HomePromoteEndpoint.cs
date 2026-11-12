using TerraBase.Errors;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class HomePromoteEndpoint(HomeMoves moves) : Endpoint<HomePromoteRequest, IResult>
{
    public override void Configure()
    {
        Post(HomeMoveRoute.Database + "/promote");
        Name("promoteReplica");
        Summary("Makes this region the home of a database");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Sent by the paused home: this region pulls its replica "
            + "until a pull is empty, turns it into the home file and records itself as home at the given placement version. 409 when the move was "
            + "abandoned or the placement differs; 409 name_taken if another database of the project holds the name in this region. Rename one of them and repeat.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<HomePromoteAnswer>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(HomePromoteRequest request, CancellationToken cancellationToken) =>
        ReplicaRoutes.Database(HttpContext) is not { } database ? ErrorResponse.From(new NotFound("the database does not exist"))
        : await moves.PromoteAsync(database, request, cancellationToken).ConfigureAwait(false) switch
        {
            HomePromoteAnswer answer => TypedResults.Json(answer, ReplicasJsonContext.Default.HomePromoteAnswer),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
