using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Profile;

public sealed record ProfilePageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class ProfilePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<ProfileSlice, ProfilePageModel>(store, "/profile")
{
    protected override ProfilePageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
