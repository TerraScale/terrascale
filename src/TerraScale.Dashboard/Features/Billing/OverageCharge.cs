using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata;

namespace TerraScale.Dashboard.Features.Billing;

public sealed class OverageCharge
{
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
    public string? ProviderId { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<OverageCharge>
    {
        public void Configure(EntityTypeBuilder<OverageCharge> builder)
        {
            builder.ToTable("billing_overage_charges", table => table.HasCheckConstraint("ck_overage_charge", "usage >= 0 AND allowance >= 0 AND quantity >= 0 AND amount_cents >= 0 AND unit_rate >= 0 AND (provider_id IS NULL) = (accepted_at IS NULL)"));
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => new { row.OrganizationId, row.SubscriptionId, row.Month, row.Meter }).IsUnique();
            builder.HasIndex(row => row.ProviderId).IsUnique().HasFilter("provider_id IS NOT NULL");
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.Property(row => row.Month).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.FrozenAt).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.AcceptedAt).HasConversion(BillingColumns.OptionalUtc);
            foreach (var property in builder.Metadata.GetProperties().Where(property => property.Name is not (nameof(ProviderId) or nameof(AcceptedAt))))
            {
                property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            }
        }
    }
}

public sealed class SkippedBillingMonth
{
    public required string OrganizationId { get; init; }
    public required string SubscriptionId { get; init; }
    public required DateTimeOffset Month { get; init; }
    public required string Reason { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<SkippedBillingMonth>
    {
        public void Configure(EntityTypeBuilder<SkippedBillingMonth> builder)
        {
            builder.ToTable("billing_skipped_months");
            builder.HasKey(row => new { row.OrganizationId, row.SubscriptionId, row.Month });
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
