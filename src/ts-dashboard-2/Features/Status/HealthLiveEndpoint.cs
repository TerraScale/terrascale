using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Status;

public sealed class HealthLiveEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/health/live");
        Summary("Health liveness probe (legacy path)");
        Tags("Status");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Ok(new { status = "ok" }));
}
