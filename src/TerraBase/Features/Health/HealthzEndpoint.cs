using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;
public sealed record StatusBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("storageFormat"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StorageFormat = null,
    [property: JsonPropertyName("revision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Revision = null,
    [property: JsonPropertyName("machine"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Machine = null)
{
    public static StatusBody For(HttpContext context, string status, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        return Listeners.ArrivedOn(context) == TerraBaseListener.Internal
            ? new(status, ServiceVersion.Value, options.Region, Engine.Turso.StorageFormat.Id, ServiceVersion.Revision, options.MachineId)
            : new(status, ServiceVersion.Value, options.Region);
    }
}
public sealed class HealthzEndpoint(TerraBaseOptions options) : EndpointWithoutRequest<StatusBody>
{
    public override void Configure()
    {
        Get("/healthz");
        Name("healthz");
        Summary("Liveness probe");
        Description("Answers while this TerraBase process serves requests; available anonymously on the client listener with status, version and region only; storage format, revision and node name need the internal listener's credential.");
        Tags("Health");
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }
    public override ValueTask<StatusBody> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(StatusBody.For(HttpContext, "ok", options));
}
