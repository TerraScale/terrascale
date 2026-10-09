using TerraScale.Endpoints;

namespace TerraBase.Features.Pressure;

/// <summary>Loopback-only cached node pressure with explicit unknown safety evidence.</summary>
public sealed class PressureEndpoint(IPressureSignals pressure) : EndpointWithoutRequest<PressureBody>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/admin/node/pressure");
        Name("nodePressure");
        Summary("Cached node pressure signals");
        Description("Unknown I/O and replication signals remain explicit null; membership state is unknown. Local readiness, storage admissions and outgoing durable ACK lag are independent diagnostics; autoscaling stays gated on unknown membership and safety evidence.");
        Tags("Administration");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<PressureBody> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.FromResult(pressure.Snapshot);
    }
}
