using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed record TerraBaseManagedDatabase(string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt, string Region = "",
    string HomeRegion = "", IReadOnlyList<string>? ReplicaRegions = null, bool AllRegions = false, string? PreviousName = null, string? ConflictOf = null,
    DateTimeOffset? ExpiresAt = null, IReadOnlyList<string>? Regions = null)
{
    public string? DefaultConsistency { get; init; }

    public bool Strict { get; init; }

    public string? State { get; init; }
}

public sealed record TerraBaseDatabasePage(IReadOnlyList<TerraBaseManagedDatabase> Items, string? Next);

public sealed record TerraBaseDatabasePlacement(string? Name = null, string? HomeRegion = null, IReadOnlyList<string>? ReplicaRegions = null, bool? AllRegions = null,
    long? TtlSeconds = null, IReadOnlyList<string>? Regions = null)
{
    public string? DefaultConsistency { get; init; }
}

public sealed record TerraBaseDatabaseBody(string? Name, IReadOnlyList<string>? Regions, bool? AllRegions, long? TtlSeconds, string? DefaultConsistency);

public sealed record TerraBaseDatabaseTtl(long TtlSeconds);

public sealed record TerraBaseBranchBody(string Name, long? TtlSeconds);

public sealed record TerraBaseRegionReplication(string Region, string AppliedBookmark, long LagMs, bool Healthy);

public sealed record TerraBaseDatabaseRegions(string DatabaseId, DateTimeOffset ObservedAtUtc, IReadOnlyList<TerraBaseRegionReplication> Regions);

public sealed record TerraBaseRegions(string Region, string Machine, IReadOnlyList<string> Regions);

public sealed record TerraBaseHealth(string Status, string Version, string Region);

public sealed record TerraBaseRulesProblem(string Table, string Field, string Code, string Message);

public sealed record TerraBaseRules(string Version, JsonElement Tables, IReadOnlyList<TerraBaseRulesProblem>? Problems, IReadOnlyList<TerraBaseRulesProblem>? Warnings,
    DateTimeOffset UpdatedAt, string UpdatedBy);

public sealed record TerraBaseRulesDeploy(TerraBaseRules? Rules, IReadOnlyList<TerraBaseRulesProblem> Problems);

public sealed record TerraBaseRulesVersion(string Version, DateTimeOffset UpdatedAt, string UpdatedBy, string Digest);

public sealed record TerraBaseRulesVersionPage(IReadOnlyList<TerraBaseRulesVersion> Items, string? Next);

public sealed record TerraBaseRulesTestValue(string Rule, bool Value);

public sealed record TerraBaseRulesTestResult(bool Allowed, string DecidedBy, string? Clause, IReadOnlyList<TerraBaseRulesTestValue> Results,
    IReadOnlyList<string> Columns, IReadOnlyList<TerraBaseRulesProblem> Problems);

public sealed record TerraBaseRulesErrorDetails(IReadOnlyList<TerraBaseRulesProblem> Problems);

public sealed record TerraBaseRulesErrorDetail(string Code, string Message, TerraBaseRulesErrorDetails? Details);

public sealed record TerraBaseRulesErrorBody(TerraBaseRulesErrorDetail Error);

public sealed record TerraBaseRestoreWindow(DateTimeOffset Earliest, DateTimeOffset Latest);

public sealed record TerraBaseRestoreWindowBody(string DatabaseId, DateTimeOffset EarliestRestorePointUtc, DateTimeOffset LatestRestorePointUtc, bool Feasible);

public sealed record TerraBasePointInTimePlan(TerraBaseRestoreWindow Window, DateTimeOffset PointInTime, DateTimeOffset SnapshotAt, DateTimeOffset? LatestSegmentAt,
    int SegmentCount, string HistoryCheck);

public sealed record TerraBasePointInTimeRequest(DateTimeOffset PointInTime, string? Name, string? Destination, long? TtlSeconds = null);

public sealed record TerraBaseRestoreBody(DateTimeOffset? PointInTime, string? Name, long? TtlSeconds);

public sealed record TerraBaseUsagePeriod(DateTimeOffset From, DateTimeOffset To, string Granularity, long Reads, long Writes);

public sealed record TerraBaseUsagePoint(DateTimeOffset Start, long Reads, long Writes);

public sealed record TerraBaseUsageMonth(string Month, long ReadUnits, long WriteUnits, long Units, long? OrganizationUnits = null, long? OrganizationReadUnits = null,
    long Invocations = 0, long? OrganizationInvocations = null);

public sealed record TerraBaseDatabaseUsage(string DatabaseId, long StorageBytes, long LogicalBytes, long Reads, long Writes, TerraBaseUsagePeriod Period,
    IReadOnlyList<TerraBaseUsagePoint> Series);

public sealed record TerraBaseProjectUsage(string ProjectId, long Databases, long StorageBytes, long LogicalBytes, long Reads, long Writes, TerraBaseUsagePeriod Period,
    IReadOnlyList<TerraBaseUsagePoint> Series, IReadOnlyList<TerraBaseDatabaseUsage> Items, long FileBytes = 0, TerraBaseUsageMonth? Month = null);

public sealed record TerraBaseUsageTotals(long Requests, long Reads, long Writes, long RowsRead, long RowsWritten, long StrongCommits, long StorageByteHours);

public sealed record TerraBaseProjectUsageTotals(string ProjectId, long Requests, long Reads, long Writes, long RowsRead, long RowsWritten, long StrongCommits,
    long StorageByteHours);

public sealed record TerraBaseOrganizationUsage(string OrganizationId, string Month, bool Complete, IReadOnlyList<string> Gaps,
    TerraBaseUsageTotals Totals, IReadOnlyList<TerraBaseProjectUsageTotals> Projects);

public sealed record TerraBaseUsageBody(string Scope, string Month, IReadOnlyDictionary<string, long> Metrics)
{
    public long Metric(string name) => Metrics.TryGetValue(name, out var value) ? value : 0;
}

public sealed record TerraBaseRequestLog(string Id, DateTimeOffset Time, string Region, string Method, string Route, int Status, double LatencyMs, long Operations,
    bool IsQuery, string? RequestId = null, long Statements = 0, long StatementErrors = 0);

public sealed record TerraBaseRequestLogPage(string DatabaseId, IReadOnlyList<TerraBaseRequestLog> Items, bool Truncated, bool RecordingLimited, string? NextCursor = null,
    bool Gap = false);

public sealed record TerraBaseLogEntry(string Id, string DatabaseId, DateTimeOffset At, string Method, string Path, int Status, long DurationMs, string Region);

public sealed record TerraBaseLogsBody(IReadOnlyList<TerraBaseLogEntry> Items, string? Next, bool Gap = false);

public sealed record TerraBasePlanBody(string Plan);

public sealed record TerraBaseErrorBody(string Code, string Message, string? SqlState, int? Statement, IReadOnlyDictionary<string, string>? Details);

public sealed record TerraBaseErrorEnvelope(TerraBaseErrorBody Error);
