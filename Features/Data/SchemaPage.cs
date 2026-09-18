using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

public sealed record SchemaPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class SchemaPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<SchemaSlice, SchemaPageModel>(store, "/schema")
{
    protected override SchemaPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
