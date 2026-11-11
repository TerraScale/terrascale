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
    public string CheckoutId { get; set; } = "";
    public required DateTime CreatedAt { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<PendingOrganizationCheckout>
    {
        public void Configure(EntityTypeBuilder<PendingOrganizationCheckout> builder)
        {
            builder.ToTable("pending_organization_checkouts", table =>
            {
                table.HasCheckConstraint("ck_pending_organization_checkouts_id", "length(user_id) = 26 AND length(draft_id) = 26");
                table.HasCheckConstraint("ck_pending_organization_checkouts_name", "length(name) BETWEEN 1 AND 128");
            });
            builder.HasKey(row => row.UserId);
            builder.Property(row => row.Version).IsConcurrencyToken();
            builder.HasIndex(row => row.DraftId).IsUnique();
            builder.HasIndex(row => row.CheckoutId).IsUnique().HasFilter("checkout_id <> ''");
        }
    }
}
