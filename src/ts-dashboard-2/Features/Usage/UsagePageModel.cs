using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Usage;

public sealed record UsagePageModel(
    string PeriodStart,
    string PeriodEnd,
    IReadOnlyList<UsageStatCard> Stats,
    string OrganizationSubtitle,
    string OrganizationUpdatedAt,
    string OrganizationStorage,
    string OrganizationWrites,
    IReadOnlyList<DashboardStore.PlanResourceRow> PlanResources,
    IReadOnlyList<DashboardStore.ProjectContributionRow> ProjectContributions,
    long StorageUsedBytes,
    long StorageLimitBytes,
    double StoragePercentUsed,
    string StorageTone,
    string StorageRemainingLabel,
    string StoragePercentLabel,
    IReadOnlyList<UsageHistoryRangeOption> HistoryRanges,
    bool RetentionWarning,
    IReadOnlyList<WorkloadPoint> History,
    IReadOnlyList<BarSlice> MeasuredUsage,
    IReadOnlyList<BarSlice> RegionPlacement,
    IReadOnlyList<BarSlice> DatabaseCoverage,
    IReadOnlyList<DashboardDatabase> Databases,
    IReadOnlyList<(string Id, string Name, string? Cap)> DatabaseCaps,
    IReadOnlyList<DashboardStore.CheckoutPlanRow> CheckoutPlans,
    bool PaidExcessEnabled,
    string SpendCap,
    string BillingStatus,
    string BillingStatusTone,
    string PlanName,
    string Price,
    string Frequency,
    string Renewal,
    long InvoiceCount,
    IReadOnlyList<DashboardStore.InvoiceRow> Invoices,
    string? BillingReturn) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static UsagePageModel From(DashboardStore store, string href, string? range, string? billing)
    {
        var databases = store.ProjectDatabases(store.ActiveProjectId);

        // Old page falls back to database/region counters when the summary has no KPIs.
        var regionCount = databases
            .SelectMany(d => new[] { d.PrimaryRegion }.Concat(d.BackupRegion is null ? [] : [d.BackupRegion]))
            .Distinct()
            .Count();
        List<UsageStatCard> stats =
        [
            new("Databases", DashboardStore.FormatNumber(databases.Length), "Saved project databases", "Database"),
            new("Regions", DashboardStore.FormatNumber(regionCount), "Distinct placement regions", "Globe2"),
        ];

        List<BarSlice> measured =
        [
            new("Writes", store.Usage.WriteUnits, DashboardStore.FormatNumber(store.Usage.WriteUnits)),
            new("Reads (unbilled)", store.Usage.ReadUnits, DashboardStore.FormatNumber(store.Usage.ReadUnits)),
            new("Requests", store.Usage.Requests, DashboardStore.FormatNumber(store.Usage.Requests)),
        ];

        var regionPlacement = databases
            .SelectMany(d => new[] { d.PrimaryRegion }.Concat(d.BackupRegion is null ? [] : [d.BackupRegion]))
            .GroupBy(r => r.ToUpperInvariant())
            .Select(g => new BarSlice(g.Key, g.Count()))
            .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
            .ToList();

        var coverage = databases
            .Select(d => new BarSlice(d.Name, 1 + (d.BackupRegion is null ? 0 : 1)))
            .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
            .ToList();

        var selectedRange = store.Workloads.Any(w => w.Range == range) ? range! : "7d";
        var historyRanges = store.Workloads
            .Select(w => new UsageHistoryRangeOption(
                w.Range,
                w.Range switch
                {
                    "24h" => "Last 24 hours",
                    "7d" => "Last 7 days",
                    "30d" => "Last 30 days",
                    "90d" => "Last 90 days",
                    _ => "Last year",
                },
                w.Range == selectedRange))
            .ToList();
        var history = store.Workloads.First(w => w.Range == selectedRange).Points;

        var activePlan = store.Plans.First(p => p.Id == store.BillingPlanId);
        var storageTone = store.StoragePercentUsed >= 100 ? "danger"
            : store.StoragePercentUsed >= 80 ? "warning" : "success";

        return new(
            PeriodStart: store.UsagePeriodStart,
            PeriodEnd: store.UsagePeriodEnd,
            Stats: stats,
            OrganizationSubtitle:
                $"Consolidated across {DashboardStore.FormatNumber(store.Projects.Length)} projects in {store.ActiveOrganizationName} · {store.BillingPlanName} plan",
            OrganizationUpdatedAt: store.OrganizationUpdatedAt,
            OrganizationStorage: DashboardStore.FormatBytes(store.StorageBytesTotal),
            OrganizationWrites: DashboardStore.FormatNumber(store.Usage.WriteUnits),
            PlanResources: [.. store.OrganizationPlanResources()],
            ProjectContributions: [.. store.ProjectContributionRows()],
            StorageUsedBytes: store.StorageBytesTotal,
            StorageLimitBytes: store.StorageLimitBytes,
            StoragePercentUsed: store.StoragePercentUsed,
            StorageTone: storageTone,
            StorageRemainingLabel: DashboardStore.FormatBytes(Math.Max(0, store.StorageLimitBytes - store.StorageBytesTotal)),
            StoragePercentLabel: $"{DashboardStore.FormatPercent(store.StoragePercentUsed)} used",
            HistoryRanges: historyRanges,
            RetentionWarning: selectedRange is "30d" or "90d",
            History: [.. history],
            MeasuredUsage: measured,
            RegionPlacement: regionPlacement,
            DatabaseCoverage: coverage,
            Databases: databases,
            DatabaseCaps: [.. databases.Select(d => (d.Id, d.Name, Cap: store.DatabaseSpendCap(d.Id)))],
            CheckoutPlans: [.. store.CheckoutPlans],
            PaidExcessEnabled: store.PaidExcessEnabled,
            SpendCap: store.SpendCapUsd ?? "",
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
            Renewal: DashboardStore.FormatDate(store.BillingRenewal),
            InvoiceCount: store.TotalInvoices,
            Invoices: [.. store.Invoices],
            BillingReturn: billing)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
