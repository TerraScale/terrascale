using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminOrganizationsPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AdminOrganizationsPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminOrganizationsSlice, AdminOrganizationsPageModel>(store, "/admin/organizations")
{
    protected override AdminOrganizationsPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
