using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;
using TursoStorageFormat = TerraBase.Engine.Turso.StorageFormat;

namespace TerraBase.Features.Health;

public sealed record StatusBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("storageFormat"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StorageFormat = null,
    [property: JsonPropertyName("revision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Revision = null,
    [property: JsonPropertyName("machine"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Machine = null,
    [property: JsonPropertyName("bucket"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Bucket = null)
{
    public static StatusBody For(HttpContext context, string status, TerraBaseOptions options, string? bucket = null) =>
        Listeners.ArrivedOn(context) == TerraBaseListener.Internal
            ? new(status, ServiceVersion.Value, options.Region, TursoStorageFormat.Id, ServiceVersion.Revision, options.MachineId, bucket)
            : new(status, ServiceVersion.Value, options.Region);
}
public sealed class HealthzEndpoint(TerraBaseOptions options, ICatalogGate catalog) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/healthz");
        Name("healthz");
        Summary("Liveness probe");
        Description("Answers while this TerraBase process serves requests; available anonymously on the client listener with status, version and region only; storage format, revision and node name need the internal listener's credential. Status is degraded, with detail, while the backup bucket holding the catalog restore anchor does not exist (bucket_missing).");
        Tags("Health");
        Produces<StatusBody>();
        Produces<StatusBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var status = catalog.Status;
        IResult result = status.State == CatalogGateState.BucketMissing
            ? TypedResults.Json(StatusBody.For(HttpContext, "degraded", options, status.Bucket), statusCode: StatusCodes.Status503ServiceUnavailable)
            : TypedResults.Ok(StatusBody.For(HttpContext, "ok", options));
        return ValueTask.FromResult(result);
    }
}
