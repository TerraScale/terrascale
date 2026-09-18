using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Usage;

public sealed record UsageStatCard(string Label, string Value, string Detail, string Icon);

public sealed record BarSlice(string Label, double Value, string? DisplayValue = null);

public sealed record UsagePageModel(
    string PeriodStart,
    string PeriodEnd,
    IReadOnlyList<UsageStatCard> Stats,
    long StorageUsedBytes,
    long StorageLimitBytes,
    double StoragePercentUsed,
    string StorageTone,
    string StorageRemainingLabel,
    IReadOnlyList<WorkloadPoint> History,
    IReadOnlyList<BarSlice> MeasuredUsage,
    IReadOnlyList<BarSlice> RegionPlacement,
    IReadOnlyList<BarSlice> DatabaseCoverage,
    IReadOnlyList<DashboardDatabase> Databases,
    string BillingStatus,
    string BillingStatusTone,
    string PlanName,
    string Price,
    string Frequency,
    string Renewal,
    long InvoiceCount,
    IReadOnlyList<DashboardStore.InvoiceRow> Invoices,
    IReadOnlyList<DashboardStore.PlanImpactRow> PlanImpact,
    string? BillingReturn) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static UsagePageModel From(DashboardStore store, string href, string? billing)
    {
        var databases = store.ProjectDatabases(store.ActiveProjectId);
        var usage = store.Usage;

        List<UsageStatCard> stats =
        [
            new("Storage used", DashboardStore.FormatBytes(usage.StorageMb * 1024L * 1024), "Stored data size", "HardDrive"),
            new("Writes", DashboardStore.FormatNumber(usage.WriteUnits), "Current billing period", "Activity"),
            new("Reads (unbilled)", DashboardStore.FormatNumber(usage.ReadUnits), "Unbilled metered fact for guards and cost attribution", "Activity"),
            new("Storage usage", DashboardStore.FormatPercent(store.StoragePercentUsed), "Storage quota consumed", "HardDrive"),
        ];

        List<BarSlice> measured =
        [
            new("Writes", usage.WriteUnits, DashboardStore.FormatNumber(usage.WriteUnits)),
            new("Reads (unbilled)", usage.ReadUnits, DashboardStore.FormatNumber(usage.ReadUnits)),
            new("Requests", usage.Requests, DashboardStore.FormatNumber(usage.Requests)),
        ];

        var regionCounts = databases
            .SelectMany(d => new[] { d.PrimaryRegion }.Concat(d.BackupRegion is null ? [] : [d.BackupRegion]))
            .GroupBy(r => r.ToUpperInvariant())
            .Select(g => new BarSlice(g.Key, g.Count()))
            .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
            .ToList();

        var coverage = databases
            .Select(d => new BarSlice(d.Name, 1 + (d.BackupRegion is null ? 0 : 1)))
            .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
            .ToList();

        var activePlan = store.Plans.First(p => p.Id == store.BillingPlanId);
        var storageTone = store.StoragePercentUsed >= 100 ? "danger"
            : store.StoragePercentUsed >= 80 ? "warning" : "success";

        return new(
            PeriodStart: store.UsagePeriodStart,
            PeriodEnd: store.UsagePeriodEnd,
            Stats: stats,
            StorageUsedBytes: store.StorageBytesTotal,
            StorageLimitBytes: store.StorageLimitBytes,
            StoragePercentUsed: store.StoragePercentUsed,
            StorageTone: storageTone,
            StorageRemainingLabel: DashboardStore.FormatBytes(Math.Max(0, store.StorageLimitBytes - store.StorageBytesTotal)),
            History: [.. store.Workloads.First(w => w.Range == "30d").Points],
            MeasuredUsage: measured,
            RegionPlacement: regionCounts,
            DatabaseCoverage: coverage,
            Databases: databases,
            BillingStatus: store.BillingAccountStatus,
            BillingStatusTone: store.BillingAccountStatus.ToLowerInvariant() switch
            {
                "active" => "success",
                "trialing" => "info",
                "past_due" or "unpaid" => "warning",
                _ => "neutral",
            },
            PlanName: store.BillingPlanName,
            Price: DashboardStore.FormatMoney((long)(activePlan.MonthlyUsd * 100), "USD"),
            Frequency: "Monthly",
            Renewal: store.BillingRenewal,
            InvoiceCount: store.TotalInvoices,
            Invoices: [.. store.Invoices],
            PlanImpact: [.. store.PlanImpactRows()],
            BillingReturn: billing)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}

public sealed class UsagePageEndpoint(DashboardStore store) : SlicePageEndpoint<UsageSlice, UsagePageModel>(store, "/usage")
{
    protected override UsagePageModel Load() =>
        UsagePageModel.From(Store, PageRoute, HttpContext.Request.Query["billing"].FirstOrDefault());
}
