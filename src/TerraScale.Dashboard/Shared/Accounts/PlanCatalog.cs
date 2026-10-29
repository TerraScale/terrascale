using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Shared.Formatting;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Shared.Accounts;

public sealed record BillingPlan(string Id, string Key, string Name, long PriceCents, long AnnualPriceCents, int TrialDays,
    PlanAllowances Allowances, PlanResourceLimits Resources, IReadOnlyList<string> BillingIntervals)
{
    // Catalog month/year map to the checkout names monthly/annual; monthly is the default whenever it is sold.
    public IReadOnlyList<string> CheckoutIntervals => BillingIntervals.Select(interval => interval == "year" ? "annual" : "monthly").ToArray();
    public string CheckoutInterval => CheckoutIntervals.Contains("monthly") ? "monthly" : CheckoutIntervals.FirstOrDefault() ?? "";
    public bool SupportsInterval(string interval) => PriceCents > 0 && CheckoutIntervals.Contains(interval);
    public long IntervalPriceCents(string interval) => interval == "annual" ? AnnualPriceCents : PriceCents;
    public string BillingDescription => CheckoutIntervals.Contains("annual")
        ? (CheckoutIntervals.Contains("monthly") ? "Billed monthly, or " : "Billed yearly at ") + AnnualPrice + " a year" : "Billed monthly";
    public string MonthlyPrice => MoneyFormat.Money(PriceCents);
    public string AnnualPrice => MoneyFormat.Money(AnnualPriceCents);
    public string Price => CheckoutInterval == "annual" ? AnnualPrice : MonthlyPrice;
    public string PriceUnit => CheckoutInterval == "annual" ? "/ year" : "/ month";
    public string DatabaseAllowance => ValueFormat.Plural(Resources.Databases, "database");
    public string StorageAllowance => (Allowances.StorageBytes / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + " GB-region storage";
    public string EgressAllowance => (Allowances.MonthlyEgressBytes / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + " GB client egress/month";
    public string MemberAllowance => ValueFormat.Plural(Resources.Members, "team member");
    public string OperationsAllowance => Allowances.MonthlyOperations.ToString("N0", CultureInfo.InvariantCulture) + " " + PlanCatalog.OperationUnitName.ToLowerInvariant() + "s/month (" + (PlanCatalog.OperationUnitBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB)";
}

// MCP field descriptions shared by every tool that lists plan prices.
public static class PlanPriceText
{
    public const string PriceCents = "US cents charged each month on the month interval.";
    public const string AnnualPriceCents = "US cents charged each year on the year interval.";
    public const string BillingIntervals = "Intervals the plan is sold at: month, year, or both.";
    public const string Price = "Default price as the dashboard shows it, such as $3.00 / month.";
}

public sealed record PlanAllowances(long MonthlyOperations, long StorageBytes, long MonthlyEgressBytes = 0);
public sealed record PlanResourceLimits(int Databases, int Members, int Projects);

public sealed record PlanComparison(string Key, string Name, long PriceCents, string Price, string PriceUnit, string BillingDescription, IReadOnlyList<string> Features);

public static class PlanCatalog
{
    internal static string PublishedJson { get; } = ReadPublishedJson();
    private static readonly JsonElement catalog = JsonSerializer.Deserialize<JsonElement>(PublishedJson);
    public static int OperationUnitBytes => catalog.GetProperty("metering").GetProperty("operationUnit").GetProperty("bytes").GetInt32();
    public static string OperationUnitName => catalog.GetProperty("metering").GetProperty("operationUnit").GetProperty("name").GetString()!;
    public static decimal OperationUnitsPerMillionUsd => catalog.GetProperty("metering").GetProperty("overageRates").GetProperty("operationUnitsPerMillionUsd").GetDecimal();
    public static string AllowanceDefinition => catalog.GetProperty("metering").GetProperty("allowances").GetProperty("definition").GetString()!;
    public static string EnterpriseBilling => catalog.GetProperty("plans").EnumerateArray().Single(plan => plan.GetProperty("id").GetString() == "enterprise").GetProperty("billing").GetString()!;
    public static string OperationUnitDefinition => catalog.GetProperty("metering").GetProperty("operationUnit").GetProperty("definition").GetString()!;
    public static IReadOnlyList<BillingPlan> Plans { get; } = Array.AsReadOnly(catalog.GetProperty("plans").EnumerateArray()
        .Where(plan => plan.GetProperty("available").GetBoolean()).Select(plan =>
        {
            var key = plan.GetProperty("id").GetString()!;
            // Free retains its existing internal key and persisted plan identity.
            var id = key switch
            {
                "free" => OrganizationRecord.FreePlanId,
                "starter" => OrganizationRecord.StarterPlanId,
                "pro" => "7CNW2H87JE68ATZX224CV2G625",
                "team" => "01K8A00000TEAM000000000000",
                _ => throw new IOException("The published plan has no persisted identity.")
            };
            var limits = plan.GetProperty("limits");
            return new BillingPlan(id, key, plan.GetProperty("name").GetString()!,
                checked((long)(plan.GetProperty("priceMonthlyUsd").GetDecimal() * 100)),
                plan.GetProperty("priceAnnualUsd").ValueKind == JsonValueKind.Null ? 0 : checked((long)(plan.GetProperty("priceAnnualUsd").GetDecimal() * 100)),
                plan.GetProperty("trialDays").ValueKind == JsonValueKind.Null ? 0 : plan.GetProperty("trialDays").GetInt32(),
                new(limits.GetProperty("operationUnitsPerMonth").GetInt64(), checked((long)(limits.GetProperty("storageGb").GetDecimal() * 1_000_000_000)), checked((long)(limits.GetProperty("egressGbPerMonth").GetDecimal() * 1_000_000_000))),
                new(limits.GetProperty("databases").GetInt32(), limits.GetProperty("members").GetInt32(), limits.GetProperty("projects").GetInt32()), Array.AsReadOnly(plan.GetProperty("billingIntervals").EnumerateArray().Select(interval => interval.GetString()!).ToArray()));
        }).ToArray());

    // Display-only catalog entries never make contact-only plans selectable.
    public static IReadOnlyList<PlanComparison> ComparisonPlans { get; } = Array.AsReadOnly(catalog.GetProperty("plans").EnumerateArray().Select(plan =>
    {
        var key = plan.GetProperty("id").GetString()!;
        var limits = plan.GetProperty("limits");
        var selectable = Plans.FirstOrDefault(candidate => candidate.Key == key);
        string Resource(string property, string singular, string plural) => limits.GetProperty(property).ValueKind == JsonValueKind.Null
            ? plural + ": contact sales" : ValueFormat.Plural(limits.GetProperty(property).GetInt32(), singular, plural);
        var cents = checked((long)(plan.GetProperty("priceMonthlyUsd").GetDecimal() * 100));
        return new PlanComparison(key, plan.GetProperty("name").GetString()!, cents,
            selectable?.Price ?? MoneyFormat.Money(cents), selectable?.PriceUnit ?? "/ month",
            key == "free" ? "No card required" : selectable?.BillingDescription ?? plan.GetProperty("billing").GetString()!,
            Array.AsReadOnly(new[] {
                limits.GetProperty("storageGb").GetDecimal().ToString("0.##", CultureInfo.InvariantCulture) + " GB-region storage",
                limits.GetProperty("operationUnitsPerMonth").GetInt64().ToString("N0", CultureInfo.InvariantCulture) + " " + OperationUnitName.ToLowerInvariant() + "s/month (" + (OperationUnitBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB)",
                limits.GetProperty("egressGbPerMonth").GetDecimal().ToString("0.##", CultureInfo.InvariantCulture) + " GB client egress/month",
                Resource("projects", "project", "projects"), Resource("databases", "database", "databases"), Resource("members", "team member", "team members") }));
    }).ToArray());

    private static string ReadPublishedJson()
    {
        using var stream = typeof(PlanCatalog).Assembly.GetManifestResourceStream("TerraScale.Dashboard.Shared.Accounts.pricing.json")
            ?? throw new IOException("The published plan catalog is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

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

    public static PlanAllowances Limits(string id) => Plans.SingleOrDefault(plan => plan.Id == id || plan.Key == id)?.Allowances ?? new(0, 0);

    public static PlanResourceLimits ResourceLimits(string id) => Plans.SingleOrDefault(plan => plan.Id == id || plan.Key == id)?.Resources ?? new(0, 0, 0);
}
