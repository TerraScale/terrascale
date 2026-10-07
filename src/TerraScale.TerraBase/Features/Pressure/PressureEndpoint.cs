using TerraScale.Endpoints;

namespace TerraBase.Features.Pressure;

/// <summary>Loopback-only cached node telemetry, deliberately unavailable as autoscaler evidence.</summary>
public sealed class PressureEndpoint(IPressureSignals pressure) : EndpointWithoutRequest<PressureBody>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/node/pressure");
        Name("nodePressure");
        Summary("Cached node pressure signals");
        Description("Unknown I/O and replication signals remain explicit null; membership state is unknown. Autoscaling remains gated until #13 and #11 land and this producer and its consumer agree on availability.");
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
