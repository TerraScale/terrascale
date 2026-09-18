using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Usage;

public sealed record UsagePageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class UsagePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<UsageSlice, UsagePageModel>(store, "/usage")
{
    protected override UsagePageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
