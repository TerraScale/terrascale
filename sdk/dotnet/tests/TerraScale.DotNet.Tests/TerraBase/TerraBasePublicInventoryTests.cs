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
    public async Task Sdk_calls_match_the_public_route_inventory()
    {
        using var inventory = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("current-public-routes.json"));
        await Assert.That(inventory.RootElement.GetProperty("serverCommit").GetString()).IsEqualTo("d95bbb36b55acfdf90ca0232cea7a80daaf07dd5");
        var expected = inventory.RootElement.GetProperty("operations").EnumerateArray().ToDictionary(r => r.GetProperty("operation").GetString()!, r => r);
        await Assert.That(expected.Count).IsEqualTo(62);
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
        var regions = new TerraBaseRegionsClient(http);
        (string Operation, Func<Task> Call)[] calls =
        [
            ("backupz", () => health.GetBackupHealthAsync()),
            ("branchDatabase", () => db.BranchAsync("copy")),
            ("branchDatabaseIfId", () => db.BranchIfIdAsync("copy", EntityId)),
            ("createBackup", () => db.CreateBackupAsync()),
            ("deleteDatabaseIfId", () => db.DeleteIfIdAsync(EntityId)),
            ("invokeFunction", () => db.InvokeFunctionAsync("hello")),
            ("listDatabaseLogs", () => db.ListRequestLogsAsync()),
            ("listFunctionLogs", () => db.ListFunctionLogsAsync("hello")),
            ("listFunctions", () => db.ListFunctionsAsync()),
            ("listRegions", () => regions.ListRegionsAsync()),
            ("deployFunction", () => db.DeployFunctionAsync("hello", new("function handler(event) { return event; }"))),
            ("getFunction", () => db.GetFunctionAsync("hello")),
            ("deleteFunction", () => db.DeleteFunctionAsync("hello")),
            ("createDatabase", () => project.CreateDatabaseAsync("app")),
            ("deleteBackupDestination", () => project.DeleteBackupDestinationAsync()),
            ("deleteDatabase", () => project.DeleteDatabaseAsync("app")),
            ("getBackupDestination", () => project.GetBackupDestinationAsync()),
            ("getBackupPolicy", () => db.GetBackupPolicyAsync()),
            ("getDatabase", () => project.GetDatabaseAsync("app")),
            ("getDatabaseAnalytics", () => db.GetAnalyticsAsync()),
            ("getDatabaseUsage", () => db.GetUsageAsync()),
            ("getProjectAnalytics", () => project.GetAnalyticsAsync()),
            ("getProjectMetrics", () => project.GetPrometheusMetricsAsync()),
            ("getProjectUsage", () => project.GetUsageAsync()),
            ("getRestoreWindow", () => db.GetRestoreWindowAsync()),
            ("getRestoreOperation", () => db.GetRestoreOperationAsync(EntityId)),
            ("listBackups", () => db.ListBackupsAsync()),
            ("listDatabases", () => project.ListDatabasesAsync()),
            ("listPlatformDatabases", () => platform.ListDatabasesAsync()),
            ("planPointInTime", () => db.PlanPointInTimeAsync(DateTimeOffset.UnixEpoch)),
            ("readyz", () => health.GetReadinessAsync()),
            ("restoreBackup", () => db.RestoreBackupAsync(EntityId)),
            ("restorePointInTime", () => db.RestorePointInTimeAsync(DateTimeOffset.UnixEpoch)),
            ("rewindPointInTime", () => db.RewindPointInTimeAsync(DateTimeOffset.UnixEpoch)),
            ("executeSql", () => sql.ExecuteAsync("SELECT 1")),
            ("exportDatabase", () => sql.ExportDatabaseAsync()),
            ("setBackupDestination", () => project.SetBackupDestinationAsync(new("https://s3.example.test", "region", "bucket", "fake-id", "fake-secret", "both"))),
            ("setBackupPolicy", () => db.SetBackupPolicyAsync(null, 7)),
            ("healthz", () => health.GetHealthAsync()),
            ("updateDatabase", () => project.RenameDatabaseAsync(EntityId, "production")),
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
            var pattern = "^" + Regex.Replace(Regex.Escape(route.GetProperty("path").GetString()!), @"\\\{[a-zA-Z]+}", "[^/]+") + "$";
            await Assert.That(Regex.IsMatch(wire.Path, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).IsTrue().Because(operation);
            await Assert.That(used.Add(operation)).IsTrue();
        }
        await Assert.That(used.Count).IsEqualTo(calls.Length);
    }

}
