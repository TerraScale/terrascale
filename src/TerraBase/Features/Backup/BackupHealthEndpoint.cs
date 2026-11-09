using TerraBase.Configuration;
using TerraBase.Features.Health;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

public sealed class BackupHealthEndpoint(IContinuousBackup backup, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    private const double MaxLagSeconds = 60;

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

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var status = backup.Status;
        var shipping = status.Shipping && status.Files.Count > 0
            && status.Files.Where(file => file.Shipping).All(file => file.LastShippedAt is not null && file.LagSeconds < MaxLagSeconds);
        return ValueTask.FromResult<IResult>(shipping
            ? TypedResults.Ok(StatusBody.For(HttpContext, "shipping", options))
            : TypedResults.Json(StatusBody.For(HttpContext, "unavailable", options), statusCode: StatusCodes.Status503ServiceUnavailable));
    }
}
