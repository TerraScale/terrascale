namespace TerraScale.Dashboard.Features.Subscriptions.Models;

public sealed record SubscriptionUsageFacts(
    int? ActiveViews = null,
    int? Subscribers = null,
    double? CdnHitRatio = null,
    long? OriginFills = null,
    int? WebsocketConnections = null);
