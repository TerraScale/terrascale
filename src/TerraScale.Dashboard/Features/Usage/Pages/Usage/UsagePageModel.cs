using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Usage.Pages.Usage;

public sealed record UsagePageModel(
    string Tab,
    string ProjectFilter,
    string DatabaseFilter,
    string RangeFilter,
    IReadOnlyList<(string Id, string Name)> ProjectOptions,
    IReadOnlyList<(string Id, string Name)> DatabaseOptions,
    IReadOnlyList<UsageStatCard> Stats,
    IReadOnlyList<UsageStatCard> Traffic,
    IReadOnlyList<DashboardStore.PlanResourceRow> PlanResources,
    IReadOnlyList<DashboardStore.ProjectContributionRow> ProjectContributions,
    IReadOnlyList<UsageHistoryRangeOption> HistoryRanges,
    bool RetentionWarning,
    IReadOnlyList<DateTime> HistoryTimes,
    IReadOnlyList<ChartSeries> History,
    IReadOnlyList<UsageStatCard> HistorySummary,
    IReadOnlyList<DashboardDatabase> Databases,
    IReadOnlyList<(string Id, string Name, string? Cap)> DatabaseCaps,
    string BillingPortalHref,
    bool PaidExcessEnabled,
    string SpendCap,
    string BillingStatus,
    string BillingStatusTone,
    string PlanName,
    string Price,
    string Frequency,
    string Renewal,
    UsageBillingSummary Summary,
    UsageInvoicePage InvoicePage,
    string? BillingReturn,
    bool HasManagedSubscription,
    IReadOnlyList<UsagePlanImpactRow> PlanImpact,
    bool BillingPartial,
    bool BillingActionRequired,
    string? StorageCoverageDeadline,
    string RetentionAlertBody,
    string? SavedControlsMessage,
    int RetentionDays,
    string ExcessSpend,
    double? CapUsedPercent) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static UsagePageModel From(DashboardStore store, StripePaymentOptions stripe, string href, string? range, string? billing, string? saved,
        string? tab = null, string? project = null, string? database = null,
        string? status = null, string? issued = null, int page = 1)
    {
        // Overview filters narrow the database scope; allowances, traffic and
        // workload history are organization totals and stay unfiltered.
        var projectFilter = store.Projects.Any(p => p.Id == project) ? project! : "";
        var projectDatabases = projectFilter is "" ? store.Databases : store.ProjectDatabases(projectFilter);
        var databaseFilter = projectDatabases.Any(d => d.Id == database) ? database! : "";
        var databases = databaseFilter is "" ? projectDatabases : [.. projectDatabases.Where(d => d.Id == databaseFilter)];

        var billingPortalHref = stripe.Enabled
            ? $"/billing/portal?return={Uri.EscapeDataString("/usage")}"
            : "/billing/portal/usage_portal";

        var regionCount = databases
            .SelectMany(d => new[] { d.PrimaryRegion }.Concat(d.BackupRegion is null ? [] : [d.BackupRegion]))
            .Distinct()
            .Count();
        List<UsageStatCard> stats =
        [
            new("Databases", DashboardStore.FormatNumber(databases.Length), "Saved project databases", "Database"),
            new("Regions", DashboardStore.FormatNumber(regionCount), "Distinct placement regions", "Globe2"),
            new("Storage", DashboardStore.FormatBytes(databases.Sum(d => d.SizeMb) * 1024L * 1024), "Logical size", "HardDrive"),
            new("Documents", DashboardStore.FormatNumber(databases.Sum(d => d.DocumentCount)), "Stored documents", "FileText"),
        ];
        List<UsageStatCard> traffic =
        [
            new("Requests", DashboardStore.FormatNumber(store.Usage.Requests), "Current billing period", "Activity"),
            new("Reads", DashboardStore.FormatNumber(store.Usage.ReadUnits), "Unbilled usage fact", "BookOpen"),
        ];

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
        var operations = store.WorkloadOperations(selectedRange);
        List<ChartSeries> history = [.. operations.Where(o => o.Values.Length > 0).Select(o => new ChartSeries(o.Operation, o.Values))];

        // Seeded points are ordinal; place them on whole steps (hours or days) ending at the current hour.
        var count = history.Count is 0 ? 0 : history[0].Values.Count;
        var now = DateTime.UtcNow;
        var end = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var step = TimeSpan.FromDays(RangeDays(selectedRange)) / Math.Max(1, count);
        List<DateTime> historyTimes = [.. Enumerable.Range(0, count).Select(i => end - step * (count - 1 - i))];
        List<UsageStatCard> historySummary = [.. history.Select(s => new UsageStatCard(s.Name, DashboardStore.FormatNumber((long)s.Values.Sum()), "Total this period", ""))];
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

        // Billing summary: current cycle, next invoice estimate and balances.
        var periodStart = DateTime.Parse(store.UsagePeriodStart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var periodEnd = periodStart.AddMonths(1);
        var planCents = (long)(activePlan.MonthlyUsd * 100);
        var excessCents = (long)(store.Usage.OverrunEstimateUsd * 100);
        var open = store.Invoices.Where(i => i.Status is "open").ToList();
        var paid = store.Invoices.Where(i => i.Status is "paid").ToList();
        var summary = new UsageBillingSummary(
            PeriodLabel: $"{periodStart:MMM d} – {periodEnd:MMM d, yyyy}",
            PeriodDays: (int)(periodEnd - periodStart).TotalDays,
            PeriodElapsedDays: (int)Math.Clamp((now - periodStart).TotalDays, 0, (periodEnd - periodStart).TotalDays),
            NextInvoice: DashboardStore.FormatMoney(planCents + excessCents, "USD"),
            NextInvoiceDetail: $"{DashboardStore.FormatMoney(planCents, "USD")} plan + {DashboardStore.FormatMoney(excessCents, "USD")} usage",
            NextInvoiceDate: $"{periodEnd:MMM d, yyyy}",
            Outstanding: DashboardStore.FormatMoney(open.Sum(i => i.AmountCents), "USD"),
            OpenCount: open.Count,
            PaidTotal: DashboardStore.FormatMoney(paid.Sum(i => i.AmountCents), "USD"),
            PaidCount: paid.Count);

        var invoicePage = UsageInvoicePage.From(store.Invoices, status, issued, page, now);
        var spendCap = decimal.TryParse(store.SpendCapUsd, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var cap) && cap > 0 ? cap : (decimal?)null;

        return new(
            Tab: tab is "billing" or "settings" ? tab : "overview",
            ProjectFilter: projectFilter,
            DatabaseFilter: databaseFilter,
            RangeFilter: selectedRange,
            ProjectOptions: [.. store.Projects.Select(p => (p.Id, p.Name))],
            DatabaseOptions: [.. projectDatabases.Select(d => (d.Id, d.Name))],
            Stats: stats,
            Traffic: traffic,
            PlanResources: [.. store.OrganizationPlanResources()],
            ProjectContributions: [.. store.ProjectContributionRows()],
            HistoryRanges: historyRanges,
            RetentionWarning: retentionLimited,
            RetentionAlertBody:
                $"The requested period exceeds the {retentionDays}-day retention window. The accessible range starts {DashboardStore.FormatUtcDateTime(store.UsagePeriodStart)}.",
            HistoryTimes: historyTimes,
            History: history,
            HistorySummary: historySummary,
            Databases: databases,
            DatabaseCaps: [.. store.ProjectDatabases(store.ActiveProjectId).Select(d => (d.Id, d.Name, Cap: store.DatabaseSpendCap(d.Id)))],
            BillingPortalHref: billingPortalHref,
            PaidExcessEnabled: store.PaidExcessEnabled,
            SpendCap: store.SpendCapUsd ?? "",
            BillingStatus: store.BillingAccountStatus,
            BillingStatusTone: store.BillingAccountStatus.ToLowerInvariant() switch
            {
                "personal_free" => "success",
                "active" => "success",
                "trialing" => "info",
                "past_due" or "unpaid" => "warning",
                _ => "neutral",
            },
            PlanName: store.BillingPlanName,
            Price: DashboardStore.FormatMoney((long)(activePlan.MonthlyUsd * 100), "USD"),
            Frequency: "Monthly",
            Renewal: DashboardStore.FormatDate(store.BillingRenewal),
            Summary: summary,
            InvoicePage: invoicePage,
            BillingReturn: billing,
            HasManagedSubscription: store.HasManagedSubscription,
            PlanImpact: planImpact,
            BillingPartial: store.BillingPartial,
            BillingActionRequired: store.BillingActionRequired,
            StorageCoverageDeadline: store.StorageCoverageDeadline,
            SavedControlsMessage: saved switch
            {
                "ok" => "Spending controls saved.",
                "invalid" => "Enter a non-negative dollar amount with at most two decimal places.",
                _ => null,
            },
            RetentionDays: retentionDays,
            ExcessSpend: DashboardStore.FormatMoney(excessCents, "USD"),
            CapUsedPercent: spendCap is { } c ? (double)(store.Usage.OverrunEstimateUsd / c * 100) : null)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}

public sealed record UsageBillingSummary(
    string PeriodLabel,
    int PeriodDays,
    int PeriodElapsedDays,
    string NextInvoice,
    string NextInvoiceDetail,
    string NextInvoiceDate,
    string Outstanding,
    int OpenCount,
    string PaidTotal,
    int PaidCount);

/// <summary>One page of payment history after the status and issued-date filters.</summary>
public sealed record UsageInvoicePage(
    IReadOnlyList<DashboardStore.InvoiceRow> Rows,
    string Status,
    string Issued,
    int Page,
    int PageCount,
    int Total,
    int First)
{
    public const int PageSize = 10;

    public static readonly (string Id, string Name)[] StatusOptions = [("paid", "Paid"), ("open", "Open"), ("void", "Void")];

    public static (string Id, string Name)[] IssuedOptions(DateTime now) =>
        [("3m", "Last 3 months"), ("6m", "Last 6 months"), ("12m", "Last 12 months"), ($"{now.Year}", $"{now.Year}"), ($"{now.Year - 1}", $"{now.Year - 1}")];

    public static UsageInvoicePage From(IEnumerable<DashboardStore.InvoiceRow> invoices, string? status, string? issued, int page, DateTime now)
    {
        status = StatusOptions.Any(o => o.Id == status) ? status! : "";
        issued = IssuedOptions(now).Any(o => o.Id == issued) ? issued! : "";
        DateTime Issued(DashboardStore.InvoiceRow row) =>
            DateTime.Parse(row.IssuedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        bool InRange(DateTime at) => issued switch
        {
            "3m" => at >= now.AddMonths(-3),
            "6m" => at >= now.AddMonths(-6),
            "12m" => at >= now.AddMonths(-12),
            "" => true,
            var year => at.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) == year,
        };

        var rows = invoices
            .Where(i => status is "" || i.Status == status)
            .Where(i => InRange(Issued(i)))
            .OrderByDescending(Issued)
            .ToList();
        var pageCount = Math.Max(1, (rows.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 1, pageCount);
        var first = (page - 1) * PageSize;
        return new([.. rows.Skip(first).Take(PageSize)], status, issued, page, pageCount, rows.Count, first + 1);
    }

    public string Href(int page) =>
        $"/usage?tab=billing&status={Status}&issued={Issued}&page={page}";
}
