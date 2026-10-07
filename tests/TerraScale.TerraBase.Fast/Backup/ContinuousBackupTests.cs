using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class ContinuousBackupTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_database_restores_as_of_a_chosen_second_from_its_encrypted_continuous_backup()
    {
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(oci.Configure, oci.Route, time);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var before = Faker.Lorem.Sentence(12);
        var after = Faker.Lorem.Sentence(12);
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "before")), new { text = before })).EnsureSuccessStatusCode();

        // Discovery runs every ten seconds: the pass after it snapshots every file, starting its generation.
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(CancellationToken.None);
        var snapshotted = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(5));
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "after")), new { text = after })).EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(CancellationToken.None);
        var shipped = time.GetUtcNow();

        using var window = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time"));
        var bounds = await window.Content.ReadFromJsonAsync<JsonElement>();
        using var earlier = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = snapshotted.AddSeconds(3) });
        var earlierDatabase = await earlier.Content.ReadFromJsonAsync<JsonElement>();
        using var latest = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = shipped, name = "main-latest" });
        using var tooEarly = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = snapshotted.AddMinutes(-1) });
        using var future = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = shipped.AddMinutes(1) });
        using var missing = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { name = "main-missing" });
        var earlierName = earlierDatabase.GetProperty("name").GetString();
        using var earlierBefore = await data.GetAsync(Uri(DocumentPath(database, earlierName!, "before")));
        using var earlierAfter = await data.GetAsync(Uri(DocumentPath(database, earlierName!, "after")));
        using var latestAfter = await data.GetAsync(Uri(DocumentPath(database, "main-latest", "after")));
        using var status = JsonDocument.Parse(await api.Server.Client.GetStringAsync(Uri("/v1/backup")));
        var metrics = await api.Server.Client.GetStringAsync(Uri("/metrics"));

        await Assert.That(window.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(bounds.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(snapshotted);
        await Assert.That(bounds.GetProperty("latest").GetDateTimeOffset()).IsEqualTo(shipped);
        await Assert.That(earlier.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(earlierName).IsEqualTo("main-restore");
        await Assert.That(earlierDatabase.GetProperty("branchedFrom").GetString()).IsEqualTo(database.Id);
        await Assert.That((await earlierBefore.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(before);
        await Assert.That(earlierAfter.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(latest.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await latestAfter.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(after);
        await Assert.That(tooEarly.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(future.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // Every file of the node ships, the system database included, and nothing waits.
        var files = status.RootElement.GetProperty("files").EnumerateArray().ToList();
        await Assert.That(status.RootElement.GetProperty("shipping").GetBoolean()).IsTrue();
        await Assert.That(files.Select(file => file.GetProperty("file").GetString())).Contains("v1/_system");
        await Assert.That(files.Select(file => file.GetProperty("file").GetString())).Contains($"v1/{database.Organization}/{database.Project}/{database.Id}");
        await Assert.That(files.All(file => file.GetProperty("generation").GetString() is not null && file.GetProperty("lagSeconds").GetDouble() == 0)).IsTrue();

        foreach (var metric in new[] { "terrabase_backup_lag_seconds", "terrabase_backup_last_success_seconds", "terrabase_backup_uploaded_bytes_total", "terrabase_backup_files" })
        {
            await Assert.That(metrics).Contains(metric);
        }

        // The bucket holds sealed objects only, under keys from the vault, which saw only signed requests.
        var plain = Encoding.UTF8.GetBytes(after);
        await Assert.That(oci.Objects.Keys.Any(key => key.Contains("/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(oci.Objects.Keys.Any(key => key.Contains("/wal/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(oci.Objects.Values.Any(content => content.AsSpan().IndexOf(plain) >= 0)).IsFalse();
        await Assert.That(oci.Objects.Values.All(content => content.AsSpan().StartsWith("TBK1"u8) || content.AsSpan().StartsWith("wrapped:"u8))).IsTrue();
        await Assert.That(oci.GeneratedKeys).IsGreaterThan(0);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task The_shipper_checkpoints_the_log_and_keeps_one_generation_across_its_restarts()
    {
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(oci.Configure, oci.Route, time);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(CancellationToken.None);
        var generation = Generation(backup, database);

        // Over a thousand pages: the next read checkpoints, so the write after it restarts the log.
        var texts = Enumerable.Range(0, 6).Select(_ => new string(Faker.Random.Chars('a', 'z', 900_000))).ToList();
        for (var i = 0; i < 5; i++)
        {
            (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", $"large-{i}")), new { text = texts[i] })).EnsureSuccessStatusCode();
        }

        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(CancellationToken.None);
        var log = new FileInfo(Path.Combine(api.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db-wal")).Length;
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "large-5")), new { text = texts[5] })).EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(CancellationToken.None);
        var shipped = time.GetUtcNow();

        using var restored = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = shipped, name = "main-latest" });
        var documents = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            using var read = await data.GetAsync(Uri(DocumentPath(database, "main-latest", $"large-{i}")));
            documents.Add((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()!);
        }

        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(documents).IsEquivalentTo(texts);
        await Assert.That(Generation(backup, database)).IsEqualTo(generation);
        // The last write went to the start of a restarted log, so the file did not grow past the checkpointed size.
        await Assert.That(new FileInfo(Path.Combine(api.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db-wal")).Length).IsEqualTo(log);
    }

    [Test]
    public async Task A_log_checkpointed_by_another_process_ends_the_generation_and_loses_nothing()
    {
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(oci.Configure, oci.Route, time);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(CancellationToken.None);
        var generation = Generation(backup, database);
        var text = Faker.Lorem.Sentence(12);
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "outside")), new { text })).EnsureSuccessStatusCode();

        // Another process copies the log back and truncates it before the shipper read the write.
        var file = Path.Combine(api.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db");
        await using (var outside = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            await outside.OpenAsync();
            await using var checkpoint = outside.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(CancellationToken.None);
        using var restored = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), name = "main-latest" });
        using var read = await data.GetAsync(Uri(DocumentPath(database, "main-latest", "outside")));
        var metrics = await api.Server.Client.GetStringAsync(Uri("/metrics"));

        await Assert.That(Generation(backup, database)).IsNotNull().And.IsNotEqualTo(generation);
        await Assert.That(metrics).Contains("terrabase_backup_holes_total");
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
    }

    [Test]
    public async Task A_shipper_cut_off_from_nats_stops_before_its_lease_expires_and_another_member_takes_over()
    {
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var leases = new FakeLeases(time);
        var database = new TenantContext(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var file = $"v1/{database.OrganizationId}/{database.ProjectId}/{database.DatabaseId}";
        await using var a = await MemberAsync("member-a");
        await using var b = await MemberAsync("member-b");
        var members = new Dictionary<string, TerraBaseTestServer>(StringComparer.Ordinal) { ["member-a"] = a, ["member-b"] = b };
        foreach (var member in members.Values)
        {
            await member.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(database, CancellationToken.None);
        }

        time.Advance(TimeSpan.FromSeconds(10));
        await PassAsync();
        var holder = leases.Holders()[file.Replace('/', '.')];
        var other = holder == "member-a" ? "member-b" : "member-a";
        var first = Status(members[holder], file);
        var otherFirst = Status(members[other], file);

        // Cut the holder off: it stops shipping once its lease could be stale, before the lease expires for the others.
        leases.Partition(holder);
        time.Advance(TimeSpan.FromSeconds(11));
        await PassAsync();
        var stopped = Status(members[holder], file);
        var waiting = Status(members[other], file);
        time.Advance(TimeSpan.FromSeconds(5));
        await PassAsync();
        var taken = Status(members[other], file);

        await Assert.That(first.Shipping).IsTrue();
        await Assert.That(first.Generation).IsNotNull();
        await Assert.That(otherFirst.Shipping).IsFalse();
        await Assert.That(stopped.Shipping).IsFalse();
        await Assert.That(waiting.Shipping).IsFalse();
        await Assert.That(taken.Shipping).IsTrue();
        await Assert.That(taken.Generation).IsNotNull().And.IsNotEqualTo(first.Generation);
        await Assert.That(leases.Holders()[file.Replace('/', '.')]).IsEqualTo(other);
        // Both generations start with their own snapshot of the file.
        var generations = oci.Objects.Keys.Where(key => key.StartsWith(file + "/snapshots/", StringComparison.Ordinal))
            .Select(key => key.Split('/')[^1].Split('-')[1]).ToHashSet(StringComparer.Ordinal);
        await Assert.That(generations).IsEquivalentTo([first.Generation!, taken.Generation!]);

        async Task<TerraBaseTestServer> MemberAsync(string node)
        {
            return await TerraBaseTestServer.StartAsync(settings =>
            {
                oci.Configure(settings);
                settings["node-name"] = node;
            }, services =>
            {
                oci.Route(services);
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<ILeaseStore>(leases);
                services.AddSingleton<IShipperLeases, LeasedShippers>();
            });
        }

        async Task PassAsync()
        {
            foreach (var member in members.Values)
            {
                await member.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(CancellationToken.None);
            }
        }
    }

    [Test]
    public async Task Without_backup_settings_nothing_ships_and_no_database_has_a_restore_window()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("main");

        using var window = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time"));
        using var restore = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = DateTimeOffset.UtcNow });
        using var status = JsonDocument.Parse(await api.Server.Client.GetStringAsync(Uri("/v1/backup")));

        await Assert.That(window.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(restore.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(status.RootElement.GetProperty("shipping").GetBoolean()).IsFalse();
        await Assert.That(status.RootElement.GetProperty("files").GetArrayLength()).IsEqualTo(0);
    }

    private static string? Generation(IContinuousBackup backup, ManagedDatabaseRef database) =>
        backup.Status.Files.Single(file => file.File == $"v1/{database.Organization}/{database.Project}/{database.Id}").Generation;

    private static BackupFileStatus Status(TerraBaseTestServer member, string file) =>
        member.Services.GetRequiredService<IContinuousBackup>().Status.Files.Single(status => status.File == file);

    private static string DocumentPath(ManagedDatabaseRef database, string name, string document) =>
        $"/v1/projects/{database.Project}/databases/{name}/collections/people/documents/{ManagementApi.DocumentId(document)}";

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
