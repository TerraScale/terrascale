using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin;

public sealed class AdminSetPlanVisibilityEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/admin/plans/visibility");
        Summary("Shows or hides a pricing plan");
        Tags("Admin");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.AdminSetPlanVisibility(form["planId"].ToString(), form["visible"] == "true");
        return Results.Redirect("/admin/pricing?notice=" + Uri.EscapeDataString("Pricing plan updated."), false, false);
    }
}
