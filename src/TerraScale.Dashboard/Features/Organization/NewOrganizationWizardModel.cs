using TerraScale.Dashboard;
using TerraScale.Dashboard.Features.Onboarding;

namespace TerraScale.Dashboard.Features.Organization;

/// <summary>
/// The create-organization modal on the organization page. It repeats only the
/// three onboarding steps a new tenant needs — organization, payment, and
/// project — and reuses the activation wizard's step screens verbatim.
/// </summary>
public sealed record NewOrganizationWizardModel(
    bool Open,
    IReadOnlyList<OnboardingStepView> Steps,
    string ActiveStep,
    int CurrentStep,
    int TotalSteps,
    int ProgressPercent,
    bool Compact,
    bool PaymentCanceled,
    IReadOnlyList<OnboardingPlanView> Plans,
    IReadOnlyList<OnboardingRegionView> Regions,
    string DefaultRegion)
{
    private static readonly (string Key, string Label, string Icon)[] StepDefinitions =
    [
        ("organization", "Organization", "Building2"),
        ("payment-method", "Payment", "CreditCard"),
        ("project", "Project", "Database"),
    ];

    /// <summary>Builds the modal from the page query. It auto-opens when the
    /// journey was advanced (<c>?newOrganization=…</c>) or when the local
    /// checkout returned a payment result.</summary>
    public static NewOrganizationWizardModel From(DashboardStore store, string? requestedStep, string? paymentStatus)
    {
        var activeStep = ResolveStep(requestedStep, paymentStatus);
        var index = Math.Max(0, Array.FindIndex(StepDefinitions, d => d.Key == activeStep));
        var regions = OnboardingCatalog.Regions(store);
        return new(
            Open: requestedStep is { Length: > 0 } || paymentStatus is not null,
            Steps: [.. StepDefinitions.Select((d, i) => new OnboardingStepView(
                d.Key, d.Label, d.Icon, i < index ? "completed" : i == index ? "current" : "pending"))],
            ActiveStep: activeStep,
            CurrentStep: index + 1,
            TotalSteps: StepDefinitions.Length,
            ProgressPercent: Math.Min(100, (int)Math.Round((index + 1) / (double)StepDefinitions.Length * 100)),
            // The project step is the wide one, mirroring the activation wizard.
            Compact: activeStep != "project",
            PaymentCanceled: paymentStatus == "canceled",
            Plans: OnboardingCatalog.Plans,
            Regions: regions,
            DefaultRegion: OnboardingCatalog.DefaultRegion(regions));
    }

    /// <summary>Canonical step key for <c>?newOrganization=</c>; unknown values
    /// fall back to the first step. A completed checkout resumes on the project
    /// step, a canceled one on payment.</summary>
    private static string ResolveStep(string? requested, string? paymentStatus) => paymentStatus switch
    {
        "success" => "project",
        "canceled" => "payment-method",
        _ => requested switch
        {
            "payment" or "payment-method" => "payment-method",
            "project" or "database" => "project",
            _ => "organization",
        },
    };
}
