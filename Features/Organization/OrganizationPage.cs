using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

public sealed record OrganizationPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class OrganizationPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<OrganizationSlice, OrganizationPageModel>(store, "/organization")
{
    protected override OrganizationPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
