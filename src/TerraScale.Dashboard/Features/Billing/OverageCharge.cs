
namespace TerraScale.Dashboard.Features.Billing;

public sealed record OverageCharge
{
    public const string Pending = "pending";
    public const string Charged = "charged";
    public const string Adopted = "adopted";
    public const string Mismatch = "mismatch";
    public const string Skipped = "skipped";
    public const string NothingDue = "nothing_due";

    public required string Id { get; init; }
    public required string OrganizationId { get; init; }
    public required string CustomerId { get; init; }
    public required string SubscriptionId { get; init; }
    public required DateTimeOffset Month { get; init; }
    public required string Meter { get; init; }
    public required DateTimeOffset FrozenAt { get; init; }
    public required long Usage { get; init; }
    public required long Allowance { get; init; }
    public required decimal UnitRate { get; init; }
    public required string Currency { get; init; }
    public required string PricingVersion { get; init; }
    public required string PlanName { get; init; }
    public required long Quantity { get; init; }
    public required long AmountCents { get; init; }
    public required string Description { get; init; }
    public required string State { get; set; }
    public string? SkipReason { get; set; }
    public string? ProviderId { get; set; }
    public long? ProviderAmountCents { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public string? ClaimOwner { get; set; }
    public DateTimeOffset? ClaimUntil { get; set; }
}

public sealed record SkippedBillingMeter
{
    public required string OrganizationId { get; init; }
    public required DateTimeOffset Month { get; init; }
    public required string Meter { get; init; }
    public required string Reason { get; init; }
}

public sealed record StripeEventReceipt : IBillingRow
{
    public string? OriginalVersion { get; set; }

    public required string Id { get; init; }
    public string Version { get; set; } = "";
    public required DateTimeOffset ReceivedAt { get; init; }
    public required string OrganizationId { get; init; }
    public required string SubscriptionId { get; init; }
    public required string InvoiceId { get; init; }
    public required string EventType { get; init; }
    public required DateTimeOffset EventCreated { get; init; }
    public DateTimeOffset ProcessingUntil { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
