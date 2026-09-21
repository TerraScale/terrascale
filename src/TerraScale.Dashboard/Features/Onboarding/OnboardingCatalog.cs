namespace TerraScale.Dashboard.Features.Onboarding;

/// <summary>Shared onboarding catalog: the checkout-selectable plans and the
/// region pins every onboarding step screen renders. Both the activation
/// wizard and the new-organization flow read from here so the screens are
/// identical.</summary>
public static class OnboardingCatalog
{
    // v1 billing catalog (ts-dashboard/src/backend/billing/catalog.ts): the
    // checkout offers Starter/Pro/Business — the free tier is bypass-only —
    // at ten monthly payments per year (two months free) with a 30-day trial.
    public static readonly IReadOnlyList<OnboardingPlanView> Plans =
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

    /// <summary>Selectable region pins: the seeded regions the region map has
    /// coordinates for, marked live or unavailable from the region status.</summary>
    public static IReadOnlyList<OnboardingRegionView> Regions(DashboardStore store) =>
    [
        .. store.Regions
            .Where(r => RegionPins.ContainsKey(r.Code))
            .Select(r => new OnboardingRegionView(r.Code, r.Name, r.Status == "Live", RegionPins[r.Code].X, RegionPins[r.Code].Y)),
    ];

    /// <summary>Suggested primary region: the first live region, matching the
    /// v1 onboarding project step.</summary>
    public static string DefaultRegion(IReadOnlyList<OnboardingRegionView> regions) =>
        regions.FirstOrDefault(r => r.Live)?.Code ?? "";
}
