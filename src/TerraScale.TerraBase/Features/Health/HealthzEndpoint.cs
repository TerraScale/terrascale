using System.Text.Json.Serialization;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Health;

/// <summary>Liveness body.</summary>
/// <param name="Status">Always <c>ok</c>.</param>
/// <param name="Version">Service version of the running build.</param>
public sealed record StatusBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version);

/// <summary><c>GET /healthz</c>: the process is serving requests.</summary>
public sealed class HealthzEndpoint : EndpointWithoutRequest<StatusBody>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/healthz");
        Name("healthz");
        Summary("Liveness probe");
        Tags("Health");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<StatusBody> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new StatusBody("ok", ServiceVersion.Value));
}
