using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Features.Billing;

internal static class OrganizationCheckoutStates
{
    public const string Pending = "pending";
    public const string Creating = "creating";
    public const string Canceling = "canceling";
    public const string RefundPending = "refund_pending";
}

public sealed class PendingOrganizationCheckout : IBillingRow
{
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

    public sealed class Configuration : IEntityTypeConfiguration<PendingOrganizationCheckout>
    {
        public void Configure(EntityTypeBuilder<PendingOrganizationCheckout> builder)
        {
            builder.ToTable("pending_organization_checkouts", table =>
            {
                table.HasCheckConstraint("ck_pending_organization_checkouts_id", "length(user_id) = 26 AND length(draft_id) = 26");
                table.HasCheckConstraint("ck_pending_organization_checkouts_organization", "organization_id = '' OR length(organization_id) = 26");
                table.HasCheckConstraint("ck_pending_organization_checkouts_name", "length(name) BETWEEN 1 AND 128");
                table.HasCheckConstraint("ck_pending_organization_checkouts_plan", "length(plan) BETWEEN 1 AND 128");
                table.HasCheckConstraint("ck_pending_organization_checkouts_interval", "interval IN ('monthly', 'annual')");
                table.HasCheckConstraint("ck_pending_organization_checkouts_checkout", "length(checkout_id) > 0");
                table.HasCheckConstraint("ck_pending_organization_checkouts_state", $"state IN ('{OrganizationCheckoutStates.Pending}', '{OrganizationCheckoutStates.Creating}', '{OrganizationCheckoutStates.Canceling}', '{OrganizationCheckoutStates.RefundPending}')");
                table.HasCheckConstraint("ck_pending_organization_checkouts_creating", $"state <> '{OrganizationCheckoutStates.Creating}' OR paid_at IS NOT NULL AND length(customer_id) > 0 AND length(subscription_id) > 0 AND subscription_status IN ('active', 'trialing') AND length(plan_id) > 0 AND renewal_at IS NOT NULL AND refusal_reason = ''");
                table.HasCheckConstraint("ck_pending_organization_checkouts_refusal", $"state NOT IN ('{OrganizationCheckoutStates.Canceling}', '{OrganizationCheckoutStates.RefundPending}') OR length(refusal_reason) > 0");
            });
            builder.Property(row => row.Plan).HasMaxLength(128);
            builder.HasKey(row => row.DraftId);
            builder.HasIndex(row => row.UserId).IsUnique().HasFilter($"state <> '{OrganizationCheckoutStates.RefundPending}'");
            builder.Property(row => row.Version).IsConcurrencyToken();
            builder.HasIndex(row => row.CheckoutId).IsUnique();
        }
    }
}
