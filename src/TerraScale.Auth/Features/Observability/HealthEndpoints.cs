using System.Text.Json.Serialization;
using TerraScale.Auth.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Auth.Features.Observability;

/// <summary>Liveness body.</summary>
/// <param name="Status">Always <c>ok</c>.</param>
/// <param name="Version">Service version of the running build.</param>
public sealed record StatusBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version);

/// <summary>Readiness body.</summary>
/// <param name="Status"><c>ready</c> or <c>not_ready</c>.</param>
/// <param name="Version">Service version of the running build.</param>
/// <param name="Region">Storage authority region, when it answered.</param>
public sealed record ReadyBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("region"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Region);

/// <summary><c>GET /healthz</c>: the process is serving requests.</summary>
public sealed class HealthzEndpoint : EndpointWithoutRequest<StatusBody>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/healthz");
        Name("healthz");
        Summary("Liveness probe");
        Tags("Observability");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override Task<StatusBody> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new StatusBody("ok", ServiceVersion.Value));
}

/// <summary><c>GET /readyz</c>: the storage authority answered a probe within two seconds.</summary>
public sealed class ReadyzEndpoint(ReadinessProbe probe) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/readyz");
        Name("readyz");
        Summary("Readiness probe");
        Description("Returns 200 only when the configured TerraBase auth database, required collections, reads and conditional writes are usable. Observations are cached for at most one second; probe deadline is two seconds.");
        Tags("Observability");
        Produces<ReadyBody>();
        Produces<ReadyBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async Task<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var observation = await probe.ObserveAsync(cancellationToken);
        var body = new ReadyBody(observation.Ready ? "ready" : "not_ready", ServiceVersion.Value, observation.Region);
        return TypedResults.Json(body, statusCode: observation.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
