namespace TerraScale.Dashboard.Features.Subscriptions.Models;

public sealed record PricingPlan(
    string Id,
    string Name,
    decimal MonthlyUsd,
    long StorageLimitMb,
    long RequestLimit,
    string Status,
    int SubscriberCount);
