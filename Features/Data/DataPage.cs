using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

public sealed record DataPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class DataPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<DataSlice, DataPageModel>(store, "/data")
{
    protected override DataPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
