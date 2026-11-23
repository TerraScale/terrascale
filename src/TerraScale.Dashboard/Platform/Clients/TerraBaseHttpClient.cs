using TerraScale.Results;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using static TerraScale.Dashboard.Platform.Clients.ServiceTransport;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class TerraBaseHttpClient(ServiceTransport transport)
{
    public const string HttpClientName = "terrabase";

    private static ClientJson Wire => ClientJson.Default;

    public Task<Result<TerraBaseRegions, TerraBaseRefusal>> ListRegionsAsync(CancellationToken cancellationToken) =>
        transport.SendAsync(ServiceCaller.Operator, HttpMethod.Get, "/v1/regions", Wire.TerraBaseRegions, cancellationToken);

    public Task<Result<TerraBaseHealth, TerraBaseRefusal>> HealthAsync(CancellationToken cancellationToken) =>
        transport.SendAsync(ServiceCaller.Operator, HttpMethod.Get, "/healthz", Wire.TerraBaseHealth, cancellationToken);

    public Task<Result<TerraBaseDatabasePage, TerraBaseRefusal>> ListDatabasesAsync(ServiceCaller caller, string projectId, string? after, int? limit,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Databases(projectId),
            Query(("after", after), ("limit", limit?.ToString(CultureInfo.InvariantCulture)))), Wire.TerraBaseDatabasePage, cancellationToken);

    public Task<Result<TerraBaseManagedDatabase, TerraBaseRefusal>> GetDatabaseAsync(ServiceCaller caller, string projectId, string database,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, Database(projectId, database), Wire.TerraBaseManagedDatabase, cancellationToken);

    public Task<Result<TerraBaseDatabaseRegions, TerraBaseRefusal>> GetDatabaseRegionsAsync(ServiceCaller caller, string projectId, string database,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/regions"), Wire.TerraBaseDatabaseRegions, cancellationToken);

    public Task<Result<TerraBaseManagedDatabase, TerraBaseRefusal>> CreateDatabaseAsync(ServiceCaller caller, string projectId, TerraBaseDatabasePlacement placement,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, Databases(projectId), Json(Body(placement), Wire.TerraBaseDatabaseBody), Wire.TerraBaseManagedDatabase, null,
            cancellationToken);

    public Task<Result<TerraBaseManagedDatabase, TerraBaseRefusal>> SetDatabaseAsync(ServiceCaller caller, string projectId, string database,
        TerraBaseDatabasePlacement placement, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Patch, Database(projectId, database), Json(Body(placement), Wire.TerraBaseDatabaseBody), Wire.TerraBaseManagedDatabase,
            null, cancellationToken);

    public Task<Result<Done, TerraBaseRefusal>> DeleteDatabaseAsync(ServiceCaller caller, string projectId, string database, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, Database(projectId, database), null, cancellationToken);

    public Task<Result<TerraBaseManagedDatabase, TerraBaseRefusal>> BranchAsync(ServiceCaller caller, string projectId, string database, string name, long? ttlSeconds,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/branches"),
            Json(new TerraBaseBranchBody(name, ttlSeconds), Wire.TerraBaseBranchBody), Wire.TerraBaseManagedDatabase, null, cancellationToken);

    public Task<Result<TerraBaseManagedDatabase, TerraBaseRefusal>> SetDatabaseTtlAsync(ServiceCaller caller, string projectId, string database, long ttlSeconds,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Put, string.Concat(Database(projectId, database), "/ttl"),
            Json(new TerraBaseDatabaseTtl(ttlSeconds), Wire.TerraBaseDatabaseTtl), Wire.TerraBaseManagedDatabase, null, cancellationToken);

    public Task<Result<TerraBaseManagedDatabase, TerraBaseRefusal>> ClearDatabaseTtlAsync(ServiceCaller caller, string projectId, string database,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, string.Concat(Database(projectId, database), "/ttl"), null, Wire.TerraBaseManagedDatabase, null,
            cancellationToken);

    public Task<Result<JsonElement, TerraBaseRefusal>> ExecuteSqlAsync(ServiceCaller caller, string projectId, string database, JsonElement request,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/sql/execute"), Json(request, Wire.JsonElement), Wire.JsonElement,
            null, cancellationToken);

    public Task<Result<TerraBaseRules, TerraBaseRefusal>> GetRulesAsync(ServiceCaller caller, string projectId, string database, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, Rules(projectId, database), Wire.TerraBaseRules, cancellationToken);

    public async Task<Result<TerraBaseRulesDeploy, TerraBaseRefusal>> DeployRulesAsync(ServiceCaller caller, string projectId, string database, string? version,
        JsonElement rules, CancellationToken cancellationToken) =>
        Deploy(await transport.SendRawAsync(caller, HttpMethod.Put, Rules(projectId, database), Json(rules, Wire.JsonElement), Precondition(version),
            cancellationToken));

    public Task<Result<TerraBaseRulesVersionPage, TerraBaseRefusal>> ListRulesVersionsAsync(ServiceCaller caller, string projectId, string database, string? after,
        int? limit, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Rules(projectId, database), "/versions",
            Query(("after", after), ("limit", limit?.ToString(CultureInfo.InvariantCulture)))), Wire.TerraBaseRulesVersionPage, cancellationToken);

    public Task<Result<TerraBaseRules, TerraBaseRefusal>> GetRulesVersionAsync(ServiceCaller caller, string projectId, string database, string version,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Rules(projectId, database), "/versions/", Segment(version)), Wire.TerraBaseRules, cancellationToken);

    public async Task<Result<TerraBaseRulesDeploy, TerraBaseRefusal>> RestoreRulesVersionAsync(ServiceCaller caller, string projectId, string database, string version,
        string current, CancellationToken cancellationToken) =>
        Deploy(await transport.SendRawAsync(caller, HttpMethod.Post, string.Concat(Rules(projectId, database), "/versions/", Segment(version), "/restore"), null,
            Precondition(current), cancellationToken));

    public Task<Result<TerraBaseRulesTestResult, TerraBaseRefusal>> TestRulesAsync(ServiceCaller caller, string projectId, string database, JsonElement request,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, string.Concat(Rules(projectId, database), ":test"), Json(request, Wire.JsonElement), Wire.TerraBaseRulesTestResult,
            null, cancellationToken);

    public Task<Result<JsonElement, TerraBaseRefusal>> GetBackupStatusAsync(ServiceCaller caller, string projectId, string database, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/backups"), Wire.JsonElement, cancellationToken);

    public Task<Result<JsonElement, TerraBaseRefusal>> CreateBackupAsync(ServiceCaller caller, string projectId, string database, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/backups"), null, Wire.JsonElement, null, cancellationToken);

    public Task<Result<JsonElement, TerraBaseRefusal>> GetBackupPolicyAsync(ServiceCaller caller, string projectId, string database, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/backup-policy"), Wire.JsonElement, cancellationToken);

    public Task<Result<JsonElement, TerraBaseRefusal>> SetBackupPolicyAsync(ServiceCaller caller, string projectId, string database, JsonElement request,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Put, string.Concat(Database(projectId, database), "/backup-policy"), Json(request, Wire.JsonElement), Wire.JsonElement,
            null, cancellationToken);

    public Task<Result<JsonElement, TerraBaseRefusal>> RestoreBackupAsync(ServiceCaller caller, string projectId, string database, string backupId, JsonElement request,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/backups/", Segment(backupId), "/restore"),
            Json(request, Wire.JsonElement), Wire.JsonElement, null, cancellationToken);

    public Task<Result<JsonElement, TerraBaseRefusal>> GetRestoreOperationAsync(ServiceCaller caller, string projectId, string database, string operationId,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/restores/", Segment(operationId)), Wire.JsonElement,
            cancellationToken);

    public async Task<Result<TerraBaseRestoreWindow, TerraBaseRefusal>> GetRestoreWindowAsync(ServiceCaller caller, string projectId, string database,
        CancellationToken cancellationToken) =>
        await transport.SendAsync(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/point-in-time"), Wire.TerraBaseRestoreWindowBody,
            cancellationToken) switch
        {
            TerraBaseRestoreWindowBody window => new TerraBaseRestoreWindow(window.EarliestRestorePointUtc, window.LatestRestorePointUtc),
            TerraBaseRefusal refusal => refusal,
        };

    public async Task<Result<TerraBasePointInTimePlan, TerraBaseRefusal>> PlanPointInTimeAsync(ServiceCaller caller, string projectId, string database,
        TerraBasePointInTimeRequest request, CancellationToken cancellationToken) =>
        await transport.SendAsync(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/point-in-time/plan"),
            Json(Restore(request), Wire.TerraBaseRestoreBody), Wire.TerraBaseRestoreWindowBody, null, cancellationToken) switch
        {
            TerraBaseRestoreWindowBody window => new TerraBasePointInTimePlan(new(window.EarliestRestorePointUtc, window.LatestRestorePointUtc), request.PointInTime,
                window.EarliestRestorePointUtc, window.LatestRestorePointUtc, 0, window.Feasible ? "complete" : "gap"),
            TerraBaseRefusal refusal => refusal,
        };

    public Task<Result<JsonElement, TerraBaseRefusal>> RestorePointInTimeAsync(ServiceCaller caller, string projectId, string database,
        TerraBasePointInTimeRequest request, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/point-in-time/restore"),
            Json(Restore(request), Wire.TerraBaseRestoreBody), Wire.JsonElement, null, cancellationToken);

    public Task<Result<JsonElement, TerraBaseRefusal>> RewindPointInTimeAsync(ServiceCaller caller, string projectId, string database,
        TerraBasePointInTimeRequest request, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, string.Concat(Database(projectId, database), "/point-in-time/rewind"),
            Json(Restore(request), Wire.TerraBaseRestoreBody), Wire.JsonElement, null, cancellationToken);

    public async Task<Result<TerraBaseRequestLogPage, TerraBaseRefusal>> ReadLogsAsync(ServiceCaller caller, string projectId, string database, string? after,
        CancellationToken cancellationToken) =>
        await transport.SendAsync(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/logs", Query(("after", after))), Wire.TerraBaseLogsBody,
            cancellationToken) switch
        {
            TerraBaseLogsBody logs => Page(database, logs),
            TerraBaseRefusal refusal => refusal,
        };

    public async Task<Result<TerraBaseProjectUsage, TerraBaseRefusal>> GetProjectUsageAsync(ServiceCaller caller, string projectId, DateTimeOffset from,
        DateTimeOffset until, CancellationToken cancellationToken) =>
        await transport.SendAsync(caller, HttpMethod.Get, string.Concat("/v1/projects/", Segment(projectId), "/usage",
            Query(("month", until.ToString("yyyy-MM", CultureInfo.InvariantCulture)))), Wire.TerraBaseUsageBody, cancellationToken) switch
        {
            TerraBaseUsageBody usage => ProjectUsage(projectId, usage, from, until),
            TerraBaseRefusal refusal => refusal,
        };

    public async Task<Result<TerraBaseDatabaseUsage, TerraBaseRefusal>> GetDatabaseUsageAsync(ServiceCaller caller, string projectId, string database,
        DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken) =>
        await transport.SendAsync(caller, HttpMethod.Get, string.Concat(Database(projectId, database), "/usage",
            Query(("month", until.ToString("yyyy-MM", CultureInfo.InvariantCulture)))), Wire.TerraBaseUsageBody, cancellationToken) switch
        {
            TerraBaseUsageBody usage => DatabaseUsage(database, usage, from, until),
            TerraBaseRefusal refusal => refusal,
        };

    public Task<Result<JsonElement, TerraBaseRefusal>> GetAnalyticsAsync(ServiceCaller caller, string projectId, string? database, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, database is null
            ? string.Concat("/v1/projects/", Segment(projectId), "/analytics")
            : string.Concat(Database(projectId, database), "/analytics"), Wire.JsonElement, cancellationToken);

    public Task<Result<JsonElement, TerraBaseRefusal>> GetOperatorAnalyticsAsync(string projectId, CancellationToken cancellationToken) =>
        transport.SendAsync(ServiceCaller.Operator, HttpMethod.Get, string.Concat("/v1/usage/projects/", Segment(projectId), "/analytics"), Wire.JsonElement,
            cancellationToken);

    public Task<Result<JsonElement, TerraBaseRefusal>> ListAllowancesAsync(CancellationToken cancellationToken) =>
        transport.SendAsync(ServiceCaller.Operator, HttpMethod.Get, "/v1/usage/allowances", Wire.JsonElement, cancellationToken);

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

    private static Result<TerraBaseRulesDeploy, TerraBaseRefusal> Deploy(Result<RawAnswer, TerraBaseRefusal> answer) => answer switch
    {
        RawAnswer { Status: >= 200 and < 300 } ok => Parse(ok, Wire.TerraBaseRules) switch
        {
            TerraBaseRules rules => new TerraBaseRulesDeploy(rules, []),
            TerraBaseRefusal refusal => refusal,
        },
        RawAnswer { Status: StatusCodes.Status400BadRequest } invalid when Parse(invalid, Wire.TerraBaseRulesErrorBody) is var parsed => parsed switch
        {
            TerraBaseRulesErrorBody { Error: { Code: "invalid_rules", Details.Problems: { } problems } } => new TerraBaseRulesDeploy(null, problems),
            TerraBaseRulesErrorBody or TerraBaseRefusal => Refusal(invalid),
        },
        RawAnswer refused => Refusal(refused),
        TerraBaseRefusal refusal => refusal,
    };

    private static TerraBaseRequestLogPage Page(string database, TerraBaseLogsBody logs) =>
        new(logs.Items.Count > 0 ? logs.Items[0].DatabaseId : database,
            [.. logs.Items.Select(static entry => new TerraBaseRequestLog(entry.Id, entry.At, entry.Region, entry.Method, entry.Path, entry.Status, entry.DurationMs, 1,
                entry.Method == "GET"))],
            false, false, logs.Next);

    private static TerraBaseUsagePeriod Period(TerraBaseUsageBody usage, DateTimeOffset from, DateTimeOffset until) =>
        new(from, until, "month", usage.Metric("reads"), usage.Metric("writes"));

    private static TerraBaseProjectUsage ProjectUsage(string projectId, TerraBaseUsageBody usage, DateTimeOffset from, DateTimeOffset until) =>
        new(projectId, 0, usage.Metric("file_bytes"), 0, 0, 0, new TerraBaseUsagePeriod(from, until, "month", 0, 0), [], [], usage.Metric("file_bytes"),
            new TerraBaseUsageMonth(usage.Month, usage.Metric("reads"), usage.Metric("writes"), usage.Metric("reads") + usage.Metric("writes"),
                Invocations: usage.Metric("invocations")));

    private static TerraBaseDatabaseUsage DatabaseUsage(string database, TerraBaseUsageBody usage, DateTimeOffset from, DateTimeOffset until) =>
        new(database, usage.Metric("storage_bytes"), usage.Metric("logical_bytes"), usage.Metric("reads"), usage.Metric("writes"), Period(usage, from, until),
            [new TerraBaseUsagePoint(from, usage.Metric("reads"), usage.Metric("writes"))]);
}
