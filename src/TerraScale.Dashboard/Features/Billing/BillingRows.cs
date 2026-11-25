using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Features.Billing.Internal;

namespace TerraScale.Dashboard.Features.Billing;

public interface IBillingRow
{
    string Version { get; set; }

    string? OriginalVersion { get; set; }
}

public sealed record BillingBinding : IBillingRow
{
    public string? OriginalVersion { get; set; }

    public string Version { get; set; } = "";
    public string? ErasureId { get; set; }
    public string Kind { get; set; } = "stripe";
    public string State { get; set; } = "reserved";
    public string? InternalReservation { get; set; }

    public InternalBillingReservation? Reservation
    {
        get => InternalReservation is null ? null : JsonSerializer.Deserialize(InternalReservation, BillingRowJson.Default.InternalBillingReservation);
        set => InternalReservation = value is null ? null : JsonSerializer.Serialize(value, BillingRowJson.Default.InternalBillingReservation);
    }
    public string OrganizationId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string Plan { get; set; } = "";
    public string Interval { get; set; } = "";
    public string ReturnPath { get; set; } = "";
    public string RequestId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public string CheckoutId { get; set; } = "";
    public string CheckoutProjectId { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string EffectivePlan { get; set; } = "";
    public string EffectiveInterval { get; set; } = "";
    public DateTimeOffset? RenewalAt { get; set; }
    public string Status { get; set; } = "pending";
    public bool TrialUsed { get; set; }
    public DateTimeOffset OverageStartsAt { get; set; }
    public DateTimeOffset? ExpiryUnavailableSince { get; set; }
    public DateTimeOffset? ExpiryLastLoggedAt { get; set; }
    public string TaxLocationStatus { get; set; } = "";
    public long Revision { get; set; }

    public BillingBinding Copy()
    {
        return (BillingBinding)MemberwiseClone();
    }
}

public sealed record StripeCustomerRow
{
    public required string Id { get; init; }

    public required string CustomerId { get; init; }

    public required string OrganizationId { get; init; }

    public required DateTime CreatedAt { get; init; }
}

public sealed record StripeCheckoutRow
{
    public required string Id { get; init; }

    public required string CheckoutId { get; init; }

    public required string OrganizationId { get; init; }

    public required string CustomerId { get; init; }

    public required string RequestId { get; init; }

    public required string ProjectId { get; init; }

    public required string ReturnPath { get; init; }

    public required string Plan { get; init; }

    public required string Interval { get; init; }

    public required DateTime CreatedAt { get; init; }

    public CheckoutBinding Binding => new(CheckoutId, OrganizationId, CustomerId, RequestId, ProjectId, ReturnPath, Plan, Interval);
}

public sealed record BillingChangeReceipt : IBillingRow
{
    public string? OriginalVersion { get; set; }

    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Plan { get; set; } = "";
    public string Interval { get; set; } = "";
    public string SourcePlanKey { get; set; } = "";
    public string SourceInterval { get; set; } = "";
    public string SourcePriceId { get; set; } = "";
    public string TargetPriceId { get; set; } = "";
    public DateTimeOffset PeriodEnd { get; set; }
    public long Revision { get; set; }
    public string Status { get; set; } = "reserved";
    public DateTimeOffset? StatusChangedAt { get; set; }
}

public sealed record InternalBillingRow
{
    public const string OrganizationScope = "organization";

    public required string Id { get; init; }

    public required string Scope { get; init; }

    public required string OrganizationId { get; init; }

    public required string PlanId { get; init; }
}

[JsonSerializable(typeof(InternalBillingReservation))]
internal sealed partial class BillingRowJson : JsonSerializerContext;
