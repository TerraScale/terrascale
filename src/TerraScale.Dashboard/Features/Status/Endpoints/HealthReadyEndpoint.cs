using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Status.Endpoints;

public sealed class HealthReadyEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/health/ready");
        Summary("Health readiness probe (legacy path)");
        Tags("Status");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Ok(new { status = "ok" }));
}
