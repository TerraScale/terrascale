using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Subscriptions;

public sealed record SubscriptionsPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class SubscriptionsPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<SubscriptionsSlice, SubscriptionsPageModel>(store, "/subscriptions")
{
    protected override SubscriptionsPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
