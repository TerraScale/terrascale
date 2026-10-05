using TerraScale.Endpoints;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Health;

/// <summary>
/// <c>GET /readyz</c>: 200 only while the storage engine is open, the data directory is writable, ts-auth's signing keys
/// are loaded (when <c>auth-issuer</c> is set) and the server is not shutting down; 503 otherwise. Unauthenticated, on the administrative and client listeners, for load balancers.
/// </summary>
public sealed class ReadyzEndpoint(IStorageEngine engine, IIssuerKeys issuerKeys, IHostApplicationLifetime lifetime, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/readyz");
        Name("readyz");
        Summary("Readiness probe");
        Description("503 while the storage engine is closed, the data directory is not writable, ts-auth's signing keys have not been fetched yet (when auth-issuer is set) or the server is shutting down.");
        Tags("Health");
        Produces<StatusBody>();
        Produces<StatusBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        // Until ts-auth's keys are fetched no user can authenticate: not ready, rather than accepting no tokens.
        var ready = false;
        if (issuerKeys.IsReady && !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
            try
            {
                ready = await engine.IsReadyAsync(stopping.Token);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                // Shutdown ends an in-flight wait immediately without cancelling the shared disk probe.
            }
        }

        return ready && !lifetime.ApplicationStopping.IsCancellationRequested
            ? TypedResults.Ok(new StatusBody("ready", ServiceVersion.Value, options.Region))
            : TypedResults.Json(new StatusBody("unavailable", ServiceVersion.Value, options.Region), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
