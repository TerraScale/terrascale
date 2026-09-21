using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Onboarding;

/// <summary>Advances one wizard step: account information, organization,
/// payment bypass, or project creation. Payment continue redirects to the
/// local checkout session instead.</summary>
public sealed class OnboardingStepEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    private static readonly string[] StepKeys =
        ["account-information", "organization", "payment-method", "payment-validation", "project"];

    private static readonly Dictionary<string, string> NextStep = new()
    {
        ["account-information"] = "organization",
        ["organization"] = "payment-method",
        ["payment-method"] = "payment-validation",
        ["payment-validation"] = "project",
        ["project"] = null!,
    };

    public override void Configure()
    {
        Post("/onboarding/step");
        Summary("Saves one onboarding step and advances the wizard");
        Tags("Onboarding");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var step = form["step"].ToString();
        if (!StepKeys.Contains(step))
        {
            return Results.Redirect("/onboarding", false, false);
        }

        if (step == "payment-method" && form["bypass"] != "1")
        {
            return Results.Redirect("/checkout/" + OnboardingPageEndpoint.DemoCheckoutSession, false, false);
        }

        store.CompleteOnboardingStep(step);
        if (step == "organization" && form["name"].ToString() is { Length: > 0 } organizationName)
        {
            store.RenameOrganization(organizationName);
        }

        if (step == "project")
        {
            var name = form["name"].ToString();
            if (name.Length > 0)
            {
                // The fresh world pre-seeds one "Personal" project; the wizard
                // names it instead of adding a second one.
                if (store.Projects.Length == 1 && store.Projects[0].Name == "Personal")
                {
                    store.RenameProject(store.Projects[0].Id, name, null, null);
                }
                else
                {
                    store.CreateProject(name, form["region"].ToString() is { Length: > 0 } region ? region : "GRU");
                }
            }

            return Results.Redirect("/", false, false);
        }

        var next = NextStep[step];
        return Results.Redirect(next is null ? "/" : $"/onboarding?step={next}", false, false);
    }
}
