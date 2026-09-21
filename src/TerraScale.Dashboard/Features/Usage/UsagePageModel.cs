using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Usage;

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
    string? BillingReturn,
    bool HasManagedSubscription,
    IReadOnlyList<UsagePlanImpactRow> PlanImpact,
    bool UsagePartial,
    bool BillingPartial,
    bool BillingActionRequired,
    string? StorageCoverageDeadline,
    string RetentionAlertBody,
    string? SavedControlsMessage) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static UsagePageModel From(DashboardStore store, string href, string? range, string? billing, string? saved)
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

        // v1 UsageHistoryPanel: 24h is always offered; longer ranges only within retention.
        var retentionDays = store.UsageRetentionDays;
        string RangeLabel(string value) => value switch
        {
            "24h" => "Last 24 hours",
            "7d" => "Last 7 days",
            "30d" => "Last 30 days",
            "90d" => "Last 90 days",
            _ => "Last year",
        };
        int RangeDays(string value) => value switch { "24h" => 1, "7d" => 7, "30d" => 30, "90d" => 90, _ => 365 };
        string[] allRanges = ["24h", "7d", "30d", "90d", "365d"];
        var offeredRanges = allRanges
            .Where(r => r is "24h" || RangeDays(r) <= retentionDays)
            .ToList();
        var selectedRange = range is not null && offeredRanges.Contains(range) ? range : "7d";
        var history = store.Workloads
            .Where(w => w.Range == selectedRange)
            .SelectMany(w => w.Points)
            .ToList();
        var retentionLimited = range is not null && allRanges.Contains(range) && RangeDays(range) > retentionDays;
        var historyRanges = offeredRanges
            .Select(r => new UsageHistoryRangeOption(r, RangeLabel(r), r == selectedRange))
            .ToList();
        var activePlan = store.Plans.First(p => p.Id == store.BillingPlanId);

        // v1 buildPlanImpactRows: current usage vs. each plan's included storage/writes.
        List<UsagePlanImpactRow> planImpact = [.. store.Plans.Select(plan =>
        {
            var isCurrent = plan.Id == activePlan.Id;
            List<string> excess = [];
            var storageExcess = store.StorageBytesTotal - plan.StorageLimitMb * 1024L * 1024;
            if (storageExcess > 0)
            {
                excess.Add($"{DashboardStore.FormatBytes(storageExcess)} storage");
            }

            var writeExcess = store.Usage.WriteUnits - plan.RequestLimit;
            if (writeExcess > 0)
            {
                excess.Add($"{DashboardStore.FormatNumber(writeExcess)} writes");
            }

            return new UsagePlanImpactRow(
                plan.Name,
                isCurrent ? "Current plan" : excess.Count > 0 ? "Over current usage" : "Within limits",
                isCurrent ? "info" : excess.Count > 0 ? "warning" : "success",
                excess.Count > 0
                    ? $"Current usage exceeds this plan by {string.Join(" and ", excess)}."
                    : "Current logical storage and writes fit this plan's included resources.",
                isCurrent ? "Current plan"
                    : plan.MonthlyUsd > activePlan.MonthlyUsd ? "Immediately; proration may apply"
                    : plan.MonthlyUsd < activePlan.MonthlyUsd ? "Next billing cycle"
                    : "Confirmed by the billing provider");
        })];

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
            RetentionWarning: retentionLimited,
            RetentionAlertBody:
                $"The requested period exceeds the {retentionDays}-day retention window. The accessible range starts {DashboardStore.FormatUtcDateTime(store.UsagePeriodStart)}.",
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
            BillingReturn: billing,
            HasManagedSubscription: store.HasManagedSubscription,
            PlanImpact: planImpact,
            UsagePartial: store.UsagePartial,
            BillingPartial: store.BillingPartial,
            BillingActionRequired: store.BillingActionRequired,
            StorageCoverageDeadline: store.StorageCoverageDeadline,
            SavedControlsMessage: saved switch
            {
                "ok" => "Spending controls saved.",
                "invalid" => "Enter a non-negative dollar amount with at most two decimal places.",
                _ => null,
            })
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
