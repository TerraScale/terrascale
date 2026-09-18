using System.Collections.Immutable;
namespace TerraScale.Dashboard2;

public sealed record SubscriptionUsageFacts(
    int? ActiveViews,
    int? Subscribers,
    double? CdnHitRatio,
    long? OriginFills,
    int? WebsocketConnections);

public sealed partial class DashboardStore
{
    public SubscriptionUsageFacts SubscriptionUsage { get; } = new(
        ActiveViews: 14,
        Subscribers: 21,
        CdnHitRatio: 0.923,
        OriginFills: 3_812,
        WebsocketConnections: 2);
}
