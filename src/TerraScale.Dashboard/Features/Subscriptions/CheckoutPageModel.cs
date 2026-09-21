using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Subscriptions;

public sealed record CheckoutPageModel(string SessionId) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
