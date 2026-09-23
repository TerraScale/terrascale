using System.Collections.Immutable;
using System.Globalization;

namespace TerraScale.Dashboard.Shared.State;

// Billing + organization usage surfaces for the Usage page (1:1 with the
// usage-data views ts-dashboard consumes). Extends the active account world.
public sealed partial class DashboardStore
{
    public sealed record InvoiceRow(
        string InvoiceId,
        long AmountCents,
        string Currency,
        string Status,          // paid | open | void
        string PeriodStart,
        string PeriodEnd,
        string IssuedAt,
        string? HostedInvoiceUrl = null,
        string? InvoicePdf = null);

    public sealed record PlanResourceRow(
        string Resource,
        string Used,
        string Included,
        string Balance,
        string Status,          // Normal | Approaching | Exceeded
        string Tone,
        double Percent);

    public sealed record ProjectContributionRow(
        string ProjectId,
        string ProjectName,
        string Storage,
        string Writes,
        string Reads,
        string Egress,
        long StorageBytes,
        long WriteUnits);

    /// <summary>A checkout option on the usage page. <paramref name="Href"/> is
    /// the demo local-checkout link; the page swaps it for the Stripe sandbox
    /// checkout when the provider is configured.</summary>
    public sealed record CheckoutPlanRow(string PlanId, string PlanKey, string Label, string Href);

    public string BillingAccountStatus => IsPersonalOrganization ? "personal_free" : ActiveOrganization.BillingStatus;
    public string BillingPlanName => IsPersonalOrganization ? "Personal free allowance" : Plans.FirstOrDefault(p => p.Id == ActiveOrganization.PlanId)?.Name ?? "Billing required";
    public string BillingPlanId => IsPersonalOrganization ? "plan_free" : ActiveOrganization.PlanId;
    public string BillingInterval => "monthly";
    public string BillingRenewal => ActiveOrganization.BillingRenewal ?? "";

    public ImmutableArray<InvoiceRow> Invoices { get => World.Invoices; private set => World.Invoices = value; }

    public long TotalInvoices => Invoices.Length;

    public string UsagePeriodStart => "2026-09-01T00:00:00Z";

    // v1 usage-page account states. The demo world keeps billing fully loaded.
    public bool BillingPartial => false;
    public string? StorageCoverageDeadline => null;
    public int UsageRetentionDays => 7;

    // v1 BillingControls: a managed subscription (active/trialing with a
    // scheduled renewal) hides checkout and shows portal-only "Manage subscription".
    public bool HasManagedSubscription =>
        !IsPersonalOrganization
        && (BillingAccountStatus is "active" or "trialing")
        && DateTimeOffset.TryParse(BillingRenewal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var renewal)
        && renewal > DateTimeOffset.UtcNow;

    // v1: past_due/unpaid accounts get the "Billing action required" warning.
    public bool BillingActionRequired => BillingAccountStatus is "past_due" or "unpaid";

    public long StorageBytesTotal => Usage.StorageMb * 1024 * 1024;
    public long StorageLimitBytes => Usage.StorageLimitMb * 1024 * 1024;
    public double StoragePercentUsed => StorageLimitBytes is 0 ? 0 : (double)StorageBytesTotal / StorageLimitBytes * 100;

    // Checkout catalog offered on the usage page (ts-dashboard billing plans).
    public ImmutableArray<CheckoutPlanRow> CheckoutPlans { get; } =
    [
        new("plan_starter", "starter", $"Starter monthly - {FormatMoney(500, "USD")}", "/checkout/usage_plan_starter"),
        new("plan_pro", "pro", $"Pro monthly - {FormatMoney(5000, "USD")}", "/checkout/usage_plan_pro"),
        new("plan_scale_v2", "business", $"Scale monthly - {FormatMoney(50000, "USD")}", "/checkout/usage_plan_scale_v2"),
        new("plan_business", "business", $"Business monthly - {FormatMoney(9900, "USD")}", "/checkout/usage_plan_business"),
    ];

    // Spending controls (paid-excess consent + organization cap), persisted in memory.
    public bool PaidExcessEnabled { get => World.PaidExcessEnabled; private set => World.PaidExcessEnabled = value; }
    public string? SpendCapUsd { get => World.SpendCapUsd; private set => World.SpendCapUsd = value; }

    public void UpdateSpendingControls(bool enabled, string? capUsd)
    {
        if (capUsd is { Length: > 0 }
            && (!decimal.TryParse(capUsd, NumberStyles.Number, CultureInfo.InvariantCulture, out var cap)
                || cap < 0
                || decimal.Round(cap, 2) != cap))
        {
            return; // invalid caps keep the previous value
        }

        PaidExcessEnabled = enabled;
        SpendCapUsd = capUsd is { Length: > 0 } ? capUsd : null;
    }

    // Optional lower caps per database, on top of the organization cap.
    public string? DatabaseSpendCap(string databaseId) =>
        World.DatabaseSpendCaps.TryGetValue(databaseId, out var cap) && cap.Length > 0 ? cap : null;

    public void UpdateDatabaseSpendCaps(IReadOnlyDictionary<string, string> capsById)
    {
        foreach (var (databaseId, cap) in capsById)
        {
            if (cap.Trim().Length == 0
                || (decimal.TryParse(cap.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                    && value >= 0
                    && decimal.Round(value, 2) == value))
            {
                World.DatabaseSpendCaps[databaseId] = cap.Trim();
            }
        }
    }

    /// <summary>Organization allowances vs. current totals (plan resource table).</summary>
    public ImmutableArray<PlanResourceRow> OrganizationPlanResources()
    {
        var plan = Plans.First(p => p.Id == BillingPlanId);
        var storageLimitBytes = plan.StorageLimitMb * 1024L * 1024;
        var storageRemaining = storageLimitBytes - StorageBytesTotal;
        var (storageStatus, storageTone) = ResourceStatus(StoragePercentUsed);

        var writeLimit = plan.RequestLimit;
        var writeExcess = Usage.WriteUnits - writeLimit;
        var writePercent = writeLimit is 0 ? 100 : (double)Usage.WriteUnits / writeLimit * 100;
        var (writeStatus, writeTone) = ResourceStatus(writePercent);

        return
        [
            new("Logical storage",
                FormatBytes(StorageBytesTotal),
                FormatBytes(storageLimitBytes),
                storageRemaining >= 0
                    ? $"{FormatBytes(storageRemaining)} remaining"
                    : $"{FormatBytes(-storageRemaining)} excess",
                storageStatus,
                storageTone,
                StoragePercentUsed),
            new("Writes",
                FormatNumber(Usage.WriteUnits),
                FormatNumber(writeLimit),
                writeExcess > 0
                    ? $"{FormatNumber(writeExcess)} excess"
                    : $"{FormatNumber(-writeExcess)} remaining",
                writeStatus,
                writeTone,
                writePercent),
        ];
    }

    private static (string Status, string Tone) ResourceStatus(double percentUsed) => percentUsed switch
    {
        >= 100 => ("Exceeded", "danger"),
        >= 80 => ("Approaching", "warning"),
        _ => ("Normal", "success"),
    };

    /// <summary>Per-project contribution rows (reads/egress are unbilled facts).</summary>
    public ImmutableArray<ProjectContributionRow> ProjectContributionRows() =>
        [.. Projects.Select(project =>
        {
            var storageBytes = Databases.Where(d => d.ProjectId == project.Id).Sum(d => d.SizeMb) * 1024L * 1024;
            var writes = project.Id == ActiveProjectId
                ? Usage.WriteUnits
                : Databases.Where(d => d.ProjectId == project.Id).Sum(d => d.DocumentCount);
            return new ProjectContributionRow(
                project.Id,
                project.Name,
                FormatBytes(storageBytes),
                FormatNumber(writes),
                "Not reported",
                "Not reported",
                storageBytes,
                writes);
        })];

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        while (bytes >= 1024 && index < units.Length - 1)
        {
            bytes /= 1024;
            index++;
        }

        return $"{bytes:0.#} {units[index]}";
    }

    public static string FormatNumber(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string FormatUtcDateTime(string iso)
    {
        return DateTimeOffset.TryParse(iso, out var time)
            ? time.UtcDateTime.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture)
            : "Unknown";
    }

    public static string FormatDate(string iso)
    {
        return DateTimeOffset.TryParse(iso, out var time)
            ? time.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
            : "Unknown";
    }

    public static string FormatMoney(long cents, string currency) =>
        currency.ToUpperInvariant() switch
        {
            "USD" => $"${cents / 100m:F2}",
            "EUR" => $"€{cents / 100m:F2}",
            "GBP" => $"£{cents / 100m:F2}",
            var other => $"{other} {cents / 100m:F2}",
        };

    public static string FormatPercent(double value) =>
        double.IsFinite(value) ? $"{value:0.#}%" : "Unknown";
}
