namespace TerraScale.Dashboard.Features.Organization.Pages.Organization;

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
    string? Error,
    IReadOnlyList<OnboardingPlanView> Plans,
    IReadOnlyList<OnboardingRegionView> Regions,
    string DefaultRegion)
{
    // The step keys are shared by the step list and the query resolver, so the
    // resolved active step always has an index in StepDefinitions.
    private const string OrganizationStep = "organization";
    private const string PaymentStep = "payment-method";
    private const string ProjectStep = "project";

    private static readonly (string Key, string Label, string Icon)[] StepDefinitions =
    [
        (OrganizationStep, "Organization", "Building2"),
        (PaymentStep, "Payment", "CreditCard"),
        (ProjectStep, "Project", "Database"),
    ];

    /// <summary>Builds the modal from the page query. It auto-opens when the
    /// journey was advanced (<c>?newOrganization=…</c>) or when the local
    /// checkout returned a payment result.</summary>
    public static NewOrganizationWizardModel From(
        DashboardStore store, string? requestedStep, string? paymentStatus, string? error = null)
    {
        var open = requestedStep is { Length: > 0 } || paymentStatus is not null;
        // Completion comes from real store facts, mirroring the activation
        // wizard's store-backed step state: the pending Business tenant marks
        // step 1 done, active billing marks payment done.
        var organizationDone = store.HasPendingBusinessOrganization || store.HasBillingActiveBusinessOrganization;
        var billingDone = store.HasBillingActiveBusinessOrganization;
        var activeStep = ResolveStep(requestedStep, paymentStatus, organizationDone, billingDone);
        var index = Array.FindIndex(StepDefinitions, d => d.Key == activeStep);
        var regions = OnboardingCatalog.Regions(store);
        return new(
            Open: open,
            Steps: [.. StepDefinitions.Select((d, i) => new OnboardingStepView(
                d.Key, d.Label, d.Icon,
                // Only an open journey can earn completion markers, so a fresh
                // modal always starts on an unstarted step 1.
                open && (d.Key == OrganizationStep && organizationDone || d.Key == PaymentStep && billingDone)
                    ? "completed"
                    : i == index ? "current" : "pending"))],
            ActiveStep: activeStep,
            CurrentStep: index + 1,
            TotalSteps: StepDefinitions.Length,
            ProgressPercent: Math.Min(100, (int)Math.Round((index + 1) / (double)StepDefinitions.Length * 100)),
            // The project step is the wide one, mirroring the activation wizard.
            Compact: activeStep != ProjectStep,
            PaymentCanceled: paymentStatus == "canceled",
            Error: error,
            Plans: OnboardingCatalog.Plans,
            Regions: regions,
            DefaultRegion: OnboardingCatalog.DefaultRegion(regions));
    }

    /// <summary>Canonical step key for <c>?newOrganization=…</c>; a step is only
    /// reachable once its store prerequisites exist, so a stale deep link or a
    /// replayed checkout return cannot render a screen whose setup never
    /// happened. An unfinished checkout resumes on payment.</summary>
    private static string ResolveStep(
        string? requested, string? paymentStatus, bool organizationDone, bool billingDone) => paymentStatus switch
    {
        "success" when billingDone => ProjectStep,
        "success" or "canceled" => organizationDone ? PaymentStep : OrganizationStep,
        _ => requested switch
        {
            "payment" or "payment-method" => organizationDone ? PaymentStep : OrganizationStep,
            "project" => billingDone ? ProjectStep : organizationDone ? PaymentStep : OrganizationStep,
            _ => OrganizationStep,
        },
    };
}
