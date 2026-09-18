using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

// Billing + organization usage surfaces for the Usage page (1:1 with the
// usage-data views ts-dashboard consumes). Extends the seeded store.
public sealed partial class DashboardStore
{
    public sealed record BillingMetricView(string Label, string Value, string Tone);

    public sealed record InvoiceRow(
        string InvoiceId,
        long AmountCents,
        string Currency,
        string Status,          // paid | open | void
        string PeriodStart,
        string PeriodEnd,
        string IssuedAt);

    public sealed record PlanImpactRow(
        string PlanId,
        string PlanName,
        string Compatibility,   // current | compatible | exceeds
        string Impact,
        string Timing);

    public string BillingAccountStatus => "active";
    public string BillingPlanName => "Scale";
    public string BillingPlanId => "plan_scale";
    public string BillingInterval => "monthly";
    public string BillingRenewal => "2026-10-01";
    public bool HasManagedSubscription => true;

    public ImmutableArray<InvoiceRow> Invoices { get; private set; } =
    [
        new("in_01JCA2QG4M6P8S1V3X5Z7B9DF", 2900, "USD", "paid", "2026-08-01T00:00:00Z", "2026-09-01T00:00:00Z", "2026-09-01T08:02:00Z"),
        new("in_01JCA3RH5N7Q9T2W4Y6A8C2EG", 4140, "USD", "open", "2026-09-01T00:00:00Z", "2026-10-01T00:00:00Z", "2026-10-01T08:00:00Z"),
    ];

    public long TotalInvoices => Invoices.Length;

    public string UsagePeriodStart => "2026-09-01T00:00:00Z";
    public string UsagePeriodEnd => "2026-10-01T00:00:00Z";

    public string OrganizationStorageLabel => FormatBytes(StorageBytesTotal);
    public long StorageBytesTotal => Usage.StorageMb * 1024 * 1024;
    public long StorageLimitBytes => Usage.StorageLimitMb * 1024 * 1024;
    public double StoragePercentUsed => StorageLimitBytes is 0 ? 0 : (double)StorageBytesTotal / StorageLimitBytes * 100;

    /// <summary>Plan-change impact table (hasManagedSubscription path).</summary>
    public ImmutableArray<PlanImpactRow> PlanImpactRows()
    {
        List<PlanImpactRow> rows = [];
        foreach (var plan in Plans.Where(p => p.Status == "Active" || p.Status == "Deprecated"))
        {
            var isCurrent = plan.Id == BillingPlanId;
            string compatibility;
            string impact;
            if (isCurrent)
            {
                compatibility = "current";
                impact = "Current logical storage and writes fit this plan's included resources.";
            }
            else if (StorageBytesTotal > plan.StorageLimitMb * 1024 * 1024 || Usage.WriteUnits > plan.RequestLimit)
            {
                compatibility = "exceeds";
                var storageExcess = Math.Max(0, StorageBytesTotal - plan.StorageLimitMb * 1024 * 1024);
                impact = $"Current usage exceeds this plan by {FormatBytes(storageExcess)} storage.";
            }
            else
            {
                compatibility = "compatible";
                impact = "Current logical storage and writes fit this plan's included resources.";
            }

            var timing = isCurrent ? "Current plan"
                : plan.MonthlyUsd > Plans.First(p => p.Id == BillingPlanId).MonthlyUsd ? "Immediately; proration may apply"
                : "Next billing cycle";
            rows.Add(new(plan.Id, plan.Name, compatibility, impact, timing));
        }

        return [.. rows];
    }

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

    public static string FormatNumber(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public static string FormatUtcDateTime(string iso)
    {
        return DateTimeOffset.TryParse(iso, out var time)
            ? time.UtcDateTime.ToString("MMM d, yyyy, h:mm tt", System.Globalization.CultureInfo.InvariantCulture)
            : "Unknown";
    }

    public static string FormatMoney(long cents, string currency) =>
        currency is "USD" || currency is "usd"
            ? $"${cents / 100m:0.00}"
            : $"{currency.ToUpperInvariant()} {(cents / 100m):F2}";

    public static string FormatPercent(double value) =>
        double.IsFinite(value) ? $"{value:0.#}%" : "Unknown";
}
