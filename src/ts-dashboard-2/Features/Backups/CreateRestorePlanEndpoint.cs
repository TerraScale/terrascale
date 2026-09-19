using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Backups;

/// <summary>Plan step of the restore flow: creates an expiring confirmation
/// plan without starting anything, then re-renders the page for review.</summary>
public sealed class CreateRestorePlanEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/backups/restore-plan");
        Summary("Creates an expiring restore confirmation plan");
        Tags("Backups");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var generation = form["generation"].ToString();
        var plan = store.PlanRestore(generation);
        return Results.Redirect(plan is null
            ? "/backups?error=" + Uri.EscapeDataString("That restore point is no longer available. Refresh backups and try again.")
            : "/backups?plan=" + Uri.EscapeDataString(plan.Token), false, false);
    }
}
