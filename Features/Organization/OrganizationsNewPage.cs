using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

public sealed record OrganizationsNewPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class OrganizationsNewPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<OrganizationsNewSlice, OrganizationsNewPageModel>(store, "/organizations/new")
{
    protected override OrganizationsNewPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
