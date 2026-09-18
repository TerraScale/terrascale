using System.Collections.Immutable;
using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Organization;

public sealed record OrganizationsNewPageModel(
    int Step,
    int ProgressPercent,
    ImmutableArray<DashboardStore.AdminPlanRow> Plans,
    ImmutableArray<DashboardStore.OrganizationRegion> RegionCatalog,
    string SuggestedRegion,
    string? PaymentStatus) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static OrganizationsNewPageModel From(DashboardStore store, string href, int step, string? paymentStatus)
    {
        // The wizard offers exactly starter/pro/business (NewOrganizationWizard selectablePlans).
        var planKeys = new[] { "starter", "pro", "business" };
        return new(
            Step: step,
            ProgressPercent: (int)Math.Round(step / 3.0 * 100),
            Plans: [.. planKeys.Select(key => store.AdminPlans.FirstOrDefault(p => p.Key == key && p.Visible)).Where(p => p is not null).Select(p => p!)],
            RegionCatalog: store.OrganizationRegions,
            SuggestedRegion: store.SuggestedRegion,
            PaymentStatus: paymentStatus)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
