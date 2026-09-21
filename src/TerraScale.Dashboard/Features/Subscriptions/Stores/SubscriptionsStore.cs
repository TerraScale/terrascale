namespace TerraScale.Dashboard.Shared.State;

public sealed partial class DashboardStore
{
    /// <summary>Usage facts stay unavailable until the engine exports them
    /// (v1 renders the "—"/fallback state for a fresh account).</summary>
    public SubscriptionUsageFacts SubscriptionUsage { get; } = new();
}
