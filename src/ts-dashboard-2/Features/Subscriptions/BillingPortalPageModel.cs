using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Subscriptions;

public sealed record BillingPortalPageModel(string SessionId) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
