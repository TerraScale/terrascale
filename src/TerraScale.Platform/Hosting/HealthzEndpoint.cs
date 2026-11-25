namespace TerraScale.Platform.Hosting;

public sealed class HealthzEndpoint(PlatformOptions options) : EndpointWithoutRequest<HealthStatus>
{
    public override void Configure()
    {
        Get("/healthz");
        Name("healthz");
        Summary("Process liveness");
        Tags("Health");
        AllowAnonymous();
    }

    public override ValueTask<HealthStatus> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new HealthStatus("ok", options.Region));
}
