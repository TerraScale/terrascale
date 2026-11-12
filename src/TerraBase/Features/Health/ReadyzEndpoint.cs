using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

public sealed class ReadyzEndpoint(
    INodeReadiness readiness, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/readyz");
        Name("readyz");
        Summary("Readiness probe");
        Description("503 while the storage engine is closed or its fault ledger has failed closed, the data directory is not writable or its write probe has not answered within 2 s, the catalog has not passed its restore gate, the platform database seed is still pending or the server is shutting down, and in a cluster until this node knows its region's writer and has started its home push and leases. The seed holds readiness until each platform database exists or is in a state that only an operator or the file audit changes: another record on the platform organization or a platform project id, another database or another placement under a platform database name, a closed platform project, a permanently deleted platform database id, or a platform database file without a catalog record. Until then a closed restore gate, an unavailable answer (busy storage, unavailable storage accounting, a cleanup intent, a catalog format this build does not support) or a failed attempt keeps it held; afterwards it is never held again, and a file without a catalog record keeps the seed retrying every 5 seconds in the background. While the seed is still trying, the terrabase.platform.seed_pending gauge names the reason.");
        Tags("Health");
        Produces<StatusBody>();
        Produces<StatusBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        return await readiness.IsReadyAsync(cancellationToken).ConfigureAwait(false)
            ? TypedResults.Ok(StatusBody.For(HttpContext, "ready", options))
            : TypedResults.Json(StatusBody.For(HttpContext, "unavailable", options), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
