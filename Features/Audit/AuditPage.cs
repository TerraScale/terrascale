using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Audit;

public sealed record AuditPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AuditPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AuditSlice, AuditPageModel>(store, "/audit")
{
    protected override AuditPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
