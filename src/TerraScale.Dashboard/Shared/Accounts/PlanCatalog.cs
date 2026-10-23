using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Shared.Accounts;

public sealed record BillingPlan(string Id, string Key, string Name, long PriceCents, long AnnualPriceCents, int TrialDays,
    PlanAllowances Allowances, PlanResourceLimits Resources)
{
    public string MonthlyPrice => "$" + (PriceCents / 100m).ToString("F2", CultureInfo.InvariantCulture);
    public string AnnualPrice => "$" + (AnnualPriceCents / 100m).ToString("F2", CultureInfo.InvariantCulture);
    public string DatabaseAllowance => Resources.Databases.ToString(CultureInfo.InvariantCulture) + (Resources.Databases == 1 ? " database" : " databases");
    public string StorageAllowance => (Allowances.StorageBytes / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + " GB-region storage";
    public string MemberAllowance => Resources.Members.ToString(CultureInfo.InvariantCulture) + (Resources.Members == 1 ? " team member" : " team members");
    public string OperationsAllowance => Allowances.MonthlyOperations.ToString("N0", CultureInfo.InvariantCulture) + " " + PlanCatalog.OperationUnitName.ToLowerInvariant() + "s/month (" + (PlanCatalog.OperationUnitBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB)";
}

public sealed record PlanAllowances(long MonthlyOperations, long StorageBytes);
public sealed record PlanResourceLimits(int Databases, int Members, int Projects);

public static class PlanCatalog
{
    internal static string PublishedJson { get; } = ReadPublishedJson();
    private static readonly JsonElement catalog = JsonSerializer.Deserialize<JsonElement>(PublishedJson);
    public static int OperationUnitBytes => catalog.GetProperty("metering").GetProperty("operationUnit").GetProperty("bytes").GetInt32();
    public static string OperationUnitName => catalog.GetProperty("metering").GetProperty("operationUnit").GetProperty("name").GetString()!;
    public static string AllowanceDefinition => catalog.GetProperty("metering").GetProperty("allowances").GetProperty("definition").GetString()!;
    public static string OperationUnitDefinition => catalog.GetProperty("metering").GetProperty("operationUnit").GetProperty("definition").GetString()!;
    public static IReadOnlyList<BillingPlan> Plans { get; } = Array.AsReadOnly(catalog.GetProperty("plans").EnumerateArray()
        .Where(plan => plan.GetProperty("available").GetBoolean()).Select(plan =>
        {
            var key = plan.GetProperty("id").GetString()!;
            // Hobby retains the existing internal key and persisted plan identity.
            var id = key switch
            {
                "hobby" => OrganizationRecord.FreePlanId,
                "starter" => OrganizationRecord.StarterPlanId,
                "pro" => "7CNW2H87JE68ATZX224CV2G625",
                _ => throw new IOException("The published plan has no persisted identity.")
            };
            var limits = plan.GetProperty("limits");
            return new BillingPlan(id, key == "hobby" ? "free" : key, plan.GetProperty("name").GetString()!,
                checked((long)(plan.GetProperty("priceMonthlyUsd").GetDecimal() * 100)),
                checked((long)(plan.GetProperty("priceAnnualUsd").GetDecimal() * 100)),
                plan.GetProperty("trialDays").ValueKind == JsonValueKind.Null ? 0 : plan.GetProperty("trialDays").GetInt32(),
                new(limits.GetProperty("operationUnitsPerMonth").GetInt64(), checked((long)(limits.GetProperty("storageGb").GetDecimal() * 1_000_000_000))),
                new(limits.GetProperty("databases").GetInt32(), limits.GetProperty("members").GetInt32(), limits.GetProperty("projects").GetInt32()));
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
