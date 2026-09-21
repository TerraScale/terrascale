using TerraScale.Dashboard;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

/// <summary>
/// Advances the create-organization modal through the three onboarding screens
/// it repeats: organization, payment (bypass or local checkout), and project.
/// Each redirect reopens the modal on the organization page.
/// </summary>
public sealed class NewOrganizationStepEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
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
                store.RenameOrganization(form["name"].ToString());
                return Results.Redirect("/organization?newOrganization=payment", false, false);
            case "payment-method" when form["bypass"] == "1":
                return Results.Redirect("/organization?newOrganization=project", false, false);
            case "payment-method":
                // The local checkout returns to the organization page, whose
                // payment result reopens the modal on the project step.
                var returnTo = Uri.EscapeDataString("/organization");
                return Results.Redirect(
                    $"/checkout/{OnboardingPageEndpoint.DemoCheckoutSession}?return={returnTo}",
                    false, false);
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
