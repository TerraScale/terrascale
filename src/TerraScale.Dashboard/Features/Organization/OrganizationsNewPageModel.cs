using TerraScale.Dashboard;
using TerraScale.Dashboard.Features.Onboarding;

namespace TerraScale.Dashboard.Features.Organization;

/// <summary>
/// The new-organization journey repeats only the onboarding steps a new tenant
/// needs: organization, payment, and project. It reuses the activation
/// wizard's step screens (and billing catalog) so both flows render the exact
/// same cards.
/// </summary>
public sealed record OrganizationsNewPageModel(
    IReadOnlyList<OnboardingStepView> Steps,
    string ActiveStep,
    int CurrentStep,
    int TotalSteps,
    int ProgressPercent,
    bool Compact,
    bool PaymentCanceled,
    IReadOnlyList<OnboardingPlanView> Plans,
    IReadOnlyList<OnboardingRegionView> Regions,
    string DefaultRegion) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    private static readonly (string Key, string Label, string Icon)[] StepDefinitions =
    [
        ("organization", "Organization", "Building2"),
        ("payment-method", "Payment", "CreditCard"),
        ("project", "Project", "Database"),
    ];

    /// <summary>Canonical step key for a <c>?step=</c> value; unknown values
    /// fall back to the first step. A completed checkout resumes on the project
    /// step, a canceled one on payment.</summary>
    public static string ResolveStep(string? requested, string? paymentStatus) => paymentStatus switch
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

    public static OrganizationsNewPageModel From(DashboardStore store, string href, string activeStep, string? paymentStatus)
    {
        var index = Math.Max(0, Array.FindIndex(StepDefinitions, d => d.Key == activeStep));
        var steps = StepDefinitions
            .Select((d, i) => new OnboardingStepView(
                d.Key, d.Label, d.Icon, i < index ? "completed" : i == index ? "current" : "pending"))
            .ToList();
        var regions = OnboardingCatalog.Regions(store);
        return new(
            Steps: steps,
            ActiveStep: activeStep,
            CurrentStep: index + 1,
            TotalSteps: StepDefinitions.Length,
            ProgressPercent: Math.Min(100, (int)Math.Round((index + 1) / (double)StepDefinitions.Length * 100)),
            // The project step is the wide one, mirroring the activation wizard.
            Compact: activeStep != "project",
            PaymentCanceled: paymentStatus == "canceled",
            Plans: OnboardingCatalog.Plans,
            Regions: regions,
            DefaultRegion: OnboardingCatalog.DefaultRegion(regions))
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
