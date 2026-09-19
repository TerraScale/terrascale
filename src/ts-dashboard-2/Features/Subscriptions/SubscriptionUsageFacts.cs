namespace TerraScale.Dashboard2;

public sealed record SubscriptionUsageFacts(
    int? ActiveViews = null,
    int? Subscribers = null,
    double? CdnHitRatio = null,
    long? OriginFills = null,
    int? WebsocketConnections = null);
