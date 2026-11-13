using TerraBase.Configuration;
using TerraBase.Features.Health;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

public sealed class BackupHealthEndpoint(IContinuousBackup backup, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/backupz");
        Name("backupz");
        Summary("Backup probe");
        Description("503 while continuous backup is not configured, has not yet listed the files this node is responsible for, or any such file has not finished recovery, has no generation or first upload yet, or holds a committed change that has waited a minute or more to be stored; also 503 when the lag observer has not run for 15 seconds. /readyz does not depend on it.");
        Tags("Health");
        Produces<StatusBody>();
        Produces<StatusBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.FromResult<IResult>(backup.Healthy
            ? TypedResults.Ok(StatusBody.For(HttpContext, "shipping", options))
            : TypedResults.Json(StatusBody.For(HttpContext, "unavailable", options), statusCode: StatusCodes.Status503ServiceUnavailable));
    }
}
