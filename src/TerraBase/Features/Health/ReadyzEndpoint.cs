using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

internal sealed class ReadyzEndpoint(TerraBaseOptions options, NodeReadiness readiness, IHostApplicationLifetime lifetime)
    : EndpointWithoutRequest<ReadinessResponse>
{
    public override void Configure()
    {
        Get("/readyz");
        Name("readyz");
        Summary("Readiness probe");
        Description("200 once the system database, platform databases and listeners are ready; 503 while starting or stopping.");
        Tags("Health");
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    public override ValueTask<ReadinessResponse> HandleAsync(CancellationToken cancellationToken)
    {
        var blocked = lifetime.ApplicationStopping.IsCancellationRequested ? "stopping" : readiness.Blocked;
        ReadinessResponse result = blocked is { } reason
            ? new UnavailableStatus<StatusBody>(HealthStatus.For(HttpContext, "unavailable", options, reason), HealthJsonContext.Default)
            : HealthStatus.For(HttpContext, "ok", options);
        return ValueTask.FromResult(result);
    }
}
