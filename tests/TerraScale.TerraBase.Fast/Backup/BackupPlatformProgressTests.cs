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
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(oci, time);
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
        var generation = BackupLayout.ParseSnapshot(oci.Objects.Keys.Single(key => key.StartsWith(BackupLayout.Snapshots(File(database)), StringComparison.Ordinal)))!.Generation;
        SeedLaterSegments(oci, database, generation, snapshotAt, customer: mode == "both");
        oci.ObservedListPrefix = BackupLayout.Segments(File(database), generation);

        using var window = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time?destination=platform"), token);
        window.EnsureSuccessStatusCode();
        var bounds = await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
        await Assert.That(bounds.GetProperty("latest").GetDateTimeOffset()).IsEqualTo(shippedAt);
        await Assert.That(oci.ListRequests).IsEqualTo(0);
        using var preview = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"), new { pointInTime = snapshotAt, destination = "platform" }, token);
        preview.EnsureSuccessStatusCode();
        var plan = await preview.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
        await Assert.That(plan.GetProperty("window").GetProperty("latest").GetDateTimeOffset()).IsEqualTo(shippedAt);
        await Assert.That(plan.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(snapshotAt);
        await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(0);
        await Assert.That(oci.ListRequests).IsEqualTo(1);
        if (mode == "both")
        {
            // Customer previews retain their metadata budget; platform acknowledgement cannot certify that source.
            await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"),
                new { pointInTime = snapshotAt, destination = "customer" }, token), HttpStatusCode.ServiceUnavailable,
                "pitr_plan_limit", "the point-in-time plan metadata scan exceeds 10000 objects");
        }

        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Customer_only_or_a_different_mirrored_generation_cannot_supply_platform_progress(bool differentGeneration)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(oci, time);
        var database = await api.CreateManagedDatabaseAsync();
        await SetDestinationAsync(api, database, differentGeneration ? "both" : "customer", token);
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var snapshotAt = time.GetUtcNow();
        var generation = differentGeneration ? Ulid.NewUlid() : BackupLayout.ParseSnapshot(oci.CustomerObjects.Keys.Single(
            key => key.StartsWith(BackupLayout.Snapshots(File(database)), StringComparison.Ordinal)))!.Generation;
        var newestAt = differentGeneration ? snapshotAt.AddMilliseconds(1) : snapshotAt;
        // Only metadata is fabricated: this test exercises the listener's source/generation selection, without imports.
        oci.SeedObject(customer: false, BackupLayout.Snapshot(File(database), newestAt, generation, 0), []);
        SeedLaterSegments(oci, database, generation, newestAt, customer: false);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"),
            new { pointInTime = snapshotAt, destination = "platform" }, token), HttpStatusCode.ServiceUnavailable,
            "pitr_plan_limit", "the point-in-time plan metadata scan exceeds 10000 objects");
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    private static async Task<ManagementApi> StartAsync(FakeOci oci, FakeTimeProvider time)
    {
        var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services =>
        {
            oci.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        return api;
    }

    private static async Task SetDestinationAsync(ManagementApi api, ManagedDatabaseRef database, string mode, CancellationToken token)
    {
        using var response = await api.Client.PutAsJsonAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), new
        {
            endpoint = FakeOci.CustomerEndpoint,
            region = "test-region-1",
            bucket = FakeOci.CustomerBucket,
            accessKeyId = FakeOci.CustomerAccessKey,
            secretKey = FakeOci.CustomerSecretKey,
            mode,
        }, token);
        response.EnsureSuccessStatusCode();
    }

    private static void SeedLaterSegments(FakeOci oci, ManagedDatabaseRef database, Ulid generation, DateTimeOffset at, bool customer)
    {
        for (var index = 1; index <= 10_001; index++)
        {
            var key = BackupLayout.Segment(File(database), generation, index, at.AddMilliseconds(index));
            oci.SeedObject(customer: false, key, []);
            if (customer)
            {
                oci.SeedObject(customer: true, key, []);
            }
        }
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";
}
