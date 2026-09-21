using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Subscriptions.Pages.Checkout;

public sealed record CheckoutPageModel(string SessionId) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
