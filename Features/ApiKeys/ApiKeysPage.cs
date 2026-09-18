using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.ApiKeys;

public sealed record ApiKeysPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class ApiKeysPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<ApiKeysSlice, ApiKeysPageModel>(store, "/api-keys")
{
    protected override ApiKeysPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
