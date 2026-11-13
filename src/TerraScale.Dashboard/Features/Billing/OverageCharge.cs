using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata;

namespace TerraScale.Dashboard.Features.Billing;

public sealed class OverageCharge
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

    public sealed class Configuration : IEntityTypeConfiguration<OverageCharge>
    {
        public void Configure(EntityTypeBuilder<OverageCharge> builder)
        {
            builder.ToTable("billing_overage_charges", table => table.HasCheckConstraint("ck_overage_charge",
                "usage >= 0 AND allowance >= 0 AND quantity >= 0 AND amount_cents >= 0 AND unit_rate >= 0 AND " +
                "state IN ('pending', 'charged', 'adopted', 'mismatch', 'skipped', 'nothing_due') AND " +
                "(provider_id IS NULL) = (accepted_at IS NULL) AND (provider_id IS NULL) = (provider_amount_cents IS NULL) AND " +
                "(provider_id IS NOT NULL) = (state IN ('charged', 'adopted', 'mismatch')) AND (skip_reason IS NOT NULL) = (state = 'skipped') AND " +
                "(state = 'nothing_due') = (amount_cents = 0) AND (claim_owner IS NULL) = (claim_until IS NULL)"));
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => new { row.OrganizationId, row.Month, row.Meter }).IsUnique();
            builder.HasIndex(row => new { row.State, row.Id });
            builder.HasIndex(row => row.ProviderId).IsUnique().HasFilter("provider_id IS NOT NULL");
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.Property(row => row.Month).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.FrozenAt).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.AcceptedAt).HasConversion(BillingColumns.OptionalUtc);
            builder.Property(row => row.ClaimUntil).HasConversion(BillingColumns.OptionalUtc);
            foreach (var property in builder.Metadata.GetProperties().Where(property => property.Name is not (nameof(State) or nameof(ProviderId) or nameof(ProviderAmountCents) or nameof(AcceptedAt) or nameof(SkipReason) or nameof(ClaimOwner) or nameof(ClaimUntil))))
            {
                property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            }
        }
    }
}

public sealed class SkippedBillingMeter
{
    public required string OrganizationId { get; init; }
    public required DateTimeOffset Month { get; init; }
    public required string Meter { get; init; }
    public required string Reason { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<SkippedBillingMeter>
    {
        public void Configure(EntityTypeBuilder<SkippedBillingMeter> builder)
        {
            builder.ToTable("billing_skipped_meters");
            builder.HasKey(row => new { row.OrganizationId, row.Month, row.Meter });
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.Property(row => row.Month).HasConversion(BillingColumns.Utc);
        }
    }
}

public sealed class StripeEventReceipt : IBillingRow
{
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

    public sealed class Configuration : IEntityTypeConfiguration<StripeEventReceipt>
    {
        public void Configure(EntityTypeBuilder<StripeEventReceipt> builder)
        {
            builder.ToTable("stripe_event_receipts");
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Version).IsConcurrencyToken();
            builder.HasIndex(row => row.ReceivedAt);
            builder.Property(row => row.ReceivedAt).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.EventCreated).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.ProcessingUntil).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.CompletedAt).HasConversion(BillingColumns.OptionalUtc);
        }
    }
}
