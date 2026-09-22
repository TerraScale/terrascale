using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization.Endpoints;

/// <summary>
/// Advances the create-organization modal through the three onboarding screens
/// it repeats: organization, payment (local checkout), and project. The
/// organization is a paid Business tenant — the Personal workspace is
/// provisioned once during account creation and is never recreated here — so
/// the payment screen has no bypass and the project only lands once billing is
/// active.
/// </summary>
public sealed class NewOrganizationStepEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    // Starter and Pro both run on the shared Scale billing plan (the v1
    // organizations/create mapping); Business is the Dedicated plan.
    private const string BillingPlanId = "plan_scale";

    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/new-organization");
        Summary("Saves one create-organization step and advances the modal");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        switch (form["step"].ToString())
        {
            case "organization":
                var error = store.CreateBusinessOrganization(form["name"].ToString(), BillingPlanId);
                return error is null
                    ? Results.Redirect("/organization?newOrganization=payment", false, false)
                    : Results.Redirect("/organization?newOrganization=organization&error=" + Uri.EscapeDataString(error), false, false);
            case "payment-method":
                // The local checkout returns to the organization page, whose
                // payment result reopens the modal on the project step.
                return Results.Redirect(store.BeginPaymentSetup("/organization?newOrganization=project"), false, false);
            case "project" when !store.CanProvisionResources:
                return Results.Redirect("/organization?newOrganization=payment&error=" + Uri.EscapeDataString("Complete Business billing before creating resources."), false, false);
            case "project":
                var name = form["name"].ToString().Trim();
                if (name.Length > 0)
                {
                    var region = form["region"].ToString() is { Length: > 0 } selected
                        ? selected
                        : store.SuggestedRegion;
                    store.CreateProject(name, region);
                }

                return Results.Redirect("/organization", false, false);
            default:
                return Results.Redirect("/organization?newOrganization=organization", false, false);
        }
    }
}
