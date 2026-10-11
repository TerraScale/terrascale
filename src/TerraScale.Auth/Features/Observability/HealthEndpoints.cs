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
    public override ValueTask<StatusBody> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new StatusBody("ok", ServiceVersion.Value));
}

/// <summary><c>GET /readyz</c>: storage, the signing-ring bootstrap and public signing metadata were usable at the latest background check.</summary>
public sealed class ReadyzEndpoint(ReadinessProbe probe) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/readyz");
        Name("readyz");
        Summary("Readiness probe");
        Description("Returns 200 only when the configured TerraBase auth database, required collections, reads and conditional writes are usable, this replica's startup signing-ring bootstrap has completed, and the serving issuer's signing ring, read from its OCI Vault secret, has valid authenticated public metadata and a clock within five minutes of this replica's. A background check runs every five seconds with a two-second deadline and this endpoint answers its latest result; the Vault read is cached for five minutes after it succeeds. Answers not ready before the first check completes and when the latest check is older than fifteen seconds.");
        Tags("Observability");
        Produces<ReadyBody>();
        Produces<ReadyBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var observation = probe.Latest();
        var body = new ReadyBody(observation.Ready ? "ready" : "not_ready", ServiceVersion.Value, observation.Region);
        return ValueTask.FromResult<IResult>(TypedResults.Json(body, statusCode: observation.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));
    }
}
