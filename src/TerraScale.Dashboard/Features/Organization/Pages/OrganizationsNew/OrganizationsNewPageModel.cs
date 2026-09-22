using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Organization.Pages.OrganizationsNew;

public sealed record OrganizationsNewPageModel(
    int Step,
    int ProgressPercent,
    ImmutableArray<DashboardStore.AdminPlanRow> Plans,
    ImmutableArray<DashboardStore.OrganizationRegion> RegionCatalog,
    string SuggestedRegion,
    string? PaymentStatus,
    string? CheckoutHref,
    string OrganizationName) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static OrganizationsNewPageModel From(DashboardStore store, string href, int step, string? paymentStatus, bool stripeCheckoutEnabled)
    {
        var planKeys = new[] { "starter", "pro", "business" };
        const string returnPath = "/organizations/new?step=payment";
        var organization = store.ActiveOrganization;
        var checkoutHref = step == 2 && organization.Kind == OrganizationKind.Business
            ? stripeCheckoutEnabled
                ? $"/billing/checkout?plan={BillingCheckoutEndpoint.ResolvePlanKey(null, organization)}&interval=monthly&return={Uri.EscapeDataString(returnPath)}"
                : store.BeginPaymentSetup(returnPath)
            : null;
        return new(
            Step: step,
            ProgressPercent: (int)Math.Round(step / 3.0 * 100),
            Plans: [.. planKeys.Select(key => store.AdminPlans.FirstOrDefault(p => p.Key == key && p.Visible)).Where(p => p is not null).Select(p => p!)],
            RegionCatalog: store.OrganizationRegions,
            SuggestedRegion: store.SuggestedRegion,
            PaymentStatus: paymentStatus,
            CheckoutHref: checkoutHref,
            OrganizationName: store.ActiveOrganizationName)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
