using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Onboarding;

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

    // v1 billing catalog (ts-dashboard/src/backend/billing/catalog.ts): the
    // checkout offers Starter/Pro/Business — the free tier is bypass-only —
    // at ten monthly payments per year (two months free) with a 30-day trial.
    private static readonly OnboardingPlanView[] PlanCatalog =
    [
        new("01J00000004WM5Z7P5MDBPN77M", "starter", "Starter", 5m, 50m, 30,
            "Small production workloads with Stripe billing",
            ["5 databases", "1M operations/month", "5 GB-region storage", "5 team members"]),
        new("01J0000000CVGZW8WBHBVYKDZ6", "pro", "Pro", 50m, 500m, 30,
            "More databases and team capacity",
            ["25 databases", "100M operations/month", "25 GB-region storage", "25 team members"]),
        new("01J0000000H8N2R4T6V9WXBQZD", "business", "Business", 99m, 990m, 30,
            "Highest seeded limits for larger organizations",
            ["100 databases", "50M operations/month", "5 TB-region storage", "100 team members"]),
    ];

    // Pin positions on /brand/region-map.svg (136x80 dotted-map projection,
    // tools/generate-region-map.mjs settings) for the seeded region codes.
    private static readonly Dictionary<string, (double X, double Y)> RegionPins = new()
    {
        ["us-east-1"] = (39, 28.58),
        ["us-west-2"] = (21.5, 24.25),
        ["eu-central-1"] = (71.5, 20.78),
        ["eu-west-1"] = (66, 18.19),
        ["sa-east-1"] = (50, 59.76),
    };

    public override void Configure()
    {
        Get("/onboarding");
        Summary("Renders the account activation wizard (registration when anonymous)");
        Tags("Pages");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;

        // Anonymous visitors get the account registration form (activation
        // step 1), mirroring ts-dashboard's anonymous onboarding branch.
        if (!store.IsAuthenticated(HttpContext))
        {
            return Task.FromResult<IResult>(OnboardingSlice.Create(RegistrationModel()
                with { RegistrationError = query["error"].FirstOrDefault() }));
        }

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

        var model = new OnboardingPageModel(
            Steps: views,
            ActiveStep: active,
            CurrentStep: index + 1,
            TotalSteps: total,
            ProgressPercent: Math.Min(100, (int)Math.Round((double)(index + 1) / total * 100)),
            PaymentCanceled: query["payment"] == "canceled",
            Plans: PlanCatalog,
            Regions: [.. store.Regions
                .Where(r => RegionPins.ContainsKey(r.Code))
                .Select(r => new OnboardingRegionView(r.Code, r.Name, r.Status == "Live", RegionPins[r.Code].X, RegionPins[r.Code].Y))],
            DisplayName: store.CurrentUser.Name,
            Email: store.CurrentUser.Email,
            OrganizationName: store.ActiveOrganizationName,
            PaymentValidated: views.First(v => v.Key == "payment-validation").State == "completed",
            Registration: false,
            RegistrationError: null);
        return Task.FromResult<IResult>(OnboardingSlice.Create(model));
    }

    private OnboardingPageModel RegistrationModel() => new(
        Steps: [.. StepDefinitions.Select((d, i) => new OnboardingStepView(d.Key, d.Label, d.Icon, i == 0 ? "current" : "pending"))],
        ActiveStep: "account-information",
        CurrentStep: 1,
        TotalSteps: StepDefinitions.Length,
        ProgressPercent: 20,
        PaymentCanceled: false,
        Plans: [],
        Regions: [],
        DisplayName: "",
        Email: "",
        OrganizationName: null,
        PaymentValidated: false,
        Registration: true,
        RegistrationError: null);
}
