using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupPlatformProgressTests
{
    [Test]
    [Arguments("platform")]
    [Arguments("both")]
    public async Task Acknowledged_platform_progress_previews_an_early_time_without_scanning_over_ten_thousand_later_segments(string mode)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(s3, time);
        var database = await api.CreateManagedDatabaseAsync();
        if (mode == "both")
        {
            await SetDestinationAsync(api, database, mode, token);
        }

        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var snapshotAt = time.GetUtcNow();
        using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "last")), new { text = new Faker().Lorem.Sentence() }, token);
        written.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(11));
        await backup.RunOnceAsync(token);
        var shippedAt = time.GetUtcNow();
        var generation = BackupLayout.ParseSnapshot(s3.Objects.Keys.Single(key => key.StartsWith(BackupLayout.Snapshots(File(database)), StringComparison.Ordinal)))!.Generation;
        SeedLaterSegments(s3, database, generation, snapshotAt, customer: mode == "both");
        s3.ObservedListPrefix = BackupLayout.Segments(File(database), generation);

        using var window = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time?destination=platform"), token);
        window.EnsureSuccessStatusCode();
        var bounds = await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
        await Assert.That(bounds.GetProperty("latest").GetDateTimeOffset()).IsEqualTo(shippedAt);
        await Assert.That(s3.ListRequests).IsEqualTo(0);
        using var preview = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"), new { pointInTime = snapshotAt, destination = "platform" }, token);
        preview.EnsureSuccessStatusCode();
        var plan = await preview.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
        await Assert.That(plan.GetProperty("window").GetProperty("latest").GetDateTimeOffset()).IsEqualTo(shippedAt);
        await Assert.That(plan.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(snapshotAt);
        await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(0);
        await Assert.That(s3.ListRequests).IsEqualTo(1);
        if (mode == "both")
        {
            // Customer previews retain their metadata budget; platform acknowledgement cannot certify that source.
            await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"),
                new { pointInTime = snapshotAt, destination = "customer" }, token), HttpStatusCode.ServiceUnavailable,
                "pitr_plan_limit", "the point-in-time plan metadata scan exceeds 10000 objects");
        }

        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Customer_only_or_a_different_mirrored_generation_cannot_supply_platform_progress(bool differentGeneration)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(s3, time);
        var database = await api.CreateManagedDatabaseAsync();
        await SetDestinationAsync(api, database, differentGeneration ? "both" : "customer", token);
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var snapshotAt = time.GetUtcNow();
        var generation = differentGeneration ? Ulid.NewUlid() : BackupLayout.ParseSnapshot(s3.CustomerObjects.Keys.Single(
            key => key.StartsWith(BackupLayout.Snapshots(File(database)), StringComparison.Ordinal)))!.Generation;
        var newestAt = differentGeneration ? snapshotAt.AddMilliseconds(1) : snapshotAt;
        // Only metadata is fabricated: this test exercises the listener's source/generation selection, without imports.
        s3.SeedObject(customer: false, BackupLayout.Snapshot(File(database), newestAt, generation, 0), []);
        SeedLaterSegments(s3, database, generation, newestAt, customer: false);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"),
            new { pointInTime = snapshotAt, destination = "platform" }, token), HttpStatusCode.ServiceUnavailable,
            "pitr_plan_limit", "the point-in-time plan metadata scan exceeds 10000 objects");
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    private static async Task<ManagementApi> StartAsync(FakeS3 s3, FakeTimeProvider time)
    {
        var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        return api;
    }

    private static async Task SetDestinationAsync(ManagementApi api, ManagedDatabaseRef database, string mode, CancellationToken token)
    {
        using var response = await api.Client.PutAsJsonAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), new
        {
            endpoint = FakeS3.CustomerEndpoint,
            region = "test-region-1",
            bucket = FakeS3.CustomerBucket,
            accessKeyId = FakeS3.CustomerAccessKey,
            secretKey = FakeS3.CustomerSecretKey,
            mode,
        }, token);
        response.EnsureSuccessStatusCode();
    }

    private static void SeedLaterSegments(FakeS3 s3, ManagedDatabaseRef database, Ulid generation, DateTimeOffset at, bool customer)
    {
        for (var index = 1; index <= 10_001; index++)
        {
            var key = BackupLayout.Segment(File(database), generation, index, at.AddMilliseconds(index));
            s3.SeedObject(customer: false, key, []);
            if (customer)
            {
                s3.SeedObject(customer: true, key, []);
            }
        }
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";
}
