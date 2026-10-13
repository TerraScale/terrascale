using System.Globalization;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Features.Accounts;

public sealed record BillingPlan(string Id, string Key, string Name, long PriceCents, long AnnualPriceCents, int TrialDays,
    string DatabaseAllowance, string StorageAllowance, string MemberAllowance, string ReadsAllowance, string WritesAllowance)
{
    public string MonthlyPrice => "$" + (PriceCents / 100m).ToString("F2", CultureInfo.InvariantCulture);
    public string AnnualPrice => "$" + (AnnualPriceCents / 100m).ToString("F2", CultureInfo.InvariantCulture);
}

public sealed record PlanAllowances(long MonthlyReads, long MonthlyWrites, long StorageBytes);
public sealed record PlanResourceLimits(int Databases, int Members);

public static class PlanCatalog
{
    public static IReadOnlyList<BillingPlan> Plans { get; } = Array.AsReadOnly(new[]
    {
        new BillingPlan("3BN8V1JQWTB3R3NSZ26SERTHWT", "free", "Free", 0, 0, 0, "1 database", "0.1 GB-region storage", "Limit not published", "100K reads/month", "100K writes/month"),
        new BillingPlan("443RCMMF70MNP6S1QMT230NF99", "starter", "Starter", 500, 5000, 30, "5 databases", "5 GB-region storage", "5 team members", "1M reads/month", "1M writes/month"),
        new BillingPlan("7CNW2H87JE68ATZX224CV2G625", "pro", "Pro", 5000, 50000, 30, "25 databases", "25 GB-region storage", "25 team members", "100M reads/month", "100M writes/month"),
        new BillingPlan("09XCWRH05BWWBADSBPTPDMFA7D", "business", "Business", 9900, 99000, 30, "100 databases", "5 TB-region storage", "100 team members", "50M reads/month", "50M writes/month")
    });

    public static BillingPlan For(string key) => Plans.SingleOrDefault(plan => plan.Key == key)
        ?? throw new OnboardingValidationException("Choose an available plan.");

    public static BillingPlan? For(OrganizationRecord organization)
    {
        if (organization.InternalBilling is { } internalPolicy)
        {
            internalPolicy.Validate(organization);
            return internalPolicy.Plan;
        }
        return Plans.SingleOrDefault(plan => plan.Id == organization.PlanId || plan.Key == organization.PlanId);
    }

    public static PlanAllowances Limits(OrganizationRecord organization) => organization.InternalBilling is { } policy
        ? Validated(organization, policy.Allowances) : Limits(organization.PlanId);
    public static PlanResourceLimits ResourceLimits(OrganizationRecord organization) => organization.InternalBilling is { } policy
        ? Validated(organization, policy.Resources) : ResourceLimits(organization.PlanId);
    private static T Validated<T>(OrganizationRecord organization, T value)
    {
        organization.InternalBilling!.Validate(organization);
        return value;
    }

    // Exact persisted Go plan definitions: storage is configured in MiB even
    // where the customer-facing allowance label says GB-region.
    public static PlanAllowances Limits(string id)
    {
        var key = Plans.SingleOrDefault(plan => plan.Id == id || plan.Key == id)?.Key;
        return key switch
        {
            "free" => new(100_000, 100_000, 103L << 20),
            "starter" => new(1_000_000, 1_000_000, 5L << 30),
            "pro" => new(100_000_000, 100_000_000, 25L << 30),
            "business" => new(50_000_000, 50_000_000, 5L << 40),
            _ => new(0, 0, 0)
        };
    }

    public static PlanResourceLimits ResourceLimits(string id) => Plans.SingleOrDefault(plan => plan.Id == id || plan.Key == id)?.Key switch
    {
        "free" => new(1, 0),
        "starter" => new(5, 5),
        "pro" => new(25, 25),
        "business" => new(100, 100),
        _ => new(0, 0)
    };
}
