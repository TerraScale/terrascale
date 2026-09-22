using System.Collections.Immutable;

namespace TerraScale.Dashboard.Features.Onboarding.Presentation;

/// <summary>Shared onboarding catalog: the checkout-selectable plans and the
/// region pins every onboarding step screen renders. Both the activation
/// wizard and the new-organization flow read from here so the screens are
/// identical.</summary>
public static class OnboardingCatalog
{
    // v1 billing catalog (ts-dashboard/src/backend/billing/catalog.ts): the
    // checkout offers Starter/Pro/Business — the free tier is bypass-only —
    // at ten monthly payments per year (two months free) with a 30-day trial.
    // The Key is the posted plan; the endpoint maps it to the store's billable
    // plan ids.
    public static readonly ImmutableArray<OnboardingPlanView> Plans =
    [
        new("starter", "Starter", 5m, 50m, 30,
            "Small production workloads with Stripe billing",
            ["5 databases", "1M operations/month", "5 GB-region storage", "5 team members"]),
        new("pro", "Pro", 50m, 500m, 30,
            "More databases and team capacity",
            ["25 databases", "100M operations/month", "25 GB-region storage", "25 team members"]),
        new("business", "Business", 99m, 990m, 30,
            "Highest seeded limits for larger organizations",
            ["100 databases", "500M operations/month", "5 TB-region storage", "100 team members"]),
    ];

    /// <summary>Selectable region pins: the seeded regions the region map has
    /// coordinates for. Every seeded hub is live in the demo world.</summary>
    public static IReadOnlyList<OnboardingRegionView> Regions(DashboardStore store) =>
    [
        .. store.OrganizationRegions
            .Select(r => (Region: r, Pin: RegionMap.Coordinates(r.Code)))
            .Where(t => t.Pin is not null)
            .Select(t => new OnboardingRegionView(
                t.Region.Code, t.Region.Name, t.Region.Country, t.Region.LatencyMs, true, t.Pin!.Value.X, t.Pin!.Value.Y)),
    ];

    /// <summary>Suggested primary region: the first live region, matching the
    /// v1 onboarding project step.</summary>
    public static string DefaultRegion(IReadOnlyList<OnboardingRegionView> regions) =>
        regions.FirstOrDefault(r => r.Live)?.Code ?? "";
}
