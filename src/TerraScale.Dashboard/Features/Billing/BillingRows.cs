using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TerraScale.Dashboard.Features.Billing.Internal;

namespace TerraScale.Dashboard.Features.Billing;

public interface IBillingRow
{
    string Version { get; set; }
}

public sealed class BillingBinding : IBillingRow
{
    public string Version { get; set; } = "";
    public string? ErasureId { get; set; }
    public string Kind { get; set; } = "stripe";
    public string State { get; set; } = "reserved";
    public InternalBillingReservation? InternalReservation { get; set; }
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

    public sealed class Configuration : IEntityTypeConfiguration<BillingBinding>
    {
        public void Configure(EntityTypeBuilder<BillingBinding> builder)
        {
            builder.ToTable("billing", table =>
            {
                table.HasCheckConstraint("ck_billing_id", "length(organization_id) = 26 AND length(version) = 26");
                table.HasCheckConstraint("ck_billing_kind", "kind IN ('stripe', 'internal') AND state IN ('reserved', 'active', 'deleting')");
                table.HasCheckConstraint("ck_billing_erasure", "erasure_id IS NULL AND length(request_id) > 0 OR length(erasure_id) = 26 AND length(request_id) = 0");
                table.HasCheckConstraint("ck_billing_internal", "(kind = 'internal') = (internal_reservation IS NOT NULL) AND (kind = 'stripe' OR customer_id = '' AND checkout_id = '' AND subscription_id = '')");
                table.HasCheckConstraint("ck_billing_customer", "customer_id = '' OR customer_id LIKE 'cus\\_%' ESCAPE '\\'");
                table.HasCheckConstraint("ck_billing_revision", "revision >= 0");
            });
            builder.HasKey(row => row.OrganizationId);
            builder.HasIndex(row => row.CustomerId).IsUnique().HasFilter("customer_id <> ''");
            builder.HasOne<OrganizationRow>().WithOne().HasForeignKey<BillingBinding>(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.Property(row => row.Version).IsConcurrencyToken();
            builder.Property(row => row.StartedAt).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.RenewalAt).HasConversion(BillingColumns.OptionalUtc);
            builder.Property(row => row.OverageStartsAt).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.ExpiryUnavailableSince).HasConversion(BillingColumns.OptionalUtc);
            builder.Property(row => row.ExpiryLastLoggedAt).HasConversion(BillingColumns.OptionalUtc);
            builder.Property(row => row.InternalReservation).HasConversion(
                reservation => reservation == null ? null : JsonSerializer.Serialize(reservation, BillingRowJson.Default.InternalBillingReservation),
                json => json == null ? null : JsonSerializer.Deserialize(json, BillingRowJson.Default.InternalBillingReservation));
        }
    }
}

public sealed class StripeCustomerRow
{
    public required string Id { get; init; }

    public required string CustomerId { get; init; }

    public required string OrganizationId { get; init; }

    public required DateTime CreatedAt { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<StripeCustomerRow>
    {
        public void Configure(EntityTypeBuilder<StripeCustomerRow> builder)
        {
            builder.ToTable("stripe_customers", table =>
            {
                table.HasCheckConstraint("ck_stripe_customers_id", "length(id) = 26 AND length(organization_id) = 26");
                table.HasCheckConstraint("ck_stripe_customers_customer", "customer_id LIKE 'cus\\_%' ESCAPE '\\'");
            });
            builder.HasKey(row => row.Id);
            builder.HasAlternateKey(row => row.CustomerId);
            builder.HasIndex(row => row.OrganizationId).IsUnique();
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}

public sealed class StripeCheckoutRow
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

    public sealed class Configuration : IEntityTypeConfiguration<StripeCheckoutRow>
    {
        public void Configure(EntityTypeBuilder<StripeCheckoutRow> builder)
        {
            builder.ToTable("stripe_checkouts", table =>
            {
                table.HasCheckConstraint("ck_stripe_checkouts_id", "length(id) = 26 AND length(organization_id) = 26");
                table.HasCheckConstraint("ck_stripe_checkouts_checkout", "checkout_id LIKE 'cs\\_%' ESCAPE '\\' AND length(request_id) > 0");
                table.HasCheckConstraint("ck_stripe_checkouts_project", "length(project_id) IN (0, 26)");
            });
            builder.HasKey(row => row.Id);
            builder.Ignore(row => row.Binding);
            builder.HasIndex(row => row.CheckoutId).IsUnique();
            builder.HasIndex(row => row.OrganizationId);
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<StripeCustomerRow>().WithMany().HasForeignKey(row => row.CustomerId).HasPrincipalKey(customer => customer.CustomerId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}

public sealed class BillingChangeReceipt : IBillingRow
{
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

    public sealed class Configuration : IEntityTypeConfiguration<BillingChangeReceipt>
    {
        public void Configure(EntityTypeBuilder<BillingChangeReceipt> builder)
        {
            builder.ToTable("billing_changes", table =>
            {
                table.HasCheckConstraint("ck_billing_changes_id", "length(id) = 26 AND length(version) = 26 AND length(organization_id) = 26");
                table.HasCheckConstraint("ck_billing_changes_action", "action IN ('change', 'undo') AND status IN ('reserved', 'applying', 'applied', 'abandoned')");
                table.HasCheckConstraint("ck_billing_changes_request", "length(request_id) > 0 AND length(subscription_id) > 0 AND revision >= 1");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => row.OrganizationId).IsUnique();
            builder.Property(row => row.Version).IsConcurrencyToken();
            builder.Property(row => row.PeriodEnd).HasConversion(BillingColumns.Utc);
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}

public sealed class InternalBillingRow
{
    public const string OrganizationScope = "organization";

    public required string Id { get; init; }

    public required string Scope { get; init; }

    public required string OrganizationId { get; init; }

    public required string PlanId { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<InternalBillingRow>
    {
        public void Configure(EntityTypeBuilder<InternalBillingRow> builder)
        {
            builder.ToTable("internal_billing", table =>
            {
                table.HasCheckConstraint("ck_internal_billing_id", "length(id) = 26 AND length(organization_id) = 26 AND length(plan_id) = 26");
                table.HasCheckConstraint("ck_internal_billing_scope", $"scope = '{OrganizationScope}'");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => row.Scope).IsUnique();
            builder.HasOne<OrganizationRow>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}

internal static class BillingColumns
{
    public static ValueConverter<DateTimeOffset, DateTime> Utc { get; } = new(value => value.UtcDateTime, value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));

    public static ValueConverter<DateTimeOffset?, DateTime?> OptionalUtc { get; } = new(
        value => value.HasValue ? value.Value.UtcDateTime : null,
        value => value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null);
}

[JsonSerializable(typeof(InternalBillingReservation))]
internal sealed partial class BillingRowJson : JsonSerializerContext;
