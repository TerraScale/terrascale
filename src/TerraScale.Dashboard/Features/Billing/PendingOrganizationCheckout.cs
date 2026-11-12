using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Features.Billing;

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
    public string State { get; set; } = "pending";
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
                table.HasCheckConstraint("ck_pending_organization_checkouts_name", "length(name) BETWEEN 1 AND 128");
                table.HasCheckConstraint("ck_pending_organization_checkouts_plan", "length(plan) BETWEEN 1 AND 128");
                table.HasCheckConstraint("ck_pending_organization_checkouts_interval", "interval IN ('monthly', 'annual')");
                table.HasCheckConstraint("ck_pending_organization_checkouts_checkout", "length(checkout_id) > 0");
                table.HasCheckConstraint("ck_pending_organization_checkouts_state", "state IN ('pending', 'creating', 'canceling', 'refund_pending')");
                table.HasCheckConstraint("ck_pending_organization_checkouts_creating", "state <> 'creating' OR paid_at IS NOT NULL AND length(customer_id) > 0 AND length(subscription_id) > 0 AND subscription_status IN ('active', 'trialing') AND length(plan_id) > 0 AND renewal_at IS NOT NULL AND refusal_reason = ''");
                table.HasCheckConstraint("ck_pending_organization_checkouts_refusal", "state NOT IN ('canceling', 'refund_pending') OR length(refusal_reason) > 0");
            });
            builder.Property(row => row.Plan).HasMaxLength(128);
            builder.HasKey(row => row.DraftId);
            builder.HasIndex(row => row.UserId).IsUnique().HasFilter("state <> 'refund_pending'");
            builder.Property(row => row.Version).IsConcurrencyToken();
            builder.HasIndex(row => row.CheckoutId).IsUnique();
        }
    }
}
