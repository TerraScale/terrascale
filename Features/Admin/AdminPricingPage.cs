using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminPricingPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AdminPricingPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminPricingSlice, AdminPricingPageModel>(store, "/admin/pricing")
{
    protected override AdminPricingPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
