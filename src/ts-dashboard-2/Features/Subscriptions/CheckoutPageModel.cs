using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Subscriptions;

public sealed record CheckoutPageModel(string SessionId) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
