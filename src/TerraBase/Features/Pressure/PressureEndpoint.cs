using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraBase.Features.Callers;

namespace TerraBase.Features.Pressure;

public sealed class PressureEndpoint(IPressureSignals pressure) : EndpointWithoutRequest<PressureBody>
{
    public override void Configure()
    {
        Metadata(new Requires("catalog.read"));
        Get("/v1/admin/node/pressure");
        Name("nodePressure");
        Summary("Cached node pressure signals");
        Description("Reports local readiness, storage admissions and measured process load. Unavailable I/O measurements remain explicit null.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }
    public override ValueTask<PressureBody> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.FromResult(pressure.Snapshot);
    }
}
