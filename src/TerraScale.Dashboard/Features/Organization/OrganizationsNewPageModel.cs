using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Organization;

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

    public static OrganizationsNewPageModel From(DashboardStore store, string href, int step, string? paymentStatus)
    {
        var planKeys = new[] { "starter", "pro", "business" };
        var checkoutHref = step == 2 && store.ActiveOrganization.Kind == OrganizationKind.Business
            ? store.BeginPaymentSetup("/organizations/new?step=payment")
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
