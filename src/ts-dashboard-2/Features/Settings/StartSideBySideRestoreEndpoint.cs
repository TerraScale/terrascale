using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

/// <summary>Port of the side-by-side restore action: records a restore job
/// that targets a new logical database and preserves the source.</summary>
public sealed class StartSideBySideRestoreEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/settings/restore");
        Summary("Starts a side-by-side restore from a restore point");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var generation = form["generation"].ToString();
        var plan = store.PlanRestore(generation);
        if (plan is not null)
        {
            store.ExecuteRestore(plan.Token, out _);
        }

        return Results.Redirect($"/settings?tab=backups&database={plan?.DatabaseId}", false, false);
    }
}
