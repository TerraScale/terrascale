using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Status.Endpoints;

public sealed class StatusReadyEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/api/v1/status/ready");
        Summary("Readiness probe");
        Tags("Status");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Ok(new { status = "ok" }));
}
