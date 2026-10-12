using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Engine.Sqlite;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

/// <summary>Probe body.</summary>
/// <param name="Status"><c>ok</c> for liveness; <c>ready</c> or <c>unavailable</c> for readiness.</param>
/// <param name="Version">Service version of the running build.</param>
/// <param name="Region">Region of this node.</param>
/// <param name="StorageFormat">On-disk storage format this build writes (<see cref="Engine.Sqlite.StorageFormat.Id"/>); the
/// deploy gate compares it with a new image's before switching the region.</param>
/// <param name="Revision">Source commit of the running build (<see cref="ServiceVersion.Revision"/>), or null when the build
/// recorded none.</param>
/// <param name="Machine">This node's machine (<c>FLY_MACHINE_ID</c>; <c>local</c> off Fly): which exact machine answered.</param>
public sealed record StatusBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("storageFormat")] string StorageFormat,
    [property: JsonPropertyName("revision")] string? Revision,
    [property: JsonPropertyName("machine")] string Machine);

/// <summary><c>GET /healthz</c>: the process is serving requests. Unauthenticated, on the administrative and client listeners.</summary>
public sealed class HealthzEndpoint(TerraBaseOptions options) : EndpointWithoutRequest<StatusBody>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/healthz");
        Name("healthz");
        Summary("Liveness probe");
        Description("Answers while this TerraBase process serves requests; available anonymously on the client and administrative listeners.");
        Tags("Health");
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    /// <inheritdoc />
    public override ValueTask<StatusBody> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new StatusBody("ok", ServiceVersion.Value, options.Region, StorageFormat.Id, ServiceVersion.Revision, options.MachineId));
}
