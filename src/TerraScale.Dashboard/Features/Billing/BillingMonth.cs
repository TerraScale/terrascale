using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata;
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
    public required string PlanName { get; init; }
    public required long OperationsAllowance { get; init; }
    public required long StorageAllowance { get; init; }
    public required decimal OperationRate { get; init; }
    public required decimal StorageRate { get; init; }
    public required string Currency { get; init; }
    public required string PricingVersion { get; init; }

    public static void Observe(BillingBinding binding, BillingPlan plan, DateTimeOffset now)
    {
        binding.ObservedPlanName = plan.Name;
        binding.ObservedOperationsAllowance = plan.Allowances.MonthlyOperations;
        binding.ObservedStorageAllowance = plan.Allowances.StorageBytes;
        binding.ObservedOperationRate = OperationUnitOverage.UnitAmountCents;
        binding.ObservedStorageRate = StorageOverage.UnitAmountCents;
        binding.ObservedCurrency = "usd";
        var terms = string.Create(CultureInfo.InvariantCulture, $"{plan.Key}|{plan.Name}|{plan.Allowances.MonthlyOperations}|{plan.Allowances.StorageBytes}|{binding.ObservedOperationRate}|{binding.ObservedStorageRate}|usd");
        var version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(terms)));
        if (binding.ObservedPricingVersion != version)
        {
            binding.EffectivePlanSince = now;
        }
        binding.ObservedPricingVersion = version;
        if (binding.OverageStartsAt == default)
        {
            binding.OverageStartsAt = new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        }
    }

    public static async Task<bool> RecordAsync(IDbContextFactory<DashboardDbContext> storage, BillingBinding binding, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var month = current.AddMonths(-1);
        if (binding.EffectivePlanSince is not { } since || binding.SubscriptionId.Length == 0 || binding.ObservedPricingVersion.Length == 0 || binding.State == "deleting")
        {
            return true;
        }

        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        await ObserveAsync(db, binding, cancellationToken);
        if (since < current && month >= binding.OverageStartsAt && binding.EffectivePlan != "free" &&
            !await db.BillingMonths.AnyAsync(row => row.OrganizationId == binding.OrganizationId && row.SubscriptionId == binding.SubscriptionId && row.Month == month, cancellationToken))
        {
            db.BillingMonths.Add(new()
            {
                OrganizationId = binding.OrganizationId,
                Month = month,
                SubscriptionId = binding.SubscriptionId,
                Plan = binding.EffectivePlan,
                PlanName = binding.ObservedPlanName,
                OperationsAllowance = binding.ObservedOperationsAllowance,
                StorageAllowance = binding.ObservedStorageAllowance,
                OperationRate = binding.ObservedOperationRate,
                StorageRate = binding.ObservedStorageRate,
                Currency = binding.ObservedCurrency,
                PricingVersion = binding.ObservedPricingVersion
            });
        }
        if (!db.ChangeTracker.HasChanges()) { return true; }
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
            throw new IOException("The monthly billing terms could not be persisted.", error);
        }
    }

    internal static async Task ObserveAsync(DashboardDbContext db, BillingBinding binding, CancellationToken cancellationToken)
    {
        if (binding.EffectivePlanSince is not { } since || since == default || binding.SubscriptionId.Length == 0) { return; }
        if (await db.BillingTermObservations.AnyAsync(row => row.OrganizationId == binding.OrganizationId && row.SubscriptionId == binding.SubscriptionId && row.ObservedAt == since && row.PricingVersion == binding.ObservedPricingVersion, cancellationToken)) { return; }
        db.BillingTermObservations.Add(new()
        {
            OrganizationId = binding.OrganizationId,
            SubscriptionId = binding.SubscriptionId,
            ObservedAt = since,
            Month = new(since.Year, since.Month, 1, 0, 0, 0, TimeSpan.Zero),
            Plan = binding.EffectivePlan,
            PlanName = binding.ObservedPlanName,
            OperationsAllowance = binding.ObservedOperationsAllowance,
            StorageAllowance = binding.ObservedStorageAllowance,
            OperationRate = binding.ObservedOperationRate,
            StorageRate = binding.ObservedStorageRate,
            Currency = binding.ObservedCurrency,
            PricingVersion = binding.ObservedPricingVersion
        });
    }

    public sealed class Configuration : IEntityTypeConfiguration<BillingMonth>
    {
        public void Configure(EntityTypeBuilder<BillingMonth> builder)
        {
            builder.ToTable("billing_months", table => table.HasCheckConstraint("ck_billing_months_plan", "length(subscription_id) > 0 AND length(pricing_version) > 0"));
            builder.HasKey(row => new { row.OrganizationId, row.SubscriptionId, row.Month });
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.Property(row => row.Month).HasConversion(BillingColumns.Utc);
        }
    }
}

public sealed class BillingTermObservation
{
    public required string OrganizationId { get; init; }
    public required string SubscriptionId { get; init; }
    public required DateTimeOffset Month { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required string Plan { get; init; }
    public required string PlanName { get; init; }
    public required long OperationsAllowance { get; init; }
    public required long StorageAllowance { get; init; }
    public required decimal OperationRate { get; init; }
    public required decimal StorageRate { get; init; }
    public required string Currency { get; init; }
    public required string PricingVersion { get; init; }

    public BillingMonth TermsFor(DateTimeOffset month) => new()
    {
        OrganizationId = OrganizationId,
        SubscriptionId = SubscriptionId,
        Month = month,
        Plan = Plan,
        PlanName = PlanName,
        OperationsAllowance = OperationsAllowance,
        StorageAllowance = StorageAllowance,
        OperationRate = OperationRate,
        StorageRate = StorageRate,
        Currency = Currency,
        PricingVersion = PricingVersion
    };

    public sealed class Configuration : IEntityTypeConfiguration<BillingTermObservation>
    {
        public void Configure(EntityTypeBuilder<BillingTermObservation> builder)
        {
            builder.ToTable("billing_term_observations", table => table.HasCheckConstraint("ck_billing_term_observations", "length(subscription_id) > 0"));
            builder.HasKey(row => new { row.OrganizationId, row.SubscriptionId, row.ObservedAt, row.PricingVersion });
            builder.HasIndex(row => row.Month);
            builder.HasOne<BillingBinding>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.Property(row => row.Month).HasConversion(BillingColumns.Utc);
            builder.Property(row => row.ObservedAt).HasConversion(BillingColumns.Utc);
            foreach (var property in builder.Metadata.GetProperties())
            {
                property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            }
        }
    }
}
