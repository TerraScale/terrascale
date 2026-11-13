using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing;

public sealed class BillingMonth
{
    public required string OrganizationId { get; init; }
    public required DateTimeOffset Month { get; init; }
    public required string SubscriptionId { get; init; }
    public required string Plan { get; init; }

    public static async Task<bool> RecordAsync(IDbContextFactory<DashboardDbContext> storage, BillingBinding binding, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (binding.EffectivePlanSince is not { } since || binding.SubscriptionId.Length == 0 || binding.EffectivePlan.Length == 0 || binding.Status is not ("active" or "trialing" or "past_due") || PlanCatalog.For(binding.EffectivePlan).PriceCents == 0)
        {
            return true;
        }

        var current = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var month = current.AddMonths(-1);
        if (since >= current || binding.OverageReportedThrough >= month)
        {
            return true;
        }

        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        if (await db.BillingMonths.AnyAsync(row => row.OrganizationId == binding.OrganizationId && row.Month == month, cancellationToken))
        {
            return true;
        }

        db.BillingMonths.Add(new() { OrganizationId = binding.OrganizationId, Month = month, SubscriptionId = binding.SubscriptionId, Plan = binding.EffectivePlan });
        var expected = binding.Version;
        db.Attach(binding);
        binding.Version = AccountService.NewId();
        db.Entry(binding).Property(row => row.Version).OriginalValue = expected;
        db.Entry(binding).Property(row => row.Version).IsModified = true;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException error) when (error is DbUpdateConcurrencyException || error.InnerException is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.UniqueViolation or TerraBaseSqlErrorCodes.PrimaryKeyViolation })
        {
            binding.Version = expected;
            return false;
        }
        catch (DbUpdateException error)
        {
            binding.Version = expected;
            throw new IOException("The monthly billing plan could not be persisted.", error);
        }
    }

    public sealed class Configuration : IEntityTypeConfiguration<BillingMonth>
    {
        public void Configure(EntityTypeBuilder<BillingMonth> builder)
        {
            builder.ToTable("billing_months", table => table.HasCheckConstraint("ck_billing_months_plan", "length(subscription_id) > 0 AND length(plan) > 0"));
            builder.HasKey(row => new { row.OrganizationId, row.Month });
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.Property(row => row.Month).HasConversion(BillingColumns.Utc);
        }
    }
}
