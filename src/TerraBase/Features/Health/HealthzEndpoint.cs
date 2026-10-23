using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

/// <summary>Probe body.</summary>
/// <param name="Status"><c>ok</c> for liveness; <c>ready</c> or <c>unavailable</c> for readiness.</param>
/// <param name="Version">Service version of the running build.</param>
/// <param name="Region">Region of this node.</param>
/// <param name="StorageFormat">On-disk storage format this build writes (<see cref="Engine.Turso.StorageFormat.Id"/>); the
/// deploy gate compares it with a new image's before switching the region. Internal listener only.</param>
/// <param name="Revision">Source commit of the running build (<see cref="ServiceVersion.Revision"/>), omitted when the build
/// recorded none. Internal listener only.</param>
/// <param name="Machine">This node's name (<c>TS_NODE</c>; <c>local</c> outside a cluster): which exact node answered.
/// Internal listener only; the public listener names it in <c>X-TerraBase-Machine</c>.</param>
public sealed record StatusBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("storageFormat"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StorageFormat = null,
    [property: JsonPropertyName("revision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Revision = null,
    [property: JsonPropertyName("machine"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Machine = null)
{
    /// <summary>
    /// The probe body for this request: build detail (storage format, revision, node) only on the internal listener, whose
    /// requests carry the peer credential; anonymous public callers get status, version and region, so an exact commit
    /// never helps match a running build to known bugs.
    /// </summary>
    public static StatusBody For(HttpContext context, string status, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        return Listeners.ArrivedOn(context) == TerraBaseListener.Internal
            ? new(status, ServiceVersion.Value, options.Region, Engine.Turso.StorageFormat.Id, ServiceVersion.Revision, options.MachineId)
            : new(status, ServiceVersion.Value, options.Region);
    }
}

/// <summary><c>GET /healthz</c>: the process is serving requests. Unauthenticated on the client listener (status only), with build
/// detail on the internal listener.</summary>
public sealed class HealthzEndpoint(TerraBaseOptions options) : EndpointWithoutRequest<StatusBody>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/healthz");
        Name("healthz");
        Summary("Liveness probe");
        Description("Answers while this TerraBase process serves requests; available anonymously on the client listener with status, version and region only; storage format, revision and node name need the internal listener's peer credential.");
        Tags("Health");
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    /// <inheritdoc />
    public override ValueTask<StatusBody> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(StatusBody.For(HttpContext, "ok", options));
}
