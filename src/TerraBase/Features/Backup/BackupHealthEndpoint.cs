using TerraBase.Configuration;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Health;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

/// <summary>
/// <c>GET /backupz</c>: 200 once backup is configured, has found this node's files, and every file the node ships has
/// reached the bucket with less than a minute of lag; 503 otherwise. A member that ships no file passes once its files
/// are found. The deploy's live check waits for it (scripts/fly/deploy.sh). It is not a Fly
/// health check, so a bucket outage never takes a node out of service.
/// </summary>
public sealed class BackupHealthEndpoint(IContinuousBackup backup, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    private const double MaxLagSeconds = 60;

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/backupz");
        Name("backupz");
        Summary("Backup probe");
        Description("503 while continuous backup is not configured, has not found this node's files yet, or a file this node ships has not reached the bucket yet or lags by a minute or more.");
        Tags("Health");
        Produces<StatusBody>();
        Produces<StatusBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var status = backup.Status;
        var shipping = status.Shipping && status.Files.Count > 0
            && status.Files.Where(file => file.Shipping).All(file => file.LastShippedAt is not null && file.LagSeconds < MaxLagSeconds);
        return ValueTask.FromResult<IResult>(shipping
            ? TypedResults.Ok(new StatusBody("shipping", ServiceVersion.Value, options.Region, StorageFormat.Id, ServiceVersion.Revision, options.MachineId))
            : TypedResults.Json(new StatusBody("unavailable", ServiceVersion.Value, options.Region, StorageFormat.Id, ServiceVersion.Revision, options.MachineId), statusCode: StatusCodes.Status503ServiceUnavailable));
    }
}
