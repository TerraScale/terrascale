using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

internal sealed class ReadyzEndpoint(TerraBaseOptions options, NodeReadiness readiness, IHostApplicationLifetime lifetime)
    : EndpointWithoutRequest<Result<StatusBody, UnavailableStatus<StatusBody>>>
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

    public override ValueTask<Result<StatusBody, UnavailableStatus<StatusBody>>> HandleAsync(CancellationToken cancellationToken)
    {
        var blocked = lifetime.ApplicationStopping.IsCancellationRequested ? "stopping" : readiness.Blocked;
        Result<StatusBody, UnavailableStatus<StatusBody>> result = blocked is { } reason
            ? new UnavailableStatus<StatusBody>(HealthStatus.For(HttpContext, "unavailable", options, reason), HealthJsonContext.Default)
            : HealthStatus.For(HttpContext, "ok", options);
        return ValueTask.FromResult(result);
    }
}
