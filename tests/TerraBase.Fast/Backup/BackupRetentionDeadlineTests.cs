using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupRetentionDeadlineTests
{
    [Test]
    public async Task A_stale_baseline_and_orphan_wal_expire_at_the_inclusive_hourly_lookahead_boundary()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3, time);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var prefix = $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";
        var baseline = s3.Objects.Keys.Single(key => key.StartsWith(prefix + "/snapshots/", StringComparison.Ordinal));
        var at = time.GetUtcNow();
        s3.FailPut = key => key.StartsWith(prefix + "/snapshots/", StringComparison.Ordinal);
        using var write = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "after")), new { text = "retained" }, token);
        write.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromHours(1));
        await backup.RunOnceAsync(token);
        var segment = s3.Objects.Keys.Single(key => key.StartsWith(prefix + "/wal/", StringComparison.Ordinal));
        var orphan = BackupLayout.Segment(prefix, Ulid.NewUlid(), 0, at);
        s3.SeedObject(customer: false, orphan, []);

        time.Advance(TimeSpan.FromHours(45));
        using var recent = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "recent")), new { text = "restorable" }, token);
        recent.EnsureSuccessStatusCode();
        await backup.RunOnceAsync(token);
        await Assert.That(s3.Objects.ContainsKey(baseline)).IsTrue();
        await Assert.That(s3.Objects.ContainsKey(orphan)).IsTrue();
        using var before = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time"), token);
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var restored = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = time.GetUtcNow(), name = "before-expiry" }, token);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);

        time.Advance(TimeSpan.FromHours(1));
        await backup.RunOnceAsync(token);
        await Assert.That(s3.Objects.ContainsKey(baseline)).IsFalse();
        await Assert.That(s3.Objects.ContainsKey(orphan)).IsFalse();
        await Assert.That(s3.Objects.ContainsKey(segment)).IsTrue();
        using var expired = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time"), token);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var unavailable = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = at.AddHours(46), name = "after-expiry" }, token);
        await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        time.Advance(TimeSpan.FromHours(1));
        await backup.RunOnceAsync(token);
        await Assert.That(s3.Objects.ContainsKey(segment)).IsFalse();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(prefix + "/snapshots/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_denied_baseline_or_wal_expiry_preserves_measurement_and_retries(bool lockWal)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3, time);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var prefix = $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";
        var baseline = s3.Objects.Keys.Single(key => key.StartsWith(prefix + "/snapshots/", StringComparison.Ordinal));
        s3.FailPut = key => key.StartsWith(prefix + "/snapshots/", StringComparison.Ordinal);
        using var write = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "after")), new { text = "retained" }, token);
        write.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var segment = s3.Objects.Keys.Single(key => key.StartsWith(prefix + "/wal/", StringComparison.Ordinal));
        var initial = await RecordAsync(api, database, token);
        s3.Locked = key => key == (lockWal ? segment : baseline);
        time.Advance(TimeSpan.FromDays(3));
        await backup.RunOnceAsync(token);
        var denied = await RecordAsync(api, database, token);
        await Assert.That(s3.Objects.ContainsKey(lockWal ? segment : baseline)).IsTrue();
        await Assert.That(denied.SizedAt).IsEqualTo(initial.SizedAt);
        await Assert.That(denied.SizeBytes).IsEqualTo(initial.SizeBytes);

        s3.Locked = _ => false;
        time.Advance(TimeSpan.FromHours(1));
        await backup.RunOnceAsync(token);
        var recovered = await RecordAsync(api, database, token);
        await Assert.That(s3.Objects.ContainsKey(baseline)).IsFalse();
        await Assert.That(s3.Objects.ContainsKey(segment)).IsFalse();
        await Assert.That(recovered.SizedAt!.Value).IsGreaterThan(initial.SizedAt!.Value);
        using var window = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time"), token);
        await Assert.That(window.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("retention")]
    [Arguments("transport")]
    [Arguments("timeout")]
    public async Task A_failed_early_wal_delete_does_not_starve_later_batches_or_snapshots(string failure)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var s3 = new FakeS3();
        await using var api = await StartAsync(s3, time);
        var database = await api.CreateManagedDatabaseAsync();
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var prefix = $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";
        var baseline = s3.Objects.Keys.Single(key => key.StartsWith(prefix + "/snapshots/", StringComparison.Ordinal));
        var at = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var initial = await RecordAsync(api, database, token);
        await Assert.That(initial.SizeBytes).IsNotNull();
        var generation = Ulid.NewUlid();
        var segments = Enumerable.Range(0, 33).Select(index => BackupLayout.Segment(prefix, generation, index, at)).ToArray();
        foreach (var segment in segments)
        {
            s3.SeedObject(customer: false, segment, [1]);
        }

        s3.FailPut = key => key.StartsWith(prefix + "/snapshots/", StringComparison.Ordinal);
        s3.Locked = key => failure == "retention" && key == segments[0];
        s3.ObjectRequested = (_, method, key) =>
        {
            if (method == HttpMethod.Delete && key == segments[0])
            {
                if (failure == "transport")
                {
                    throw new HttpRequestException("simulated object deletion transport failure");
                }

                if (failure == "timeout")
                {
                    throw new OperationCanceledException("simulated object deletion timeout");
                }
            }
        };

        time.Advance(TimeSpan.FromDays(3));
        await backup.RunOnceAsync(token);
        var incomplete = await RecordAsync(api, database, token);
        await Assert.That(s3.Objects.ContainsKey(segments[0])).IsTrue();
        await Assert.That(segments.Skip(1).Any(s3.Objects.ContainsKey)).IsFalse();
        await Assert.That(s3.Objects.ContainsKey(baseline)).IsFalse();
        await Assert.That(incomplete.SizedAt).IsEqualTo(initial.SizedAt);
        await Assert.That(incomplete.SizeBytes).IsEqualTo(initial.SizeBytes);

        s3.Locked = _ => false;
        s3.ObjectRequested = null;
        time.Advance(TimeSpan.FromHours(1));
        await backup.RunOnceAsync(token);
        var recovered = await RecordAsync(api, database, token);
        await Assert.That(s3.Objects.ContainsKey(segments[0])).IsFalse();
        await Assert.That(recovered.SizedAt!.Value).IsGreaterThan(initial.SizedAt!.Value);
    }

    private static Task<ManagementApi> StartAsync(FakeS3 s3, FakeTimeProvider time) =>
        ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);

    private static async Task<BackupStatusRecord> RecordAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken token) =>
        (await api.Server.Services.GetRequiredService<SystemCatalog>().ReadAsync(transaction => SystemCatalog.GetAsync(transaction,
            "databases/" + database.Id + "/backup_status", "current", TerraBaseJsonContext.Default.BackupStatusRecord, token),
            static error => throw new InvalidOperationException(error.Message), token))!;
}
