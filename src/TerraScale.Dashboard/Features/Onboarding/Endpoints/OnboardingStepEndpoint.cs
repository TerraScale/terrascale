using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Onboarding.Endpoints;

/// <summary>Advances account setup. Registration already creates the Personal
/// workspace, so onboarding only covers optional payment setup and the first project.</summary>
public sealed class OnboardingStepEndpoint(DashboardStore store, StripePaymentOptions stripe) : EndpointWithoutRequest<IResult>
{
    private static readonly string[] StepKeys = ["account-information", "payment-method", "project"];

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
        if (!StepKeys.Contains(step, StringComparer.Ordinal))
            return Results.Redirect("/onboarding", false, false);

        if (step == "payment-method" && form["bypass"] != "1")
        {
            // Without a configured provider the wizard keeps the in-memory local
            // payment setup; the Stripe sandbox takes over as soon as it is set.
            if (!stripe.Enabled)
                return Results.Redirect(store.BeginPaymentSetup("/onboarding"), false, false);

            var planKey = StripePaymentOptions.NormalizePlan(form["plan"].ToString()) is { Length: > 0 } plan ? plan : "starter";
            var interval = form["interval"].ToString() == "annual" ? "annual" : "monthly";
            return Results.Redirect(
                $"/billing/checkout?plan={planKey}&interval={interval}&return={Uri.EscapeDataString("/onboarding")}",
                false,
                false);
        }

        store.CompleteOnboardingStep(step);
        if (step == "project")
        {
            var name = form["name"].ToString().Trim();
            if (name.Length > 0)
            {
                if (store.Projects.Length == 1 && store.Projects[0].Name == "Personal")
                    store.RenameProject(store.Projects[0].Id, name, null, null);
                else if (store.Projects.Length == 0)
                    store.CreateProject(name, form["region"].ToString() is { Length: > 0 } region ? region : "GRU");
            }
            return Results.Redirect("/", false, false);
        }

        return Results.Redirect("/onboarding", false, false);
    }
}
