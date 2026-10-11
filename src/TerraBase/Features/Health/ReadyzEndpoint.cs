using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

/// <summary>
/// <c>GET /readyz</c>: 200 only while the storage engine is open, the data directory is writable, ts-auth's signing keys
/// are loaded (when <c>auth-issuer</c> is set), a catalog follower has caught up with its primary once and the server is
/// not shutting down; 503 otherwise. Unauthenticated, on the administrative and client listeners, for load balancers.
/// </summary>
public sealed class ReadyzEndpoint(
    INodeReadiness readiness, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
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

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        return await readiness.IsReadyAsync(cancellationToken).ConfigureAwait(false)
            ? TypedResults.Ok(new StatusBody("ready", ServiceVersion.Value, options.Region))
            : TypedResults.Json(new StatusBody("unavailable", ServiceVersion.Value, options.Region), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
