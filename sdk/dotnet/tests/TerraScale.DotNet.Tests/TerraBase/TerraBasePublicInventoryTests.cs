using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBasePublicInventoryTests
{
    private const string ProjectId = "01JA0000000000000000000001";
    private const string EntityId = "01JA0000000000000000000002";

    [Test]
    public async Task Every_public_source_route_has_an_SDK_call_and_no_route_is_dropped()
    {
        using var inventory = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("current-public-routes.json"));
        await Assert.That(inventory.RootElement.GetProperty("serverCommit").GetString()).IsEqualTo("e7fa366ddb289f5612c93d316b316fb92c568151");
        var expected = inventory.RootElement.GetProperty("operations").EnumerateArray().ToDictionary(r => r.GetProperty("operation").GetString()!, r => r);
        await Assert.That(expected.Count).IsEqualTo(38);
        (string Method, string Path)? sent = null;
        var requests = 0;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent = (request.Method.Method, request.RequestUri!.AbsolutePath);
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"code":"recorded","message":"recorded"}}""", Encoding.UTF8, "application/json"),
            });
        });
        using var http = new HttpClient(handler);
        var origin = new Uri("https://api.example.test");
        var client = new TerraBaseDatabaseClient(http, new TerraBaseDatabaseClientOptions { BaseAddress = origin, ApiKey = "fake-token" });
        var db = client.Database(ProjectId, "app");
        var project = client.Project(ProjectId);
        var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { BaseAddress = origin, AccessTokenProvider = _ => ValueTask.FromResult("fake-token") }).Database(ProjectId, "app");
        var platform = new TerraBasePlatformClient(http, new TerraBasePlatformClientOptions { BaseAddress = origin, AccessTokenProvider = _ => ValueTask.FromResult("fake-token") });
        var health = new TerraBaseHealthClient(http, origin);
        (string Operation, Func<Task> Call)[] calls =
        [
            ("backupz", () => health.GetBackupHealthAsync()),
            ("branchDatabase", () => db.BranchAsync("copy")),
            ("createBackup", () => db.CreateBackupAsync()),
            ("invokeFunction", () => db.InvokeFunctionAsync("hello")),
            ("listFunctionLogs", () => db.ListFunctionLogsAsync("hello")),
            ("listFunctions", () => db.ListFunctionsAsync()),
            ("deployFunction", () => db.DeployFunctionAsync("hello", new("function handler(event) { return event; }"))),
            ("getFunction", () => db.GetFunctionAsync("hello")),
            ("deleteFunction", () => db.DeleteFunctionAsync("hello")),
            ("createDatabase", () => project.CreateDatabaseAsync("app")),
            ("createSavedQuery", () => project.CreateSavedQueryAsync("recent", "SELECT * FROM items LIMIT 1")),
            ("deleteBackupDestination", () => project.DeleteBackupDestinationAsync()),
            ("deleteDatabase", () => project.DeleteDatabaseAsync("app")),
            ("deleteSavedQuery", () => project.DeleteSavedQueryAsync(EntityId)),
            ("getBackupDestination", () => project.GetBackupDestinationAsync()),
            ("getBackupPolicy", () => db.GetBackupPolicyAsync()),
            ("getDatabase", () => project.GetDatabaseAsync("app")),
            ("getDatabaseAnalytics", () => db.GetAnalyticsAsync()),
            ("getDatabaseUsage", () => db.GetUsageAsync()),
            ("getProjectAnalytics", () => project.GetAnalyticsAsync()),
            ("getProjectMetrics", () => project.GetPrometheusMetricsAsync()),
            ("getProjectUsage", () => project.GetUsageAsync()),
            ("getRestoreWindow", () => db.GetRestoreWindowAsync()),
            ("getSavedQuery", () => project.GetSavedQueryAsync(EntityId)),
            ("listBackups", () => db.ListBackupsAsync()),
            ("listDatabases", () => project.ListDatabasesAsync()),
            ("listPlatformDatabases", () => platform.ListDatabasesAsync()),
            ("listSavedQueries", () => project.ListSavedQueriesAsync()),
            ("planPointInTime", () => db.PlanPointInTimeAsync(DateTimeOffset.UnixEpoch)),
            ("readyz", () => health.GetReadinessAsync()),
            ("restoreBackup", () => db.RestoreBackupAsync(EntityId)),
            ("restorePointInTime", () => db.RestorePointInTimeAsync(DateTimeOffset.UnixEpoch)),
            ("executeSql", () => sql.ExecuteAsync("SELECT 1")),
            ("setBackupDestination", () => project.SetBackupDestinationAsync(new("https://s3.example.test", "region", "bucket", "fake-id", "fake-secret", "both"))),
            ("setBackupPolicy", () => db.SetBackupPolicyAsync(null, 7)),
            ("status", () => health.GetHealthAsync()),
            ("updateDatabase", () => project.RenameDatabaseAsync(EntityId, "production")),
            ("updateSavedQuery", () => project.UpdateSavedQueryAsync(EntityId, "recent", "SELECT * FROM items LIMIT 1")),
        ];
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (operation, call) in calls)
        {
            sent = null;
            requests = 0;
            var refusal = await Assert.ThrowsAsync<TerraBaseException>(call);
            await Assert.That(refusal!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(requests).IsEqualTo(1);
            if (sent is not { } wire) throw new InvalidOperationException(operation + " sent no request");
            var route = expected[operation];
            await Assert.That(wire.Method).IsEqualTo(route.GetProperty("method").GetString());
            var pattern = "^" + Regex.Replace(Regex.Escape(route.GetProperty("path").GetString()!), @"\\\{[a-z]+}", "[^/]+") + "$";
            await Assert.That(Regex.IsMatch(wire.Path, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).IsTrue().Because(operation);
            await Assert.That(used.Add(operation)).IsTrue();
        }
        await Assert.That(expected.Keys.Except(used)).IsEmpty();
    }

}
