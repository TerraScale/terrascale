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
        Description("503 while the storage engine is closed or its fault ledger has failed closed, the data directory is not writable or its write probe has not answered within 2 s, the catalog has not passed its restore gate, the platform database seed is still pending or the server is shutting down, and in a cluster until this node knows its region's writer and has started its home push and leases. The seed stops holding readiness once no platform database is pending: each exists or is in a state only an operator can change, such as another record on the platform organization or a platform project id, another database or another placement under a platform database name, a closed platform project or a permanently deleted platform database id. While it is pending, the terrabase.platform.seed_pending gauge names the reason.");
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
