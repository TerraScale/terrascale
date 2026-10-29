using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class RestoreWindowTests
{
    [Test]
    public async Task An_unshipped_write_is_rejected_and_exact_latest_restores_the_last_stored_data()
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var stored = new Faker().Lorem.Sentence(12);
        using var seed = await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "state")), new { text = stored });
        seed.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var shipped = time.GetUtcNow();
        // Keep the application running while the bucket refuses additional uploads.
        s3.FailPut = _ => true;

        time.Advance(TimeSpan.FromSeconds(5));
        var unshipped = new Faker().Lorem.Sentence(20);
        using var write = await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "state")), new { text = unshipped });
        write.EnsureSuccessStatusCode();
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        using var window = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time"));
        window.EnsureSuccessStatusCode();
        var bounds = await window.Content.ReadFromJsonAsync<JsonElement>();
        var earliest = bounds.GetProperty("earliest").GetDateTimeOffset();
        var latest = bounds.GetProperty("latest").GetDateTimeOffset();
        await Assert.That(earliest).IsLessThanOrEqualTo(latest);
        await Assert.That(latest).IsEqualTo(shipped);
        await Assert.That(latest).IsLessThan(time.GetUtcNow());

        using var now = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), name = "main-unshipped" });
        using var afterLatest = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = latest.AddTicks(1), name = "main-after" });
        // Not in the future: the backup does not hold these times yet, a retryable 409.
        await Assert.That(now.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(afterLatest.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var databases = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.ProjectPath}/databases"));
        await Assert.That(databases.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([database.Id]);

        using var restored = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = latest, name = "main-latest" });
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var read = await data.GetAsync(Uri(ManagementApi.DocumentPath(database with { Name = "main-latest" }, "people", "state")));
        read.EnsureSuccessStatusCode();
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(stored);
        using var live = await data.GetAsync(Uri(ManagementApi.DocumentPath(database, "people", "state")));
        live.EnsureSuccessStatusCode();
        await Assert.That((await live.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(unshipped);
    }

    [Test]
    public async Task Exact_earliest_restores_before_later_changes_even_when_metadata_discovery_advances_time()
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var initial = new Faker().Lorem.Sentence(12);
        using var seed = await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "state")), new { text = initial });
        seed.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var snapshotted = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(5));
        using var write = await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "state")), new { text = new Faker().Lorem.Sentence(20) });
        write.EnsureSuccessStatusCode();
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        s3.FailPut = _ => true;

        using var window = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time"));
        window.EnsureSuccessStatusCode();
        var bounds = await window.Content.ReadFromJsonAsync<JsonElement>();
        var earliest = bounds.GetProperty("earliest").GetDateTimeOffset();
        var latest = bounds.GetProperty("latest").GetDateTimeOffset();
        await Assert.That(earliest).IsEqualTo(snapshotted);
        await Assert.That(earliest).IsLessThan(latest);
        await Assert.That(latest).IsEqualTo(time.GetUtcNow());

        using var beforeEarliest = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = earliest.AddTicks(-1), name = "main-before" });
        // Within the plan's retention window, before the oldest stored snapshot: the backup does not hold it (409).
        await Assert.That(beforeEarliest.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var databases = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.ProjectPath}/databases"));
        await Assert.That(databases.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([database.Id]);

        using var restored = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = earliest, name = "main-earliest" });
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var read = await data.GetAsync(Uri(ManagementApi.DocumentPath(database with { Name = "main-earliest" }, "people", "state")));
        read.EnsureSuccessStatusCode();
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(initial);

        // The plan clips earliest to a time between the retained snapshot and its later segment.
        time.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(2));
        var floor = time.GetUtcNow() - TimeSpan.FromDays(1);
        using var retainedWindow = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time"));
        retainedWindow.EnsureSuccessStatusCode();
        var retainedBounds = await retainedWindow.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(retainedBounds.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(floor);
        await Assert.That(floor).IsGreaterThan(snapshotted).And.IsLessThan(latest);
        using var outsidePlan = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = floor.AddTicks(-1), name = "main-outside-plan" });
        await Assert.That(outsidePlan.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // Discovery crosses the retention boundary; restore must use its clock captured before the snapshot LIST.
        s3.ObservedListPrefix = BackupLayout.Snapshots(backup.Status.Files.Single(file => file.File == File(database)).File);
        var lists = s3.ListRequests;
        s3.BeforeList = (prefix, _) =>
        {
            if (prefix == s3.ObservedListPrefix)
            {
                time.Advance(TimeSpan.FromSeconds(1));
            }

            return Task.CompletedTask;
        };
        using var retainedRestore = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = floor, name = "main-retained" });
        s3.BeforeList = null;
        await Assert.That(retainedRestore.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(s3.ListRequests).IsEqualTo(lists + 1);
        await Assert.That(floor).IsLessThan(time.GetUtcNow() - TimeSpan.FromDays(1));
        using var retainedRead = await data.GetAsync(Uri(ManagementApi.DocumentPath(database with { Name = "main-retained" }, "people", "state")));
        retainedRead.EnsureSuccessStatusCode();
        await Assert.That((await retainedRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(initial);
    }

    [Test]
    public async Task Locked_objects_do_not_extend_the_plans_restore_window_after_stored_data_expires()
    {
        var s3 = new FakeS3 { Locked = _ => true };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var seed = await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "state")), new { text = new Faker().Lorem.Sentence(12) });
        seed.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var snapshotted = time.GetUtcNow();
        var prefix = backup.Status.Files.Single(file => file.File == File(database)).File + "/";
        s3.FailPut = _ => true;
        var retained = s3.Objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        await Assert.That(retained.Length).IsGreaterThan(0);

        // Hobby retains one day. Equality with the retention floor still includes the stored endpoint.
        time.Advance(TimeSpan.FromDays(1));
        using var boundary = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time"));
        boundary.EnsureSuccessStatusCode();
        var bounds = await boundary.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(bounds.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(snapshotted);
        await Assert.That(bounds.GetProperty("latest").GetDateTimeOffset()).IsEqualTo(snapshotted);

        time.Advance(TimeSpan.FromSeconds(1));
        using var expired = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time"));
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = snapshotted, name = "main-expired" }),
            HttpStatusCode.NotFound, "not_found", "continuous backup holds nothing of this database within its plan's retention window");
        await Assert.That(s3.Objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))).IsEquivalentTo(retained);
        var databases = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.ProjectPath}/databases"));
        await Assert.That(databases.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([database.Id]);
    }

    private static void ManualShipping(FakeS3 s3, IServiceCollection services)
    {
        s3.Route(services);
        ManualBackupHosting.Configure(services);
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
