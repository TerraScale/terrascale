
namespace TerraScale.Dashboard.Features.Billing;

internal static class OrganizationCheckoutStates
{
    public const string Pending = "pending";
    public const string Creating = "creating";
    public const string Canceling = "canceling";
    public const string RefundPending = "refund_pending";
}

public sealed record PendingOrganizationCheckout : IBillingRow
{
    public string? OriginalVersion { get; set; }

    public string Version { get; set; } = "";
    public required string UserId { get; init; }
    public required string DraftId { get; init; }
    public required string Name { get; init; }
    public required string Plan { get; init; }
    public required string Interval { get; init; }
    public required string CheckoutId { get; init; }
    public required DateTime CreatedAt { get; init; }
    public string State { get; set; } = OrganizationCheckoutStates.Pending;
    public string OrganizationId { get; set; } = "";
    public string RefusalReason { get; set; } = "";
    public string RefundStatus { get; set; } = "";
    public DateTime? RefundNoticeAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public string CustomerId { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string SubscriptionStatus { get; set; } = "";
    public string PlanId { get; set; } = "";
    public DateTime? RenewalAt { get; set; }
}
