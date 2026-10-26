using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Usage;

public sealed record UsageToolAllowance(string Name, string Allowance, long Limit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Used, bool Measured,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Percent);
public sealed record UsageToolProject(string Id, string Name, string Region, int DatabaseCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? LogicalBytes, bool Unavailable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SharePercent, bool Selected);
public sealed record UsageToolDatabase(string Id, string Name, string Region, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Documents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? LogicalBytes, bool Unavailable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SharePercent, bool Metered,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Reads,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Writes);
public sealed record UsageToolTotals(bool Available, string Source, string Error, string Range,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Reads,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Writes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? LogicalBytes, bool StorageMeasured,
    IReadOnlyList<ActivityPoint> Series);
public sealed record UsageToolReport(string OrganizationId, string ProjectId, string DatabaseId,
    string PlanName, IReadOnlyList<UsageToolAllowance> Allowances, string AllowanceError, string PlanUsageNote,
    IReadOnlyList<UsageToolProject> Projects, IReadOnlyList<UsageToolDatabase> Databases, UsageToolTotals Totals,
    int ActiveDatabases,
    [property: Description("Distinct regions in explicit project database placement; null when serving placement is unknown, including all-regions mode."), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RegionCount, bool CanManage)
{
    public string RegionCountDescription { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InternalBill? InternalBill { get; init; }
}

[McpServerToolType]
public sealed class UsageMcpTools(IHttpContextAccessor http, AccountService accounts, SpendingService spending, IUsageMeasurements measurements, IUsageStorage documentStorage, TimeProvider time)
{
    [McpServerTool(Name = "get_usage", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read organization plan allowances and producer measurements for an authorized project/database. Missing measurements remain unavailable.")]
    public Task<UsageToolReport> Read(string databaseId = "",
        [Description("1h, 24h, 7d, or 30d. Defaults to 7d; other values are rejected.")] string range = "7d",
        string? organizationId = null, string? projectId = null) => ReportAsync(Scope(organizationId, projectId), databaseId, range);

    private async Task<UsageToolReport> ReportAsync(AccountContext context, string databaseId, string range)
    {
        context = await AccountScope.RefreshAsync(accounts, context, Cancellation);
        if (range is not ("1h" or "24h" or "7d" or "30d")) throw new AccountAccessException("Choose 1h, 24h, 7d or 30d.", 400);
        if (databaseId.Length > 0 && (context.Project is null || !context.Project.Databases.TryGetValue(databaseId, out var database) || database.ProjectId != context.Project.Id))
            throw new AccountAccessException("Database is unavailable.", 404);
        var selection = UsageSelection.For(context, context.Project?.Id ?? "", databaseId, range);
        var controls = await spending.ReadAsync(context, Cancellation);
        var measurementRead = measurements.ReadAsync(selection, Cancellation);
        var storageRead = documentStorage.ReadAsync(selection, Cancellation);
        await Task.WhenAll(measurementRead, storageRead);
        var measured = await measurementRead;
        var stored = await storageRead;
        stored.Validate(selection);
        var plan = PlanCatalog.For(context.Organization!);
        var limits = PlanCatalog.Limits(context.Organization!);
        var complete = measured.Scope.State != UsageSourceState.NoProducer && controls.Projects.All(project => measured.Monthly.GetValueOrDefault(project.Id) is { State: UsageSourceState.Measured, Reading: not null });
        long? reads = null, writes = null, storage = null;
        if (complete)
        {
            try
            {
                reads = 0; writes = 0; storage = 0;
                foreach (var project in controls.Projects)
                {
                    var reading = measured.Monthly[project.Id].Reading!;
                    if (reading.ProjectId != project.Id || reading.DatabaseId.Length > 0) throw new IOException("Monthly usage scope is invalid.");
                    reads = checked(reads + reading.Period.Reads); writes = checked(writes + reading.Period.Writes); storage = checked(storage + reading.LogicalBytes);
                }
            }
            catch (OverflowException) { reads = null; writes = null; storage = null; }
        }
        var scope = measured.Scope.State == UsageSourceState.Measured ? measured.Scope.Reading : null;
        if (scope is not null && (scope.ProjectId != context.Project?.Id || scope.DatabaseId != databaseId)) throw new IOException("Selected usage scope is invalid.");
        var selected = controls.Projects.SingleOrDefault(project => project.Id == context.Project?.Id);
        var projects = controls.Projects.Select(project =>
        {
            var reading = measured.Monthly.GetValueOrDefault(project.Id) is { State: UsageSourceState.Measured } observation ? observation.Reading : null;
            if (reading is not null && (reading.ProjectId != project.Id || reading.DatabaseId.Length > 0)) throw new IOException("Project usage scope is invalid.");
            var bytes = reading?.LogicalBytes;
            return new UsageToolProject(project.Id, project.Name, project.Region.Length > 0 ? project.Region : "Not configured",
                project.Databases.Count, bytes, bytes is null,
                Share(bytes, storage), project.Id == context.Project?.Id);
        }).ToArray();
        var listed = selected?.Databases.Where(database => databaseId.Length == 0 || database.Id == databaseId).ToArray() ?? [];
        var databaseReadings = listed.ToDictionary(database => database.Id,
            database => databaseId.Length > 0 ? scope : scope?.Items.SingleOrDefault(reading => reading.DatabaseId == database.Id), StringComparer.Ordinal);
        var databaseBytes = listed.ToDictionary(database => database.Id, database => databaseReadings[database.Id]?.LogicalBytes ??
            stored.Projects.GetValueOrDefault(context.Project!.Id)?.Databases.GetValueOrDefault(database.Id)?.LogicalBytes, StringComparer.Ordinal);
        var databaseTotal = UsageStorageSnapshot.SumCounters(databaseBytes.Values);
        var databases = listed.Select(database =>
        {
            var reading = databaseReadings[database.Id];
            var fact = stored.Projects.GetValueOrDefault(context.Project!.Id)?.Databases.GetValueOrDefault(database.Id);
            var bytes = databaseBytes[database.Id];
            return new UsageToolDatabase(database.Id, database.Name, database.Region,
                context.Project!.Databases[database.Id].Status, reading?.Documents ?? fact?.Documents, bytes, bytes is null,
                Share(bytes, databaseTotal), reading is not null, reading?.Period.Reads, reading?.Period.Writes);
        }).ToArray();
        var error = measured.Scope.State switch
        {
            UsageSourceState.Unauthorized => "TerraBase didn't accept this sign-in. Sign out and in again to load metered reads and writes.",
            UsageSourceState.Unavailable => "Metered reads and writes could not be loaded. Try again shortly.",
            _ => ""
        };
        var source = measured.Scope.State switch
        {
            UsageSourceState.Measured => "Best-effort metering from TerraBase; not billing data. Counts since the last save can be lost on a crash.",
            UsageSourceState.NoProducer => "This deployment has no TerraBase, which meters reads and writes.",
            UsageSourceState.NotMetered => "TerraBase does not report metering for this " + (databaseId.Length > 0 ? "database" : "project") + ".",
            UsageSourceState.Restricted => "The usage resource was not found or is not authorized for this account.",
            UsageSourceState.QueryTooExpensive => "TerraBase could not complete this usage query within its resource limits; no zero usage is inferred.",
            UsageSourceState.Unauthorized => error,
            _ => "TerraBase metering is unavailable."
        };
        var allowanceError = complete && reads is not null || measured.Scope.State == UsageSourceState.NoProducer ? "" : measured.Monthly.Values.Any(value => value.State == UsageSourceState.Unauthorized)
            ? "TerraBase didn't accept this sign-in, so this month's reads and writes didn't load. Sign out and in again."
            : measured.Monthly.Values.Any(value => value.State == UsageSourceState.Unavailable) || complete
                ? "This month's reads and writes could not be loaded from TerraBase. Try again shortly." : "";
        var scopeBytes = scope?.LogicalBytes ?? (databaseId.Length > 0 ? stored.Scope(selection)?.LogicalBytes : null);
        return new(context.Organization!.Id, context.Project?.Id ?? "", databaseId, plan?.Name ?? "No billing plan",
            [Allowance("Operation units this month", plan?.OperationsAllowance, limits.MonthlyOperations,
                complete && reads is not null ? measured.OperationUnits(controls.Projects.Select(project => project.Id), time.GetUtcNow()) : null),
             Allowance("Storage across regions", plan?.StorageAllowance, limits.StorageBytes, null),
             Allowance("Client egress this month", plan?.EgressAllowance, limits.MonthlyEgressBytes, null)], allowanceError,
            "Published allowances count 4 KiB operation units and storage across selected regions, and client egress. These document counters do not measure allowance utilization.",
            projects, databases,
            new(scope is not null, source, error, range, scope?.Period.Reads, scope?.Period.Writes, scopeBytes, scopeBytes is not null, scope?.Series ?? []),
            context.Project?.Databases.Count ?? 0,
            UsagePlacement.For(context.Project).RegionCount,
            context.CanManage)
        {
            RegionCountDescription = UsagePlacement.For(context.Project).Label,
            InternalBill = context.Organization.InternalBilling is not null
                ? InternalBillingService.Estimate(context.Organization, measured, time.GetUtcNow()) : null
        };
    }

    private static UsageToolAllowance Allowance(string name, string? label, long limit, long? used) => new(name, label ?? "Limit not published", limit, used, used is not null,
        used is not null && limit > 0 ? (int)Math.Min(int.MaxValue, Math.Round(used.Value / (double)limit * 100, MidpointRounding.AwayFromZero)) : null);
    private static int? Share(long? part, long? total) => part is null || total is null ? null : part <= 0 || total <= 0 ? 0
        : Math.Max(1, (int)Math.Round(part.Value / (double)total.Value * 100, MidpointRounding.AwayFromZero));
    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}
