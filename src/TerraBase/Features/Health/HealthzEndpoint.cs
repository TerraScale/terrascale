using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

internal sealed class HealthzEndpoint(TerraBaseOptions options) : EndpointWithoutRequest<StatusBody>
{
    public override void Configure()
    {
        Get("/healthz");
        Name("healthz");
        Summary("Liveness probe");
        Description("Answers while this TerraBase process serves requests; storage format, revision and node name only on the internal listener.");
        Tags("Health");
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    public override ValueTask<StatusBody> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(HealthStatus.For(HttpContext, "ok", options));
}
