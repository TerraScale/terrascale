using TerraScale.Dashboard;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

/// <summary>
/// Advances the new-organization wizard through the three onboarding screens it
/// repeats: organization, payment (bypass or local checkout), and project.
/// </summary>
public sealed class OrganizationsNewStepEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/organizations/new/step");
        Summary("Saves one new-organization step and advances the wizard");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        switch (form["step"].ToString())
        {
            case "organization":
                store.RenameOrganization(form["name"].ToString());
                return Results.Redirect("/organizations/new?step=payment", false, false);
            case "payment-method" when form["bypass"] == "1":
                return Results.Redirect("/organizations/new?step=project", false, false);
            case "payment-method":
                // The local checkout returns to the wizard so it resumes on the
                // project step after the payment result is applied.
                var returnTo = Uri.EscapeDataString("/organizations/new");
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
                return Results.Redirect("/organizations/new", false, false);
        }
    }
}
