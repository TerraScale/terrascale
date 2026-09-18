namespace TerraScale.Dashboard2;

public sealed record SubscriptionUsageFacts(
    int? ActiveViews,
    int? Subscribers,
    double? CdnHitRatio,
    long? OriginFills,
    int? WebsocketConnections);
