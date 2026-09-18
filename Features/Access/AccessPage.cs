using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Access;

public sealed record AccessPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AccessPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AccessSlice, AccessPageModel>(store, "/access")
{
    protected override AccessPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
