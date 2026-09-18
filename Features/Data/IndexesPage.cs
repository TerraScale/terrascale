using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

public sealed record IndexesPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class IndexesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<IndexesSlice, IndexesPageModel>(store, "/indexes")
{
    protected override IndexesPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
