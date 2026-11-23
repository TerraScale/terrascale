using System.Text.Json.Serialization;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Observability;

public sealed record StatusBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version);

public sealed record ReadyBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("region"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Region,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

public sealed class HealthzEndpoint : EndpointWithoutRequest<StatusBody>
{
    public override void Configure()
    {
        Get("/healthz");
        Name("healthz");
        Summary("Liveness probe");
        Tags("Observability");
        AllowAnonymous();
    }

    public override ValueTask<StatusBody> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers["X-TerraScale-Revision"] = ServiceVersion.Revision;
        return ValueTask.FromResult(new StatusBody("ok", ServiceVersion.Revision));
    }
}

public sealed class ReadyzEndpoint(ReadinessProbe probe) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/readyz");
        Name("readyz");
        Summary("Readiness probe");
        Description("Returns 200 only when the configured TerraBase auth database, required tables, reads and conditional writes are usable, the serving issuer has signing keys with valid authenticated public metadata, and this replica's startup signing-ring bootstrap has completed. Startup seeds the platform owner and projects and provisions the first encrypted signing key when absent; existing unusable keys fail closed without replacement. A background check runs five seconds after the previous one ends, with an eight-second deadline, and this endpoint answers its latest result. Answers not ready before the first check completes and when the latest check is older than fifteen seconds.");
        Tags("Observability");
        Produces<ReadyBody>();
        Produces<ReadyBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var observation = probe.Latest();
        this.HttpContext.Response.Headers["X-TerraScale-Revision"] = ServiceVersion.Revision;
        var body = new ReadyBody(observation.Ready ? "ready" : "not_ready", ServiceVersion.Revision, observation.Region, observation.Error);
        return ValueTask.FromResult<IResult>(TypedResults.Json(body, statusCode: observation.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));
    }
}
