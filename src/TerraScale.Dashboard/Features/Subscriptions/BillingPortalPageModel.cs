using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Subscriptions;

public sealed record BillingPortalPageModel(string SessionId) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
