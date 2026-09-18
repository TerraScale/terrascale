using System.Collections.Immutable;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

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

public sealed class OrganizationsNewPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/organizations/new");
        Summary("Renders the create-organization wizard");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var step = query["step"].FirstOrDefault() is { Length: > 0 } and var s && int.TryParse(s, out var n) && n is >= 1 and <= 3 ? n : 1;
        var payment = query["payment"].FirstOrDefault();
        var paymentStatus = payment is "success" or "canceled" ? payment : null;
        return Task.FromResult<IResult>(OrganizationsNewSlice.Create(
            OrganizationsNewPageModel.From(store, "/organization", step, paymentStatus)));
    }
}
