using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Backups;

/// <summary>Refresh backups action from the original panel header.</summary>
public sealed class RefreshRestorePointsEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/backups/refresh");
        Summary("Reloads restore points and restore jobs");
        Tags("Backups");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        store.RefreshRestorePoints();
        return Task.FromResult(Results.Redirect("/backups", false, false));
    }
}
