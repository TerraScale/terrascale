using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Usage;

public sealed record SpendingCapChange(string Id, long? CapCents = null, long? ReadLimit = null, long? WriteLimit = null, long? ReplicationLimit = null);
public sealed record UsageToolAllowance(string Name, string Allowance, long Limit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Used, bool Measured,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Percent);
public sealed record UsageToolProject(OperationLimits OperationLimits, string Id, string Name, string Region, int DatabaseCount,
    long SpendCapCents, long EffectiveCapCents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? LogicalBytes, bool Unavailable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SharePercent, bool Selected);
public sealed record UsageToolDatabase(OperationLimits OperationLimits, string Id, string Name, string Region, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Documents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? LogicalBytes, bool Unavailable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SharePercent, bool Metered,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Reads,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Writes, long SpendCapCents, long EffectiveCapCents);
public sealed record UsageToolTotals(bool Available, string Source, string Error, string Range,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Reads,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Writes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? LogicalBytes, bool StorageMeasured,
    IReadOnlyList<ActivityPoint> Series);
public sealed record UsageToolReport(string OrganizationId, string ProjectId, string DatabaseId,
    string PlanName, IReadOnlyList<UsageToolAllowance> Allowances, string AllowanceError, string PlanUsageNote,
    long SpendCapCents, bool PaidExcessEnabled, long ProjectEffectiveCapCents,
    IReadOnlyList<UsageToolProject> Projects, IReadOnlyList<UsageToolDatabase> Databases, UsageToolTotals Totals,
    int ActiveDatabases, int RegionCount, bool CanManage)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InternalBill? InternalBill { get; init; }
}

[McpServerToolType]
public sealed class UsageMcpTools(IHttpContextAccessor http, AccountService accounts, SpendingService spending, IUsageMeasurements measurements, IUsageStorage documentStorage, TimeProvider time)
{
    [McpServerTool(Name = "get_usage", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read organization plan allowances, persisted spending controls and producer measurements for an authorized project/database. Missing measurements remain unavailable.")]
    public Task<UsageToolReport> Read(string databaseId = "", string range = "7d", string? organizationId = null, string? projectId = null) => ReportAsync(Scope(organizationId, projectId), databaseId, range);

    [McpServerTool(Name = "set_spending_controls", UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Set monthly spending caps in US cents and read/write/replication limits. Zero removes a limit; omitted fields remain unchanged. Requires an owner or admin role.")]
    public async Task<UsageToolReport> Set(long? organizationCapCents = null, bool? paidExcess = null,
        SpendingCapChange[]? projectCaps = null, SpendingCapChange[]? databaseCaps = null,
        string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        var projects = Changes(projectCaps, "project");
        var databases = Changes(databaseCaps, "database");
        await spending.SetAsync(context, new(organizationCapCents, paidExcess,
            Caps(projects), Caps(databases), Operations(projects), Operations(databases)), Cancellation);
        return await ReportAsync(context, "", "7d");
    }

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
        storage ??= stored.OrganizationBytes;
        var scope = measured.Scope.State == UsageSourceState.Measured ? measured.Scope.Reading : null;
        if (scope is not null && (scope.ProjectId != context.Project?.Id || scope.DatabaseId != databaseId)) throw new IOException("Selected usage scope is invalid.");
        var selected = controls.Projects.SingleOrDefault(project => project.Id == context.Project?.Id);
        var projects = controls.Projects.Select(project =>
        {
            var reading = measured.Monthly.GetValueOrDefault(project.Id) is { State: UsageSourceState.Measured } observation ? observation.Reading : null;
            if (reading is not null && (reading.ProjectId != project.Id || reading.DatabaseId.Length > 0)) throw new IOException("Project usage scope is invalid.");
            var bytes = reading?.LogicalBytes ?? stored.Projects.GetValueOrDefault(project.Id)?.Total?.LogicalBytes;
            return new UsageToolProject(project.Operations, project.Id, project.Name, project.Region.Length > 0 ? project.Region : "Not configured",
                project.Databases.Count, project.CapCents, project.EffectiveCapCents, bytes, bytes is null,
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
            return new UsageToolDatabase(database.Operations, database.Id, database.Name, database.Region,
                context.Project!.Databases[database.Id].Status, reading?.Documents ?? fact?.Documents, bytes, bytes is null,
                Share(bytes, databaseTotal), reading is not null, reading?.Period.Reads, reading?.Period.Writes,
                database.CapCents, database.EffectiveCapCents);
        }).ToArray();
        var error = measured.Scope.State switch
        {
            UsageSourceState.Unauthorized => "TerraBase didn't accept this sign-in. Sign out and in again to load metered reads and writes.",
            UsageSourceState.Unavailable => "Metered reads and writes could not be loaded. Try again shortly.",
            _ => ""
        };
        var source = measured.Scope.State switch
        {
            UsageSourceState.Measured => "Metered by TerraBase.",
            UsageSourceState.NoProducer => "This deployment has no TerraBase, which meters reads and writes.",
            UsageSourceState.NotMetered => "TerraBase doesn't report this " + (databaseId.Length > 0 ? "database" : "project") + ": it isn't stored there yet, or this account isn't a member of it there.",
            UsageSourceState.Unauthorized => error,
            _ => "TerraBase metering is unavailable."
        };
        var allowanceError = complete && reads is not null || measured.Scope.State == UsageSourceState.NoProducer ? "" : measured.Monthly.Values.Any(value => value.State == UsageSourceState.Unauthorized)
            ? "TerraBase didn't accept this sign-in, so this month's reads and writes didn't load. Sign out and in again."
            : measured.Monthly.Values.Any(value => value.State == UsageSourceState.Unavailable) || complete
                ? "This month's reads and writes could not be loaded from TerraBase. Try again shortly." : "";
        var scopeBytes = scope?.LogicalBytes ?? stored.Scope(selection)?.LogicalBytes;
        return new(context.Organization!.Id, context.Project?.Id ?? "", databaseId, plan?.Name ?? "No billing plan",
            [Allowance("Reads this month", plan?.ReadsAllowance, limits.MonthlyReads, reads),
             Allowance("Writes this month", plan?.WritesAllowance, limits.MonthlyWrites, writes),
             Allowance("Storage", plan?.StorageAllowance, limits.StorageBytes, storage)], allowanceError,
            reads is null ? "This month's reads and writes aren't metered here, so their utilization is unavailable."
                : "Reads and writes count separately: each read is one read and each write one write. The month runs on UTC.",
            controls.OrganizationCapCents, controls.PaidExcess, selected?.EffectiveCapCents ?? controls.OrganizationCapCents, projects, databases,
            new(scope is not null, source, error, range, scope?.Period.Reads, scope?.Period.Writes, scopeBytes, scopeBytes is not null, scope?.Series ?? []),
            context.Project?.Databases.Count ?? 0,
            context.Project?.Databases.Values.Select(database => database.Region).Where(region => region.Length > 0).Distinct(StringComparer.Ordinal).Count() ?? 0,
            context.CanManage)
        {
            InternalBill = context.Organization.InternalBilling is not null
                ? InternalBillingService.Estimate(context.Organization, measured, time.GetUtcNow()) : null
        };
    }

    private static UsageToolAllowance Allowance(string name, string? label, long limit, long? used) => new(name, label ?? "Limit not published", limit, used, used is not null,
        used is not null && limit > 0 ? (int)Math.Min(int.MaxValue, Math.Round(used.Value / (double)limit * 100, MidpointRounding.AwayFromZero)) : null);
    private static int? Share(long? part, long? total) => part is null || total is null ? null : part <= 0 || total <= 0 ? 0
        : Math.Max(1, (int)Math.Round(part.Value / (double)total.Value * 100, MidpointRounding.AwayFromZero));
    private static IReadOnlyList<SpendingCapChange> Changes(SpendingCapChange[]? values, string kind)
    {
        values ??= [];
        if (values.Any(value => string.IsNullOrWhiteSpace(value.Id)) || values.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new AccountAccessException("Supply distinct " + kind + " IDs.", 400);
        return values;
    }
    private static Dictionary<string, long> Caps(IReadOnlyList<SpendingCapChange> changes) => changes.Where(value => value.CapCents.HasValue).ToDictionary(value => value.Id, value => value.CapCents!.Value, StringComparer.Ordinal);
    private static Dictionary<string, OperationLimitChange> Operations(IReadOnlyList<SpendingCapChange> changes) => changes
        .Where(value => value.ReadLimit is not null || value.WriteLimit is not null || value.ReplicationLimit is not null)
        .ToDictionary(value => value.Id, value => new OperationLimitChange(value.ReadLimit, value.WriteLimit, value.ReplicationLimit), StringComparer.Ordinal);
    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}
