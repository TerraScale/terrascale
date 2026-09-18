using System.Collections.Immutable;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

public sealed record OrganizationsNewPageModel(
    int Step,
    string StepLabel,
    int ProgressPercent,
    ImmutableArray<PricingPlan> Plans,
    ImmutableArray<RegionInfo> RegionCatalog,
    string SuggestedRegion,
    string? PaymentStatus) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static OrganizationsNewPageModel From(DashboardStore store, string href, int step, string? paymentStatus)
    {
        var labels = new[] { "Organization", "Payment", "Database" };
        return new(
            Step: step,
            StepLabel: labels[step - 1],
            ProgressPercent: (int)Math.Round(step / 3.0 * 100),
            Plans: store.Plans.Where(p => p.Status == "Active").ToImmutableArray(),
            RegionCatalog: store.Regions.Where(r => r.Status != "Offline").ToImmutableArray(),
            SuggestedRegion: store.ActiveProject.Region,
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
