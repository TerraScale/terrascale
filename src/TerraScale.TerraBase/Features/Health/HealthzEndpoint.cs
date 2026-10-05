using System.Text.Json.Serialization;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Health;

/// <summary>Probe body.</summary>
/// <param name="Status"><c>ok</c> for liveness; <c>ready</c> or <c>unavailable</c> for readiness.</param>
/// <param name="Version">Service version of the running build.</param>
/// <param name="Region">Region of this node.</param>
public sealed record StatusBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("region")] string Region);

/// <summary><c>GET /healthz</c>: the process is serving requests. Unauthenticated, on the administrative and client listeners.</summary>
public sealed class HealthzEndpoint(TerraBaseOptions options) : EndpointWithoutRequest<StatusBody>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/healthz");
        Name("healthz");
        Summary("Liveness probe");
        Tags("Health");
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    /// <inheritdoc />
    public override ValueTask<StatusBody> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new StatusBody("ok", ServiceVersion.Value, options.Region));
}
