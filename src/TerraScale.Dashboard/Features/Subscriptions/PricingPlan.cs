namespace TerraScale.Dashboard;

public sealed record PricingPlan(
    string Id,
    string Name,
    decimal MonthlyUsd,
    long StorageLimitMb,
    long RequestLimit,
    string Status,
    int SubscriberCount);
