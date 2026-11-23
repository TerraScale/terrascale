using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using static TerraScale.Dashboard.Platform.Clients.ServiceTransport;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class TerraBaseHttpClient(ServiceTransport transport)
{
    public const string HttpClientName = "terrabase";

    private static ClientJson Wire => ClientJson.Default;

    public Task<TerraBaseRegionsOutcome> ListRegionsAsync(CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseRegions, TerraBaseRegionsOutcome>(ServiceCaller.Operator, HttpMethod.Get, "/v1/regions", Wire.TerraBaseRegions, cancellationToken);

    public Task<TerraBaseHealthOutcome> HealthAsync(CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseHealth, TerraBaseHealthOutcome>(ServiceCaller.Operator, HttpMethod.Get, "/healthz", Wire.TerraBaseHealth, cancellationToken);

    public Task<TerraBaseDatabasePageOutcome> ListDatabasesAsync(ServiceCaller caller, string projectId, string? after, int? limit,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseDatabasePage, TerraBaseDatabasePageOutcome>(caller, HttpMethod.Get, string.Concat(Databases(projectId),
            Query(("after", after), ("limit", limit?.ToString(CultureInfo.InvariantCulture)))), Wire.TerraBaseDatabasePage, cancellationToken);

    public Task<TerraBaseDatabaseOutcome> GetDatabaseAsync(ServiceCaller caller, string projectId, string database,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseManagedDatabase, TerraBaseDatabaseOutcome>(caller, HttpMethod.Get, Database(projectId, database), Wire.TerraBaseManagedDatabase, cancellationToken);

    public Task<TerraBaseDatabaseRegionsOutcome> GetDatabaseRegionsAsync(ServiceCaller caller, string projectId, string database,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseDatabaseRegions, TerraBaseDatabaseRegionsOutcome>(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/regions"), Wire.TerraBaseDatabaseRegions, cancellationToken);

    public Task<TerraBaseDatabaseOutcome> CreateDatabaseAsync(ServiceCaller caller, string projectId, TerraBaseDatabasePlacement placement,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseManagedDatabase, TerraBaseDatabaseOutcome>(caller, HttpMethod.Post, Databases(projectId), Json(Body(placement), Wire.TerraBaseDatabaseBody), Wire.TerraBaseManagedDatabase, null,
            cancellationToken);

    public Task<TerraBaseDatabaseOutcome> SetDatabaseAsync(ServiceCaller caller, string projectId, string database,
        TerraBaseDatabasePlacement placement, CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseManagedDatabase, TerraBaseDatabaseOutcome>(caller, HttpMethod.Patch, Database(projectId, database), Json(Body(placement), Wire.TerraBaseDatabaseBody), Wire.TerraBaseManagedDatabase,
            null, cancellationToken);

    public Task<TerraBaseActionOutcome> DeleteDatabaseAsync(ServiceCaller caller, string projectId, string database, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, Database(projectId, database), null, cancellationToken);

    public Task<TerraBaseDatabaseOutcome> BranchAsync(ServiceCaller caller, string projectId, string database, string name, long? ttlSeconds,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseManagedDatabase, TerraBaseDatabaseOutcome>(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/branches"),
            Json(new TerraBaseBranchBody(name, ttlSeconds), Wire.TerraBaseBranchBody), Wire.TerraBaseManagedDatabase, null, cancellationToken);

    public Task<TerraBaseDatabaseOutcome> SetDatabaseTtlAsync(ServiceCaller caller, string projectId, string database, long ttlSeconds,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseManagedDatabase, TerraBaseDatabaseOutcome>(caller, HttpMethod.Put, string.Concat(Database(projectId, database), "/ttl"),
            Json(new TerraBaseDatabaseTtl(ttlSeconds), Wire.TerraBaseDatabaseTtl), Wire.TerraBaseManagedDatabase, null, cancellationToken);

    public Task<TerraBaseDatabaseOutcome> ClearDatabaseTtlAsync(ServiceCaller caller, string projectId, string database,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseManagedDatabase, TerraBaseDatabaseOutcome>(caller, HttpMethod.Delete, string.Concat(Database(projectId, database), "/ttl"), null, Wire.TerraBaseManagedDatabase, null,
            cancellationToken);

    public Task<TerraBaseJsonOutcome> ExecuteSqlAsync(ServiceCaller caller, string projectId, string database, JsonElement request,
        CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/sql/execute"), Json(request, Wire.JsonElement), Wire.JsonElement,
            null, cancellationToken);

    public Task<TerraBaseRulesOutcome> GetRulesAsync(ServiceCaller caller, string projectId, string database, CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseRules, TerraBaseRulesOutcome>(caller, HttpMethod.Get, Rules(projectId, database), Wire.TerraBaseRules, cancellationToken);

    public async Task<TerraBaseRulesDeployOutcome> DeployRulesAsync(ServiceCaller caller, string projectId, string database, string? version,
        JsonElement rules, CancellationToken cancellationToken) =>
        Deploy(await transport.SendRawAsync(caller, HttpMethod.Put, Rules(projectId, database), Json(rules, Wire.JsonElement), Precondition(version),
            cancellationToken));

    public Task<TerraBaseRulesVersionPageOutcome> ListRulesVersionsAsync(ServiceCaller caller, string projectId, string database, string? after,
        int? limit, CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseRulesVersionPage, TerraBaseRulesVersionPageOutcome>(caller, HttpMethod.Get, string.Concat(Rules(projectId, database), "/versions",
            Query(("after", after), ("limit", limit?.ToString(CultureInfo.InvariantCulture)))), Wire.TerraBaseRulesVersionPage, cancellationToken);

    public Task<TerraBaseRulesOutcome> GetRulesVersionAsync(ServiceCaller caller, string projectId, string database, string version,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseRules, TerraBaseRulesOutcome>(caller, HttpMethod.Get, string.Concat(Rules(projectId, database), "/versions/", Segment(version)), Wire.TerraBaseRules, cancellationToken);

    public async Task<TerraBaseRulesDeployOutcome> RestoreRulesVersionAsync(ServiceCaller caller, string projectId, string database, string version,
        string current, CancellationToken cancellationToken) =>
        Deploy(await transport.SendRawAsync(caller, HttpMethod.Post, string.Concat(Rules(projectId, database), "/versions/", Segment(version), "/restore"), null,
            Precondition(current), cancellationToken));

    public Task<TerraBaseRulesTestOutcome> TestRulesAsync(ServiceCaller caller, string projectId, string database, JsonElement request,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseRulesTestResult, TerraBaseRulesTestOutcome>(caller, HttpMethod.Post, string.Concat(Rules(projectId, database), ":test"), Json(request, Wire.JsonElement), Wire.TerraBaseRulesTestResult,
            null, cancellationToken);

    public Task<TerraBaseJsonOutcome> GetBackupDestinationAsync(ServiceCaller caller, string projectId, CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Get, BackupDestination(projectId), Wire.JsonElement, cancellationToken);

    public Task<TerraBaseJsonOutcome> SetBackupDestinationAsync(ServiceCaller caller, string projectId, JsonElement request,
        CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Put, BackupDestination(projectId), Json(request, Wire.JsonElement), Wire.JsonElement, null, cancellationToken);

    public Task<TerraBaseActionOutcome> DeleteBackupDestinationAsync(ServiceCaller caller, string projectId, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, BackupDestination(projectId), null, cancellationToken);

    public Task<TerraBaseJsonOutcome> GetBackupStatusAsync(ServiceCaller caller, string projectId, string database, CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/backups"), Wire.JsonElement, cancellationToken);

    public Task<TerraBaseJsonOutcome> CreateBackupAsync(ServiceCaller caller, string projectId, string database, CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/backups"), null, Wire.JsonElement, null, cancellationToken);

    public Task<TerraBaseJsonOutcome> GetBackupPolicyAsync(ServiceCaller caller, string projectId, string database, CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/backup-policy"), Wire.JsonElement, cancellationToken);

    public Task<TerraBaseJsonOutcome> SetBackupPolicyAsync(ServiceCaller caller, string projectId, string database, JsonElement request,
        CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Put, string.Concat(Database(projectId, database), "/backup-policy"), Json(request, Wire.JsonElement), Wire.JsonElement,
            null, cancellationToken);

    public Task<TerraBaseJsonOutcome> RestoreBackupAsync(ServiceCaller caller, string projectId, string database, string backupId, JsonElement request,
        CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/backups/", Segment(backupId), "/restore"),
            Json(request, Wire.JsonElement), Wire.JsonElement, null, cancellationToken);

    public Task<TerraBaseJsonOutcome> GetRestoreOperationAsync(ServiceCaller caller, string projectId, string database, string operationId,
        CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/restores/", Segment(operationId)), Wire.JsonElement,
            cancellationToken);

    public Task<TerraBaseRestoreWindowOutcome> GetRestoreWindowAsync(ServiceCaller caller, string projectId, string database,
        CancellationToken cancellationToken) =>
        transport.SendMappedAsync(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/point-in-time"), null, Wire.TerraBaseRestoreWindowBody, null,
            static window => TerraBaseRestoreWindowOutcome.Accepted(new TerraBaseRestoreWindow(window.EarliestRestorePointUtc, window.LatestRestorePointUtc)),
            TerraBaseRestoreWindowOutcome.Refused, cancellationToken);

    public Task<TerraBasePointInTimePlanOutcome> PlanPointInTimeAsync(ServiceCaller caller, string projectId, string database,
        TerraBasePointInTimeRequest request, CancellationToken cancellationToken) =>
        transport.SendMappedAsync(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/point-in-time/plan"),
            Json(Restore(request), Wire.TerraBaseRestoreBody), Wire.TerraBaseRestoreWindowBody, null,
            window => TerraBasePointInTimePlanOutcome.Accepted(new TerraBasePointInTimePlan(new(window.EarliestRestorePointUtc, window.LatestRestorePointUtc),
                request.PointInTime, window.EarliestRestorePointUtc, window.LatestRestorePointUtc, 0, window.Feasible ? "complete" : "gap")),
            TerraBasePointInTimePlanOutcome.Refused, cancellationToken);

    public Task<TerraBaseJsonOutcome> RestorePointInTimeAsync(ServiceCaller caller, string projectId, string database,
        TerraBasePointInTimeRequest request, CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/point-in-time/restore"),
            Json(Restore(request), Wire.TerraBaseRestoreBody), Wire.JsonElement, null, cancellationToken);

    public Task<TerraBaseJsonOutcome> RewindPointInTimeAsync(ServiceCaller caller, string projectId, string database,
        TerraBasePointInTimeRequest request, CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/point-in-time/rewind"),
            Json(Restore(request), Wire.TerraBaseRestoreBody), Wire.JsonElement, null, cancellationToken);

    public Task<TerraBaseRequestLogPageOutcome> ReadLogsAsync(ServiceCaller caller, string projectId, string database, string? after,
        CancellationToken cancellationToken) =>
        transport.SendMappedAsync(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/logs", Query(("after", after), ("limit", "100"))), null,
            Wire.TerraBaseLogsBody, null, Page, TerraBaseRequestLogPageOutcome.Refused, cancellationToken);

    public Task<TerraBaseProjectUsageOutcome> GetProjectUsageAsync(ServiceCaller caller, string projectId, DateTimeOffset from,
        DateTimeOffset until, CancellationToken cancellationToken) =>
        transport.SendMappedAsync(caller, HttpMethod.Get, string.Concat("/v1/projects/", Segment(projectId), "/usage",
            Query(("month", until.ToString("yyyy-MM", CultureInfo.InvariantCulture)))), null, Wire.TerraBaseUsageBody, null,
            usage => TerraBaseProjectUsageOutcome.Accepted(ProjectUsage(usage, from, until)), TerraBaseProjectUsageOutcome.Refused, cancellationToken);

    public Task<TerraBaseDatabaseUsageOutcome> GetDatabaseUsageAsync(ServiceCaller caller, string projectId, string database,
        DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken) =>
        transport.SendMappedAsync(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/usage",
            Query(("month", until.ToString("yyyy-MM", CultureInfo.InvariantCulture)))), null, Wire.TerraBaseUsageBody, null,
            usage => TerraBaseDatabaseUsageOutcome.Accepted(DatabaseUsage(usage, from, until)), TerraBaseDatabaseUsageOutcome.Refused, cancellationToken);

    public Task<TerraBaseJsonOutcome> GetAnalyticsAsync(ServiceCaller caller, string projectId, string? database, CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(caller, HttpMethod.Get, database is null
            ? string.Concat("/v1/projects/", Segment(projectId), "/analytics")
            : string.Concat(Database(projectId, database), "/analytics"), Wire.JsonElement, cancellationToken);

    public Task<TerraBaseJsonOutcome> GetOperatorAnalyticsAsync(string projectId, CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(ServiceCaller.Operator, HttpMethod.Get, string.Concat("/v1/usage/projects/", Segment(projectId), "/analytics"), Wire.JsonElement,
            cancellationToken);

    public Task<TerraBaseJsonOutcome> ListAllowancesAsync(CancellationToken cancellationToken) =>
        transport.SendAsync<JsonElement, TerraBaseJsonOutcome>(ServiceCaller.Operator, HttpMethod.Get, "/v1/usage/allowances", Wire.JsonElement, cancellationToken);

    private static string BackupDestination(string projectId) => string.Concat("/v1/projects/", Segment(projectId), "/backup-destination");

    private static string Databases(string projectId) => string.Concat("/v1/projects/", Segment(projectId), "/databases");

    private static string Database(string projectId, string database) => string.Concat(Databases(projectId), "/", Segment(database));

    private static string Rules(string projectId, string database) => string.Concat(Database(projectId, database), "/rules");

    private static TerraBaseDatabaseBody Body(TerraBaseDatabasePlacement placement) =>
        new(placement.Name, placement.Regions ?? Regions(placement), placement.AllRegions, placement.TtlSeconds, placement.DefaultConsistency);

    private static IReadOnlyList<string>? Regions(TerraBaseDatabasePlacement placement) =>
        placement.HomeRegion is null && placement.ReplicaRegions is null
            ? null
            : [.. new[] { placement.HomeRegion }.Concat(placement.ReplicaRegions ?? []).OfType<string>().Where(static region => region.Length > 0).Distinct(StringComparer.Ordinal)];

    private static TerraBaseRestoreBody Restore(TerraBasePointInTimeRequest request) => new(request.PointInTime, request.Name, request.TtlSeconds);

    private static Action<HttpRequestHeaders> Precondition(string? version) => version is null
        ? static headers => headers.IfNoneMatch.Add(EntityTagHeaderValue.Any)
        : headers => headers.IfMatch.Add(new EntityTagHeaderValue(string.Concat("\"", version, "\"")));

    private static TerraBaseRulesDeployOutcome Deploy(TerraBaseRawOutcome answer) => answer switch
    {
        RawAnswer { Status: >= 200 and < 300 } ok => Parse(ok, Wire.TerraBaseRules, static rules => new TerraBaseRulesDeployOutcome(new TerraBaseRulesDeploy(rules, [])),
            TerraBaseRulesDeployOutcome.Refused),
        RawAnswer { Status: StatusCodes.Status400BadRequest } invalid => Parse(invalid, Wire.TerraBaseRulesErrorBody, body => body switch
        {
            { Error: { Code: "invalid_rules", Details.Problems: { } problems } } => new TerraBaseRulesDeployOutcome(new TerraBaseRulesDeploy(null, problems)),
            _ => new TerraBaseRulesDeployOutcome(Refusal(invalid)),
        }, _ => new TerraBaseRulesDeployOutcome(Refusal(invalid))),
        RawAnswer refused => Refusal(refused),
        TerraBaseRefusal refusal => refusal,
    };

    private static TerraBaseRequestLogPageOutcome Page(TerraBaseLogsBody logs) =>
        logs.Items.Select(static entry => entry.DatabaseId).Distinct(StringComparer.Ordinal).ToArray() switch
        {
            [] => new TerraBaseRequestLogPage("", [], false, false, logs.Next, logs.Gap),
            [var databaseId] => new TerraBaseRequestLogPage(databaseId,
                [.. logs.Items.Select(static entry => new TerraBaseRequestLog(entry.Id, entry.At, entry.Region, entry.Method, entry.Path, entry.Status, entry.DurationMs, 1,
                    entry.Method == "GET", entry.Id))],
                false, false, logs.Next, logs.Gap),
            _ => TerraBaseRefusal.Invalid("Request logs mixed several databases."),
        };

    private static TerraBaseUsagePeriod Period(TerraBaseUsageBody usage, DateTimeOffset from, DateTimeOffset until) =>
        new(from, until, "month", usage.Metric("reads"), usage.Metric("writes"));

    private static TerraBaseProjectUsage ProjectUsage(TerraBaseUsageBody usage, DateTimeOffset from, DateTimeOffset until) =>
        new(ScopeId(usage, "project:"), 0, usage.Metric("file_bytes"), 0, usage.Metric("reads"), usage.Metric("writes"), Period(usage, from, until), [], [],
            usage.Metric("file_bytes"),
            new TerraBaseUsageMonth(usage.Month, usage.Metric("reads"), usage.Metric("writes"), usage.Metric("reads") + usage.Metric("writes"),
                Invocations: usage.Metric("invocations")));

    private static TerraBaseDatabaseUsage DatabaseUsage(TerraBaseUsageBody usage, DateTimeOffset from, DateTimeOffset until) =>
        new(ScopeId(usage, "db:"), usage.Metric("storage_bytes"), usage.Metric("logical_bytes"), usage.Metric("reads"), usage.Metric("writes"), Period(usage, from, until),
            []);

    private static string ScopeId(TerraBaseUsageBody usage, string prefix) =>
        usage.Scope.StartsWith(prefix, StringComparison.Ordinal) ? usage.Scope[prefix.Length..] : "";
}
