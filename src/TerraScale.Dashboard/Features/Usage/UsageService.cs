using System.Globalization;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Common.Charts;
using TerraScale.Dashboard.Common.Formatting;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Usage
{
    public sealed record UsageDatabaseRow(SpendingDatabase Spending, string Status, UsageReading? Reading, double? SharePercent, UsageStorageFact? Storage = null)
    {
        public long? LogicalBytes => Reading?.LogicalBytes ?? Storage?.LogicalBytes;
        public long? Documents => Reading?.Documents ?? Storage?.Documents;
    }
    public sealed record UsageProjectRow(SpendingProject Spending, UsageReading? Reading, bool Selected, double? SharePercent, UsageStorageFact? Storage = null)
    {
        public long? LogicalBytes => Reading?.LogicalBytes ?? Storage?.LogicalBytes;
    }
    public sealed record UsageAllowance(string Name, string Allowance, string Used, double? Percent, string PlanName, string? ExactUsed = null, string? ExactAllowance = null)
    {
        public string Tone => Percent >= 100 ? "danger" : Percent >= 80 ? "warning" : "success";
        public string Label => $"{Name}: {ValueFormat.PercentLabel(Percent ?? 0)} of the {PlanName} plan's allowance";
    }
    public sealed record UsagePage(ShellModel Shell, UsageSelection Selection, string Tab, string Saved, SpendingSnapshot Spending,
        IReadOnlyList<UsageProjectRow> Projects, IReadOnlyList<UsageDatabaseRow> Databases, IReadOnlyList<UsageAllowance> Allowances,
        string PlanName, UsageReading? Scope, AnalyticsLinePlot? History, string UsageSource, string UsageError, string AllowanceError, string PlanUsageNote, bool OrganizationStorageMeasured, UsageReplication Replication)
    {
        public UsageStorageFact? StoredScope { get; init; }
        // Set when a plan without overage billing has used its monthly operation units: TerraBase refuses new writes.
        public string WritesPaused { get; init; } = "";
        public InternalBill? InternalBill { get; init; }
        public long? ScopeLogicalBytes => Scope?.LogicalBytes ?? StoredScope?.LogicalBytes;
        public bool ScopeStorageMeasured => ScopeLogicalBytes is not null;
        public int ActiveDatabases => Selection.Context.Project?.Databases.Count ?? 0;
        public int? RegionCount => UsagePlacement.For(Selection.Context.Project).RegionCount;
        public string RegionCountLabel => UsagePlacement.For(Selection.Context.Project).Label;
        public string HistoryDescription => "The selected " + (Selection.DatabaseId.Length > 0 ? "database" : "project") + ", " + char.ToLowerInvariant(Selection.RangeLabel[0]) + Selection.RangeLabel[1..];
        public string ProjectHref(string projectId, string tab = "overview")
        {
            return "/usage?tab=" + tab + "&project=" + Uri.EscapeDataString(projectId) +
            "&range=" + Uri.EscapeDataString(Selection.Range) +
            (projectId == Selection.Context.Project?.Id && Selection.DatabaseId.Length > 0 ? "&database=" + Uri.EscapeDataString(Selection.DatabaseId) : "");
        }
    }

    public sealed class UsageService(IUsageMeasurements measurements, SpendingService spending, IUsageReplication replication, IUsageStorage storage, TimeProvider time)
    {
        // A plan without overage billing stops new writes at its monthly operation units (TerraBase answers operation_allowance).
        public static string WritesPaused(BillingPlan? plan, long monthlyOperations, long? units, DateTimeOffset now)
        {
            if (plan is not { PriceCents: 0 } || monthlyOperations is <= 0 or long.MaxValue || units is not { } used || used < monthlyOperations)
            {
                return "";
            }

            var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            return string.Create(CultureInfo.InvariantCulture, $"Writes are paused: this organization used all {monthlyOperations:N0} operation units of the {plan.Name} plan for {month:MMMM yyyy}. Reads and deletes keep working, and writes resume on {month.AddMonths(1):MMMM d} (UTC).");
        }

        public async Task<UsagePage> LoadAsync(ShellModel shell, UsageSelection selection, string tab, string saved, CancellationToken cancellationToken)
        {
            var controls = await spending.ReadAsync(selection.Context, cancellationToken);
            var usageRead = tab == "overview" || selection.Context.Organization!.InternalBilling is not null ? measurements.ReadAsync(selection, cancellationToken) : Task.FromResult(new UsageMeasurements(new(UsageSourceState.NotMetered), new Dictionary<string, UsageObservation>()));
            var replicationRead = tab == "overview" ? replication.ReadAsync(selection, cancellationToken) : Task.FromResult(new UsageReplication([], []));
            var storageRead = StoredAsync();
            await Task.WhenAll(usageRead, replicationRead, storageRead);
            var result = await usageRead;
            var stored = await storageRead;
            stored?.Validate(selection);
            var plan = PlanCatalog.For(selection.Context.Organization!);
            var limits = PlanCatalog.Limits(selection.Context.Organization!);
            var name = plan?.Name ?? "No billing plan";
            var complete = result.Scope.State != UsageSourceState.NoProducer && controls.Projects.All(project => result.Monthly.GetValueOrDefault(project.Id) is { State: UsageSourceState.Measured, Reading: not null });
            long reads = 0, writes = 0, logical = 0;
            var monthError = "";
            if (complete)
            {
                try
                {
                    foreach (var project in controls.Projects)
                    {
                        var reading = result.Monthly[project.Id].Reading!;
                        if (reading.ProjectId != project.Id || reading.DatabaseId.Length > 0)
                        {
                            throw new IOException("Monthly usage scope is invalid.");
                        }

                        reads = checked(reads + reading.Period.Reads); writes = checked(writes + reading.Period.Writes); logical = checked(logical + reading.LogicalBytes);
                    }
                }
                catch (Exception error) when (error is IOException or OverflowException) { complete = false; monthError = "This month's reads and writes could not be loaded from TerraBase. Try again shortly."; }
            }
            if (!complete && monthError.Length == 0 && result.Scope.State != UsageSourceState.NoProducer)
            {
                monthError = result.Monthly.Values.Any(value => value.State == UsageSourceState.Unauthorized)
                    ? "TerraBase didn't accept this sign-in, so this month's reads and writes didn't load. Sign out and in again."
                    : result.Monthly.Values.Any(value => value.State == UsageSourceState.NotMetered)
                        ? "This month's organization usage is incomplete because TerraBase doesn't report every project's monthly usage."
                        : "This month's reads and writes could not be loaded from TerraBase. Try again shortly.";
            }

            long? organizationStorage = complete ? logical : null; // Collection fallback excludes files.
            var units = complete ? result.OperationUnits(controls.Projects.Select(project => project.Id), time.GetUtcNow()) : null;
            var allowances = new[] {
                Allowance("Operation units this month", plan?.OperationsAllowance ?? "Limit not published", limits.MonthlyOperations, units ?? 0, false, units is not null),
                Allowance("Storage across regions", plan?.StorageAllowance ?? "Limit not published", limits.StorageBytes, 0, true, false),
                Allowance("Client egress this month", plan?.EgressAllowance ?? "Limit not published", limits.MonthlyEgressBytes, 0, true, false)
            };
            var writesPaused = WritesPaused(plan, limits.MonthlyOperations, units, time.GetUtcNow());
            var scope = result.Scope.State == UsageSourceState.Measured ? result.Scope.Reading : null;
            if (scope is not null && (scope.ProjectId != selection.Context.Project?.Id || scope.DatabaseId != selection.DatabaseId))
            {
                throw new IOException("Selected usage scope is invalid.");
            }

            var scopeName = selection.DatabaseId.Length > 0 ? "database" : "project";
            var source = result.Scope.State switch
            {
                UsageSourceState.Measured => "Best-effort metering from TerraBase; not billing data. Counts since the last save can be lost on a crash.",
                UsageSourceState.NoProducer => "This deployment has no TerraBase, which meters reads and writes.",
                UsageSourceState.NotMetered => "TerraBase does not report metering for this " + scopeName + ".",
                UsageSourceState.Restricted => "The usage resource was not found or is not authorized for this account.",
                UsageSourceState.QueryTooExpensive => "TerraBase could not complete this usage query within its resource limits; no zero usage is inferred.",
                UsageSourceState.Unauthorized => "TerraBase didn't accept this sign-in. Sign out and in again to load metered reads and writes.",
                _ => "TerraBase metering is unavailable."
            };
            var projects = controls.Projects.Select(project =>
            {
                var reading = result.Monthly.GetValueOrDefault(project.Id) is { State: UsageSourceState.Measured } observation ? observation.Reading : null;
                var bytes = reading?.LogicalBytes;
                return new UsageProjectRow(project, reading, project.Id == selection.Context.Project?.Id,
                    bytes is not null && organizationStorage is not null ? ValueFormat.Percent(bytes, organizationStorage) : null);
            }).ToArray();
            var scopedDatabases = scope?.Items ?? [];
            var selectedProject = controls.Projects.FirstOrDefault(project => project.Id == selection.Context.Project?.Id);
            var listed = selectedProject?.Databases.Where(database => selection.DatabaseId.Length == 0 || database.Id == selection.DatabaseId).ToArray() ?? [];
            var databaseRows = listed.Select(database => new UsageDatabaseRow(database, selection.Context.Project!.Databases[database.Id].Status,
                selection.DatabaseId.Length > 0 ? scope : scopedDatabases.FirstOrDefault(reading => reading.DatabaseId == database.Id), 0,
                stored?.Projects.GetValueOrDefault(selection.Context.Project!.Id)?.Databases.GetValueOrDefault(database.Id))).ToArray();
            var knownDatabaseTotal = UsageStorageSnapshot.SumCounters(databaseRows.Select(database => database.LogicalBytes));
            databaseRows = [.. databaseRows.Select(database => database with { SharePercent = database.LogicalBytes is { } bytes && knownDatabaseTotal is { } total ? ValueFormat.Percent(bytes, total) : null })];
            var history = scope is { Series.Count: > 0 } ? AnalyticsLinePlot.For("Reads and writes, " + selection.RangeLabel, selection.Range, [.. scope.Series.Select(point => point.Start)],
                [new("Reads", [.. scope.Series.Select(point => (decimal)point.Reads)]), new("Writes", [.. scope.Series.Select(point => (decimal)point.Writes)])]) : null;
            return new(shell, selection, tab, saved, controls, projects, databaseRows, allowances, name, scope, history, source,
                result.Scope.State == UsageSourceState.Unavailable ? "Metered reads and writes could not be loaded. Try again shortly." : "", monthError,
                "Published allowances count 4 KiB operation units and storage across selected regions, and client egress. These document counters do not measure allowance utilization.", organizationStorage is not null, await replicationRead)
            {
                StoredScope = selection.DatabaseId.Length > 0 ? stored?.Scope(selection) : null,
                WritesPaused = writesPaused,
                InternalBill = selection.Context.Organization!.InternalBilling is not null
                ? InternalBillingService.Estimate(selection.Context.Organization, result, time.GetUtcNow()) : null
            };

            UsageAllowance Allowance(string title, string allowance, long limit, long used, bool bytes, bool measured) => new(title, !bytes && limit > 0 ? ChartGeometry.Compact(limit) + " operation units/month" : allowance,
                measured ? bytes ? MetricFormat.Bytes(used) : ChartGeometry.Compact(used) : "Not metered",
                measured && limit > 0 ? ValueFormat.Percent(used, limit) : null, name, measured && !bytes ? used.ToString("N0", CultureInfo.InvariantCulture) : null, !bytes && limit > 0 ? limit.ToString("N0", CultureInfo.InvariantCulture) + " operation units/month" : null);
            async Task<UsageStorageSnapshot?> StoredAsync() => tab == "overview" ? await storage.ReadAsync(selection, cancellationToken) : null;
        }
    }
}
