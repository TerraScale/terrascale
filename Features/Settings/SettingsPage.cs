using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

public sealed record SettingsPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class SettingsPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<SettingsSlice, SettingsPageModel>(store, "/settings")
{
    protected override SettingsPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
