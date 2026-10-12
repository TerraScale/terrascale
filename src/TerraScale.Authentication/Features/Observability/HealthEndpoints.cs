using System.Text.Json.Serialization;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Observability;

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
/// <param name="Error">Why it is not ready, only when an operator must act (<c>no signing keys: operator provisioning required …</c>).</param>
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
        Description("Returns 200 only when the configured TerraBase auth database, required collections, reads and conditional writes are usable, the serving issuer has signing keys with valid authenticated public metadata, and this replica's startup signing-ring bootstrap has completed. ts-auth never creates the issuer's first key itself: without keys it answers not ready with `error` \"no signing keys: operator provisioning required\" until an operator runs `ts-auth provision-signing-key`. A background check runs every five seconds with a two-second deadline and this endpoint answers its latest result. Answers not ready before the first check completes and when the latest check is older than fifteen seconds.");
        Tags("Observability");
        Produces<ReadyBody>();
        Produces<ReadyBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var observation = probe.Latest();
        var body = new ReadyBody(observation.Ready ? "ready" : "not_ready", ServiceVersion.Value, observation.Region, observation.Error);
        return ValueTask.FromResult<IResult>(TypedResults.Json(body, statusCode: observation.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));
    }
}
