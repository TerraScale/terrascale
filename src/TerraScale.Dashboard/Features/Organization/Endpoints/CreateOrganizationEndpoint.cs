using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization.Endpoints;

/// <summary>Creates a paid Business organization. Personal is provisioned once
/// during account registration and is never recreated by this endpoint.</summary>
public sealed class CreateOrganizationEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organizations/create");
        Summary("Creates a paid Business organization");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var planKey = form["planId"].ToString();
        var plan = store.AdminPlans.FirstOrDefault(p => p.PlanId == planKey || p.Key == planKey);
        if (plan is null)
            return Results.Redirect("/organizations/new?error=" + Uri.EscapeDataString("Choose an available paid plan."), false, false);
        var billingPlanId = plan.Key switch
        {
            "starter" or "pro" => "plan_scale",
            "business" => "plan_dedicated",
            _ => "",
        };
        var error = store.CreateBusinessOrganization(form["name"].ToString(), billingPlanId);
        if (error is not null)
            return Results.Redirect("/organizations/new?error=" + Uri.EscapeDataString(error), false, false);
        return Results.Redirect("/organizations/new?step=payment", false, false);
    }
}
