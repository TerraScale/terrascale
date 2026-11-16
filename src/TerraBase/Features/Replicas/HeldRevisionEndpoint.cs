using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class HeldRevisionEndpoint(TerraBaseOptions options, IDatabaseReplicas replicas) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/held");
        Name("getReplicaHeldRevision");
        Summary("Reads the revision this node's replica of a database holds");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Shows which position this node's replica of a "
            + "database holds, so an operator can compare replicas. 404 when this node holds no pulled replica.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<HeldRevisionAnswer>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.FromResult(ReplicaRoutes.Database(HttpContext) is { } database
            && replicas.HeldRevision(database) is { } held
            ? TypedResults.Json(new HeldRevisionAnswer(options.Region, held.ToString()), ReplicasJsonContext.Default.HeldRevisionAnswer)
            : ErrorResponse.From(new NotFound("this node holds no replica of the database")));
    }
}
