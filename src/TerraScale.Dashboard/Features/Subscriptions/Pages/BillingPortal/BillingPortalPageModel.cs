using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Subscriptions.Pages.BillingPortal;

public sealed record BillingPortalPageModel(string SessionId) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
