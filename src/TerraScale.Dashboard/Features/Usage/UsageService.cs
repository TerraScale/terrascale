using System.Globalization;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Usage;

public sealed record UsageDatabaseRow(SpendingDatabase Spending, string Status, UsageReading? Reading, int SharePercent, UsageStorageFact? Storage = null)
{
    public long? LogicalBytes => Reading?.LogicalBytes ?? Storage?.LogicalBytes;
    public long? Documents => Reading?.Documents ?? Storage?.Documents;
}
public sealed record UsageProjectRow(SpendingProject Spending, UsageReading? Reading, bool Selected, int SharePercent, UsageStorageFact? Storage = null)
{
    public long? LogicalBytes => Reading?.LogicalBytes ?? Storage?.LogicalBytes;
}
public sealed record UsageAllowance(string Name, string Allowance, string Used, int? Percent, string PlanName)
{
    public string Tone => Percent >= 100 ? "danger" : Percent >= 80 ? "warning" : "success";
    public string Label => $"{Name}: {Percent}% of the {PlanName} plan's allowance";
}
public sealed record UsagePage(ShellModel Shell, UsageSelection Selection, string Tab, string Saved, SpendingSnapshot Spending,
    IReadOnlyList<UsageProjectRow> Projects, IReadOnlyList<UsageDatabaseRow> Databases, IReadOnlyList<UsageAllowance> Allowances,
    string PlanName, UsageReading? Scope, AnalyticsLinePlot? History, string UsageSource, string UsageError, string AllowanceError, string PlanUsageNote, bool OrganizationStorageMeasured, UsageReplication Replication)
{
    public UsageStorageFact? StoredScope { get; init; }
    public InternalBill? InternalBill { get; init; }
    public long? ScopeLogicalBytes => Scope?.LogicalBytes ?? StoredScope?.LogicalBytes;
    public bool ScopeStorageMeasured => ScopeLogicalBytes is not null;
    public int ActiveDatabases => Selection.Context.Project?.Databases.Count ?? 0;
    public int RegionCount => Selection.Context.Project?.Databases.Values.Select(database => database.Region).Where(region => region.Length > 0).Distinct().Count() ?? 0;
    public string HistoryDescription => "The selected " + (Selection.DatabaseId.Length > 0 ? "database" : "project") + ", " + char.ToLowerInvariant(Selection.RangeLabel[0]) + Selection.RangeLabel[1..];
    public string ProjectHref(string projectId, string tab = "overview") => "/usage?tab=" + tab + "&project=" + Uri.EscapeDataString(projectId);
}

public sealed class UsageService(IUsageMeasurements measurements, SpendingService spending, IUsageReplication replication, IUsageStorage storage, TimeProvider time)
{
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
                    if (reading.ProjectId != project.Id || reading.DatabaseId.Length > 0) throw new IOException("Monthly usage scope is invalid.");
                    reads = checked(reads + reading.Period.Reads); writes = checked(writes + reading.Period.Writes); logical = checked(logical + reading.LogicalBytes);
                }
            }
            catch (Exception error) when (error is IOException or OverflowException) { complete = false; monthError = "This month's reads and writes could not be loaded from TerraBase. Try again shortly."; }
        }
        if (!complete && monthError.Length == 0 && result.Scope.State != UsageSourceState.NoProducer)
            monthError = result.Monthly.Values.Any(value => value.State == UsageSourceState.Unauthorized)
                ? "TerraBase didn't accept this sign-in, so this month's reads and writes didn't load. Sign out and in again."
                : result.Monthly.Values.Any(value => value.State == UsageSourceState.NotMetered)
                    ? "This month's organization usage is incomplete because TerraBase doesn't report every project's monthly usage."
                    : "This month's reads and writes could not be loaded from TerraBase. Try again shortly.";
        var organizationStorage = complete ? logical : stored?.OrganizationBytes;
        var allowances = new[] {
            Allowance("Reads this month", plan?.ReadsAllowance ?? "Limit not published", limits.MonthlyReads, reads, false, complete),
            Allowance("Writes this month", plan?.WritesAllowance ?? "Limit not published", limits.MonthlyWrites, writes, false, complete),
            Allowance("Storage", plan?.StorageAllowance ?? "Limit not published", limits.StorageBytes, organizationStorage ?? 0, true, organizationStorage is not null)
        };
        var scope = result.Scope.State == UsageSourceState.Measured ? result.Scope.Reading : null;
        if (scope is not null && (scope.ProjectId != selection.Context.Project?.Id || scope.DatabaseId != selection.DatabaseId)) throw new IOException("Selected usage scope is invalid.");
        var scopeName = selection.DatabaseId.Length > 0 ? "database" : "project";
        var source = result.Scope.State switch
        {
            UsageSourceState.Measured => "Metered by TerraBase.",
            UsageSourceState.NoProducer => "This deployment has no TerraBase, which meters reads and writes.",
            UsageSourceState.NotMetered => "TerraBase doesn't report this " + scopeName + ": it isn't stored there yet, or this account isn't a member of it there.",
            UsageSourceState.Unauthorized => "TerraBase didn't accept this sign-in. Sign out and in again to load metered reads and writes.",
            _ => "TerraBase metering is unavailable."
        };
        var projects = controls.Projects.Select(project =>
        {
            var reading = result.Monthly.GetValueOrDefault(project.Id) is { State: UsageSourceState.Measured } observation ? observation.Reading : null;
            var fact = stored?.Projects.GetValueOrDefault(project.Id)?.Total;
            var bytes = reading?.LogicalBytes ?? fact?.LogicalBytes;
            return new UsageProjectRow(project, reading, project.Id == selection.Context.Project?.Id,
                bytes is not null && organizationStorage is not null ? Share(bytes.Value, organizationStorage.Value) : 0, fact);
        }).ToArray();
        var scopedDatabases = scope?.Items ?? [];
        var selectedProject = controls.Projects.FirstOrDefault(project => project.Id == selection.Context.Project?.Id);
        var listed = selectedProject?.Databases.Where(database => selection.DatabaseId.Length == 0 || database.Id == selection.DatabaseId).ToArray() ?? [];
        var databaseRows = listed.Select(database => new UsageDatabaseRow(database, selection.Context.Project!.Databases[database.Id].Status,
            selection.DatabaseId.Length > 0 ? scope : scopedDatabases.FirstOrDefault(reading => reading.DatabaseId == database.Id), 0,
            stored?.Projects.GetValueOrDefault(selection.Context.Project!.Id)?.Databases.GetValueOrDefault(database.Id))).ToArray();
        var knownDatabaseTotal = UsageStorageSnapshot.SumCounters(databaseRows.Select(database => database.LogicalBytes));
        databaseRows = databaseRows.Select(database => database with { SharePercent = database.LogicalBytes is { } bytes && knownDatabaseTotal is { } total ? Share(bytes, total) : 0 }).ToArray();
        var history = scope is not null ? AnalyticsLinePlot.For("Reads and writes, " + selection.RangeLabel, selection.Range, scope.Series.Select(point => point.Start).ToArray(),
            [new("Reads", scope.Series.Select(point => point.Reads).ToArray()), new("Writes", scope.Series.Select(point => point.Writes).ToArray())]) : null;
        return new(shell, selection, tab, saved, controls, projects, databaseRows, allowances, name, scope, history, source,
            result.Scope.State == UsageSourceState.Unavailable ? "Metered reads and writes could not be loaded. Try again shortly." : "", monthError,
            !complete && monthError.Length == 0 ? "This month's reads and writes aren't metered here, so their utilization is unavailable."
                : "Reads and writes count separately: each read is one read and each write one write. The month runs on UTC.", organizationStorage is not null, await replicationRead)
        {
            StoredScope = stored?.Scope(selection),
            InternalBill = selection.Context.Organization!.InternalBilling is not null
            ? InternalBillingService.Estimate(selection.Context.Organization, result, time.GetUtcNow()) : null
        };

        UsageAllowance Allowance(string title, string allowance, long limit, long used, bool bytes, bool measured) => new(title, allowance,
            measured ? bytes ? UsageFormat.Bytes(used) : used.ToString("N0", CultureInfo.InvariantCulture) : "Not metered",
            measured && limit > 0 ? (int)Math.Min(int.MaxValue, Math.Round(used / (double)limit * 100, MidpointRounding.AwayFromZero)) : null, name);
        async Task<UsageStorageSnapshot?> StoredAsync() => tab == "overview" ? await storage.ReadAsync(selection, cancellationToken) : null;
    }
    private static int Share(long part, long total) => part <= 0 || total <= 0 ? 0 : Math.Max(1, (int)Math.Round(part / (double)total * 100, MidpointRounding.AwayFromZero));
}
