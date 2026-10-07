using TerraScale.Endpoints;

namespace TerraBase.Features.Pressure;

/// <summary>Loopback-only instance-bound node evidence consumed through the SDK.</summary>
public sealed class AutoscalerPressureEndpoint(IPressureSignals pressure) : EndpointWithoutRequest<AutoscalerPressureBody>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/admin/node/pressure");
        Name("autoscalerPressure");
        Summary("Instance-bound node pressure evidence");
        Description("Cached schema-2 pressure. Local readiness and outgoing durable ACK lag are independent diagnostics, not certified membership or incoming catch-up evidence. Unknown lifecycle and safety counters remain explicit; consumers must hold unsafe scaling.");
        Tags("Administration");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<AutoscalerPressureBody> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.FromResult(new AutoscalerPressureBody(pressure.Snapshot));
    }
}
