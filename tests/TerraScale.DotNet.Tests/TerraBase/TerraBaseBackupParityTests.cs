using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseBackupParityTests
{
    private const string BackupId = "01JA0000000000000000000003";
    private const string Backup = """{"id":"01JA0000000000000000000003","databaseId":"01JA0000000000000000000002","status":"ready","trigger":"manual","sizeBytes":9007199254740993,"createdAt":"2026-10-22T00:00:00Z"}""";
    private const string Policy = """{"fullBackupCron":null,"retentionDays":7,"disabled":false,"updatedAt":null}""";
    private const string Window = """{"earliest":"2026-10-21T00:00:00Z","latest":"2026-10-22T00:00:00Z"}""";
    private const string Restored = """{"id":"01JA0000000000000000000004","projectId":"01JA0000000000000000000001","name":"copy","branchedFrom":"01JA0000000000000000000002","createdAt":"2026-10-22T00:00:00Z","homeRegion":null,"replicaRegions":null,"placementVersion":0,"replicas":[]}""";

    [Test]
    public async Task Backup_snapshot_policy_and_restore_use_producer_response_types()
    {
        var calls = new List<(string Method, string Path, string Query, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            var path = request.RequestUri.AbsolutePath;
            var body = path.EndsWith("/restore", StringComparison.Ordinal) ? Restored
                : path.EndsWith("/backup-policy", StringComparison.Ordinal) ? Policy
                : request.Method == HttpMethod.Get ? "{\"items\":[" + Backup + "],\"next\":\"opaque-cursor\"}" : Backup;
            return TerraBaseIntegrationFixture.Json(body, request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var backup = await db.CreateBackupAsync();
        var page = await db.ListBackupsAsync("opaque/cursor+", 2);
        var policy = await db.GetBackupPolicyAsync();
        await db.SetBackupPolicyAsync(null, 7);
        var restored = await db.RestoreBackupAsync(BackupId);
        await Assert.That(backup.SizeBytes).IsEqualTo(9007199254740993L);
        await Assert.That(page.Next).IsEqualTo("opaque-cursor");
        await Assert.That(policy.FullBackupCron).IsNull();
        await Assert.That(policy.UpdatedAt).IsNull();
        await Assert.That(restored.Name).IsEqualTo("copy");
        await Assert.That(restored.HomeRegion).IsNull();
        await Assert.That(restored.Replicas).IsEmpty();
        await Assert.That(calls[0].Path).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/backups");
        await Assert.That(calls[1].Query).IsEqualTo("?after=opaque%2Fcursor%2B&limit=2");
        await Assert.That(calls[3].Body).IsEqualTo("{\"retentionDays\":7,\"disabled\":false}");
        await Assert.That(calls[4].Path).EndsWith($"/backups/{BackupId}/restore");
        await Assert.That(calls[4].Body).IsEqualTo("{}");
        await Assert.That(calls.Select(c => c.Method)).IsEquivalentTo(["POST", "GET", "GET", "PUT", "POST"]);
    }

    [Test]
    public async Task Point_in_time_source_selection_and_metadata_remain_explicit()
    {
        var calls = new List<(string Method, string Path, string Query, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return request.Method == HttpMethod.Get ? TerraBaseIntegrationFixture.Json(Window)
                : request.RequestUri.AbsolutePath.EndsWith("/plan", StringComparison.Ordinal)
                    ? TerraBaseIntegrationFixture.Json("{\"window\":" + Window + ",\"pointInTime\":\"2026-10-21T12:00:00Z\",\"snapshotAt\":\"2026-10-21T00:00:00Z\",\"latestSegmentAt\":null,\"segmentCount\":0,\"historyCheck\":\"metadata\"}")
                    : TerraBaseIntegrationFixture.Json(Restored, HttpStatusCode.Created);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var at = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
        var window = await db.GetRestoreWindowAsync("customer");
        var plan = await db.PlanPointInTimeAsync(at, "customer");
        await db.RestorePointInTimeAsync(at, "copy", "customer");
        await Assert.That(window.Latest).IsEqualTo(at.AddHours(12));
        await Assert.That(plan.HistoryCheck).IsEqualTo("metadata");
        await Assert.That(plan.LatestSegmentAt).IsNull();
        await Assert.That(calls[0].Query).IsEqualTo("?destination=customer");
        await Assert.That(calls[1].Path).EndsWith("/point-in-time/plan");
        await Assert.That(calls[2].Path).EndsWith("/point-in-time/restore");
        using var body = JsonDocument.Parse(calls[2].Body!);
        await Assert.That(body.RootElement.GetProperty("pointInTime").GetDateTimeOffset()).IsEqualTo(at);
        await Assert.That(body.RootElement.GetProperty("destination").GetString()).IsEqualTo("customer");
        await Assert.That(body.RootElement.GetProperty("name").GetString()).IsEqualTo("copy");
    }

    [Test]
    [Arguments(404, "not_found")]
    [Arguments(409, "backup_history_incomplete")]
    [Arguments(409, "restore_point_unproven")]
    [Arguments(429, "quota_exceeded")]
    [Arguments(503, "storage_unavailable")]
    public async Task Restore_errors_remain_explicit_without_retry(int status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", (HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http)
            .Database(TerraBaseIntegrationFixture.ProjectId, "app").RestorePointInTimeAsync(DateTimeOffset.UtcNow));
        await Assert.That(error!.ErrorCode).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
    }
}
