using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

public sealed class ReadyzEndpoint(
    INodeReadiness readiness, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/readyz");
        Name("readyz");
        Summary("Readiness probe");
        Description("503 while the storage engine is closed, the data directory is not writable, ts-auth's signing keys have not been fetched yet (when auth-issuer is set), a catalog follower has not caught up with its primary yet or the server is shutting down.");
        Tags("Health");
        Produces<StatusBody>();
        Produces<StatusBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        return await readiness.IsReadyAsync(cancellationToken).ConfigureAwait(false)
            ? TypedResults.Ok(StatusBody.For(HttpContext, "ready", options))
            : TypedResults.Json(StatusBody.For(HttpContext, "unavailable", options), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
