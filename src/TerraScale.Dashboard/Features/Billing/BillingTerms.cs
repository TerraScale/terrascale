using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Features.Billing;

public sealed class BillingSubscription : IBillingRow
{
    public string Version { get; set; } = "";
    public required string OrganizationId { get; init; }
    public required string SubscriptionId { get; init; }
    public required string CustomerId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CheckedAt { get; set; }

    public bool Ended => Status is "canceled" or "incomplete_expired";

    public bool KnownThrough(DateTimeOffset end) => CheckedAt >= end || EndedAt is { } ended && ended <= CheckedAt;

    public sealed class Configuration : IEntityTypeConfiguration<BillingSubscription>
    {
        public void Configure(EntityTypeBuilder<BillingSubscription> builder)
        {
            builder.ToTable("billing_subscriptions", table => table.HasCheckConstraint("ck_billing_subscriptions", "length(subscription_id) > 0 AND length(customer_id) > 0"));
            builder.HasKey(row => new { row.OrganizationId, row.SubscriptionId });
            builder.Property(row => row.Version).IsConcurrencyToken();
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.Property(row => row.StartedAt).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.EndedAt).HasConversion(BillingColumns.OptionalUtc);
            builder.Property(row => row.CheckedAt).HasConversion(BillingColumns.Utc);
        }
    }
}

public sealed class BillingTerms
{
    public const string StartDate = "start_date";
    public const string ProviderEvent = "event";
    public const string FirstCheck = "first_check";
    public const string Unestablished = "unestablished";

    public required string OrganizationId { get; init; }
    public required string SubscriptionId { get; init; }
    public required DateTimeOffset EffectiveFrom { get; init; }
    public required string Basis { get; init; }
    public required DateTimeOffset LearnedAt { get; init; }
    public required string Plan { get; init; }
    public required string PlanName { get; init; }
    public required long OperationsAllowance { get; init; }
    public required long StorageAllowance { get; init; }
    public required decimal OperationRate { get; init; }
    public required decimal StorageRate { get; init; }
    public required string Currency { get; init; }
    public required string PricingVersion { get; init; }

    public bool Priced => PricingVersion.Length > 0;

    public static BillingTerms For(string organizationId, string subscriptionId, DateTimeOffset effectiveFrom, string basis, DateTimeOffset learnedAt, BillingPlan? plan)
    {
        var operationRate = plan is null ? 0 : OperationUnitOverage.UnitAmountCents;
        var storageRate = plan is null ? 0 : StorageOverage.UnitAmountCents;
        var text = plan is null ? "" : string.Create(CultureInfo.InvariantCulture, $"{plan.Key}|{plan.Name}|{plan.Allowances.MonthlyOperations}|{plan.Allowances.StorageBytes}|{operationRate}|{storageRate}|usd");
        return new()
        {
            OrganizationId = organizationId,
            SubscriptionId = subscriptionId,
            EffectiveFrom = effectiveFrom,
            Basis = basis,
            LearnedAt = learnedAt,
            Plan = plan?.Key ?? "",
            PlanName = plan?.Name ?? "",
            OperationsAllowance = plan?.Allowances.MonthlyOperations ?? 0,
            StorageAllowance = plan?.Allowances.StorageBytes ?? 0,
            OperationRate = operationRate,
            StorageRate = storageRate,
            Currency = "usd",
            PricingVersion = plan is null ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
        };
    }

    public sealed class Configuration : IEntityTypeConfiguration<BillingTerms>
    {
        public void Configure(EntityTypeBuilder<BillingTerms> builder)
        {
            builder.ToTable("billing_terms", table => table.HasCheckConstraint("ck_billing_terms", "length(subscription_id) > 0 AND basis IN ('start_date', 'event', 'first_check', 'unestablished')"));
            builder.HasKey(row => new { row.OrganizationId, row.SubscriptionId, row.EffectiveFrom, row.PricingVersion });
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.Property(row => row.EffectiveFrom).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.LearnedAt).HasConversion(BillingColumns.Utc);
            foreach (var property in builder.Metadata.GetProperties())
            {
                property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            }
        }
    }
}
