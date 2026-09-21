using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin.Endpoints;

public sealed class AdminDeletePlanEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/admin/plans/delete");
        Summary("Deletes a pricing plan");
        Tags("Admin");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.AdminDeletePlan(form["planId"].ToString());
        return Results.Redirect("/admin/pricing?notice=" + Uri.EscapeDataString("Pricing plan deleted."), false, false);
    }
}
