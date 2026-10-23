using System.Text.Json.Serialization;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Observability;

/// <summary>Liveness body.</summary>
/// <param name="Status">Always <c>ok</c>.</param>
/// <param name="Version">Service version of the running build, without its revision (<see cref="ServiceVersion.Public"/>).</param>
public sealed record StatusBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version);

/// <summary>Readiness body.</summary>
/// <param name="Status"><c>ready</c> or <c>not_ready</c>.</param>
/// <param name="Version">Service version of the running build, without its revision (<see cref="ServiceVersion.Public"/>).</param>
/// <param name="Region">Storage authority region, when it answered.</param>
/// <param name="Error">Why it is not ready, when signing-key bootstrap is pending (<c>no signing keys: startup bootstrap pending …</c>).</param>
public sealed record ReadyBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("region"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Region,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

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
        ValueTask.FromResult(new StatusBody("ok", ServiceVersion.Public));
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
        Description("Returns 200 only when the configured TerraBase auth database, required collections, reads and conditional writes are usable, the serving issuer has signing keys with valid authenticated public metadata, and this replica's startup signing-ring bootstrap has completed. Startup seeds the platform owner and projects and provisions the first encrypted signing key when absent; existing unusable keys fail closed without replacement. A background check runs five seconds after the previous one ends, with an eight-second deadline, and this endpoint answers its latest result. Answers not ready before the first check completes and when the latest check is older than fifteen seconds.");
        Tags("Observability");
        Produces<ReadyBody>();
        Produces<ReadyBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var observation = probe.Latest();
        var body = new ReadyBody(observation.Ready ? "ready" : "not_ready", ServiceVersion.Public, observation.Region, observation.Error);
        return ValueTask.FromResult<IResult>(TypedResults.Json(body, statusCode: observation.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));
    }
}
