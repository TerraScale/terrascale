using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminEstatePageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AdminEstatePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminEstateSlice, AdminEstatePageModel>(store, "/admin/estate")
{
    protected override AdminEstatePageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
