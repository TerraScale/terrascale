using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Onboarding;

public sealed record OnboardingStepView(string Key, string Label, string Icon, string State);

public sealed record OnboardingPlanView(string PlanId, string Name, decimal MonthlyUsd, bool IsFree, long RequestLimit);

public sealed record OnboardingPageModel(
    IReadOnlyList<OnboardingStepView> Steps,
    string ActiveStep,
    int CurrentStep,
    int TotalSteps,
    int ProgressPercent,
    bool PaymentCanceled,
    IReadOnlyList<OnboardingPlanView> Plans,
    IReadOnlyList<string> Regions,
    string DisplayName,
    string Email,
    string? OrganizationName,
    bool PaymentValidated) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class OnboardingPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public const string DemoCheckoutSession = "cs_local_01JCHECKOUT7F3K9Q";

    private static readonly (string Key, string Label, string Icon)[] StepDefinitions =
    [
        ("account-information", "Account", "UserRound"),
        ("organization", "Organization", "Building2"),
        ("payment-method", "Payment", "CreditCard"),
        ("payment-validation", "Validation", "CircleCheckBig"),
        ("project", "Project", "Database"),
    ];

    public override void Configure()
    {
        Get("/onboarding");
        Summary("Renders the account activation wizard");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;

        // Returning from checkout: apply the payment outcome before rendering.
        if (query["payment"] == "success")
        {
            store.CompleteOnboardingStep("payment-method");
            store.CompleteOnboardingStep("payment-validation");
        }

        var steps = store.OnboardingSteps;
        var requested = StepDefinitions.Any(s => s.Key == query["step"].ToString()) ? query["step"].ToString() : null;
        var active = requested ?? store.OnboardingNextStep;
        var index = Math.Max(0, steps.ToList().FindIndex(s => s.Key == active));
        var total = steps.Length;
        var views = StepDefinitions.Select(d =>
        {
            var state = steps.FirstOrDefault(s => s.Key == d.Key)?.State ?? "pending";
            return new OnboardingStepView(d.Key, d.Label, d.Icon, state);
        }).ToList();

        var plans = store.Plans
            .Where(p => p.Status == "Active" && p.Name != "Dedicated")
            .Select(p => new OnboardingPlanView(p.Id, p.Name, p.MonthlyUsd, p.MonthlyUsd == 0, p.RequestLimit))
            .ToList();

        var model = new OnboardingPageModel(
            Steps: views,
            ActiveStep: active,
            CurrentStep: index + 1,
            TotalSteps: total,
            ProgressPercent: Math.Min(100, (int)Math.Round((double)(index + 1) / total * 100)),
            PaymentCanceled: query["payment"] == "canceled",
            Plans: plans,
            Regions: [.. store.Regions.Where(r => r.Status == "Live").Select(r => r.Code)],
            DisplayName: store.CurrentUser.Name,
            Email: store.CurrentUser.Email,
            OrganizationName: store.ActiveOrganizationName,
            PaymentValidated: views.First(v => v.Key == "payment-validation").State == "completed");
        return Task.FromResult<IResult>(OnboardingSlice.Create(model));
    }
}
