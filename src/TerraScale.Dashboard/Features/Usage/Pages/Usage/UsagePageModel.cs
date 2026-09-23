using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Usage.Pages.Usage;

public sealed record UsagePageModel(
    IReadOnlyList<UsageStatCard> Stats,
    IReadOnlyList<DashboardStore.PlanResourceRow> PlanResources,
    IReadOnlyList<DashboardStore.ProjectContributionRow> ProjectContributions,
    IReadOnlyList<UsageHistoryRangeOption> HistoryRanges,
    bool RetentionWarning,
    IReadOnlyList<WorkloadPoint> History,
    IReadOnlyList<UsageStatCard> HistorySummary,
    IReadOnlyList<DashboardDatabase> Databases,
    IReadOnlyList<(string Id, string Name, string? Cap)> DatabaseCaps,
    IReadOnlyList<DashboardStore.CheckoutPlanRow> CheckoutPlans,
    bool StripeCheckoutEnabled,
    string BillingPortalHref,
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
    bool BillingPartial,
    bool BillingActionRequired,
    string? StorageCoverageDeadline,
    string RetentionAlertBody,
    string? SavedControlsMessage) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static UsagePageModel From(DashboardStore store, StripePaymentOptions stripe, string href, string? range, string? billing, string? saved)
    {
        var databases = store.ProjectDatabases(store.ActiveProjectId);

        // With the Stripe sandbox configured the usage page offers exactly the
        // plans that have a sandbox price; otherwise it keeps the demo catalog.
        IReadOnlyList<DashboardStore.CheckoutPlanRow> checkoutPlans = stripe.Enabled
            ? [.. store.CheckoutPlans
                .Where(plan => stripe.PriceId(plan.PlanKey, "monthly") is not null)
                .Select(plan => plan with
                {
                    Href = $"/billing/checkout?plan={plan.PlanKey}&interval=monthly&return={Uri.EscapeDataString("/usage")}",
                })]
            : [.. store.CheckoutPlans];
        var billingPortalHref = stripe.Enabled
            ? $"/billing/portal?return={Uri.EscapeDataString("/usage")}"
            : "/billing/portal/usage_portal";

        // Old page falls back to database/region counters when the summary has no KPIs.
        var regionCount = databases
            .SelectMany(d => new[] { d.PrimaryRegion }.Concat(d.BackupRegion is null ? [] : [d.BackupRegion]))
            .Distinct()
            .Count();
        List<UsageStatCard> stats =
        [
            new("Databases", DashboardStore.FormatNumber(databases.Length), "Saved project databases", "Database"),
            new("Regions", DashboardStore.FormatNumber(regionCount), "Distinct placement regions", "Globe2"),
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
        var rawHistory = store.Workloads
            .Where(w => w.Range == selectedRange)
            .SelectMany(w => w.Points)
            .ToList();

        // Seeded points are ordinal; spread them evenly over the window ending now.
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromDays(RangeDays(selectedRange));
        var timeFormat = selectedRange is "24h" ? "HH:mm" : "MMM d";
        List<WorkloadPoint> history = [.. rawHistory.Select((p, i) => p with
        {
            Label = (now - window * (rawHistory.Count - 1 - i) / Math.Max(1, rawHistory.Count - 1))
                .ToString(timeFormat, System.Globalization.CultureInfo.InvariantCulture),
        })];
        List<UsageStatCard> historySummary = history.Count is 0 ? [] :
        [
            new("Peak", DashboardStore.FormatNumber((long)history.Max(p => p.Value)), "", ""),
            new("Average", DashboardStore.FormatNumber((long)history.Average(p => p.Value)), "", ""),
            new("Latest", DashboardStore.FormatNumber((long)history[^1].Value), "", ""),
        ];
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

        return new(
            Stats: stats,
            PlanResources: [.. store.OrganizationPlanResources()],
            ProjectContributions: [.. store.ProjectContributionRows()],
            HistoryRanges: historyRanges,
            RetentionWarning: retentionLimited,
            RetentionAlertBody:
                $"The requested period exceeds the {retentionDays}-day retention window. The accessible range starts {DashboardStore.FormatUtcDateTime(store.UsagePeriodStart)}.",
            History: history,
            HistorySummary: historySummary,
            Databases: databases,
            DatabaseCaps: [.. databases.Select(d => (d.Id, d.Name, Cap: store.DatabaseSpendCap(d.Id)))],
            CheckoutPlans: checkoutPlans,
            StripeCheckoutEnabled: stripe.Enabled,
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
            InvoiceCount: store.TotalInvoices,
            Invoices: [.. store.Invoices],
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
            })
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
