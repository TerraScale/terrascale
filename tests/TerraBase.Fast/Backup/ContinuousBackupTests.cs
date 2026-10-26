using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class ContinuousBackupTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_database_restores_as_of_a_chosen_second_from_its_encrypted_continuous_backup()
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var before = Faker.Lorem.Sentence(12);
        var after = Faker.Lorem.Sentence(12);
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "before")), new { text = before })).EnsureSuccessStatusCode();

        // Discovery runs every ten seconds: the pass after it snapshots every file, starting its generation.
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var snapshotted = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(5));
        var written = time.GetUtcNow();
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "after")), new { text = after })).EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var shipped = time.GetUtcNow();
        var lastShipped = backup.Status.Files.Single(file => file.File == File(database)).LastShippedAt;

        using var window = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time"));
        var bounds = await window.Content.ReadFromJsonAsync<JsonElement>();
        using var earlier = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = snapshotted.AddSeconds(3) });
        var earlierDatabase = await earlier.Content.ReadFromJsonAsync<JsonElement>();
        using var latest = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = lastShipped!.Value, name = "main-latest" });
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
        await Assert.That(lastShipped).IsNotNull().And.IsGreaterThanOrEqualTo(written);
        await Assert.That(bounds.GetProperty("latest").GetDateTimeOffset()).IsEqualTo(lastShipped!.Value);
        await Assert.That(earlier.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(earlierName).IsEqualTo("main-restore");
        await Assert.That(earlierDatabase.GetProperty("branchedFrom").GetString()).IsEqualTo(database.Id);
        await Assert.That((await earlierBefore.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(before);
        await Assert.That(earlierAfter.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(latest.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await latestAfter.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(after);
        // Before the oldest snapshot but within the plan's retention window: the backup does not hold it (409); only a
        // future or expired time is invalid.
        await Assert.That(tooEarly.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
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

        // The bucket holds sealed objects only, under data keys wrapped by the master key.
        var plain = Encoding.UTF8.GetBytes(after);
        await Assert.That(s3.Objects.Keys.Any(key => key.Contains("/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s3.Objects.Keys.Any(key => key.Contains("/wal/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s3.Objects.Values.Any(content => content.AsSpan().IndexOf(plain) >= 0)).IsFalse();
        await Assert.That(s3.Objects.All(item => item.Value.AsSpan().StartsWith("TBK1"u8) || item.Key.StartsWith("v1/keys/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s3.GeneratedKeys).IsGreaterThan(0);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task The_shipper_trims_the_change_log_and_keeps_one_generation_across_large_writes()
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var generation = Generation(backup, database);

        // Over a thousand pages and several read pages of the captured changes (each read stops after 4 MiB).
        var texts = Enumerable.Range(0, 6).Select(_ => new string(Faker.Random.Chars('a', 'z', 900_000))).ToList();
        for (var i = 0; i < 5; i++)
        {
            (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", $"large-{i}")), new { text = texts[i] })).EnsureSuccessStatusCode();
        }

        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "large-5")), new { text = texts[5] })).EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var shipped = backup.Status.Files.Single(file => file.File == File(database)).LastShippedAt!.Value;

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
        await Assert.That(backup.Status.Files.Single(file => file.File == File(database)).LogBytes).IsGreaterThan(6 * 900_000);
        // Every change read was trimmed from the capture table but its newest row, so a reopen keeps counting ids.
        await using var capture = new Turso.TursoConnection(TestMasterKey.ConnectionString(Path.Combine(api.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db")));
        await capture.OpenAsync();
        await using var rows = capture.CreateCommand();
        rows.CommandText = "SELECT count(*) FROM turso_cdc;";
        await Assert.That(await rows.ExecuteScalarAsync()).IsEqualTo(1L);
    }

    [Test]
    public async Task Automatic_checkpoints_across_several_passes_keep_one_generation()
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        // Checkpoint continuity owns the write workload; per-document quota accounting is covered separately.
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        using var first = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        using var second = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var clients = new[] { first, second };
        await ManagementApi.CreateCollectionAsync(first, database, "people");
        time.AdjustTime(time.GetUtcNow() + TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var generation = Generation(backup, database);

        // Each round pushes the log past a thousand pages. Reads between the writes return sessions to the pool, so
        // the log restarts under different connections, and each pass then sees a restart after a complete checkpoint.
        for (var round = 0; round < 10; round++)
        {
            // Faker.Random is shared and not thread-safe, so the text is drawn before the writers start.
            var text = new string(Faker.Random.Chars('a', 'z', 100_000));
            var writers = clients.Select(async (client, writer) =>
            {
                for (var i = 0; i < 8; i++)
                {
                    var path = ManagementApi.DocumentPath(database, "people", $"round-{round}-{writer}-{i}");
                    (await client.PutAsJsonAsync(Uri(path), new { text })).EnsureSuccessStatusCode();
                    (await client.GetAsync(Uri(path))).EnsureSuccessStatusCode();
                }
            });
            await Task.WhenAll(writers);

            // Advance the shipping timestamp without launching unrelated hosted maintenance.
            time.AdjustTime(time.GetUtcNow() + TimeSpan.FromMinutes(1));
            await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await Assert.That(Generation(backup, database)).IsEqualTo(generation);
        }
    }

    [Test]
    public async Task Changes_deleted_before_the_shipper_read_them_end_the_generation_and_lose_nothing()
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var generation = Generation(backup, database);
        var text = Faker.Lorem.Sentence(12);
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "outside")), new { text })).EnsureSuccessStatusCode();

        // Something else empties the capture table on the file's writer before the shipper read the write.
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var store = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
        await store.WithWriterAsync(writer =>
        {
            using var delete = writer.Connection.CreateCommand();
            delete.CommandText = "DELETE FROM turso_cdc;";
            return delete.ExecuteNonQuery();
        }, "a test", CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var shipped = backup.Status.Files.Single(file => file.File == File(database)).LastShippedAt!.Value;
        using var restored = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = shipped, name = "main-latest" });
        using var read = await data.GetAsync(Uri(DocumentPath(database, "main-latest", "outside")));
        var metrics = await api.Server.Client.GetStringAsync(Uri("/metrics"));

        await Assert.That(Generation(backup, database)).IsNotNull().And.IsNotEqualTo(generation);
        await Assert.That(metrics).Contains("terrabase_backup_holes_total");
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
    }

    [Test]
    public async Task A_shipper_cut_off_from_the_directory_stops_and_another_member_takes_over_once_it_is_removed()
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var store = new FakeMemberStore();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        var file = $"v1/{database.OrganizationId}/{database.ProjectId}/{database.DatabaseId}";
        await using var a = await MemberAsync("member-a");
        await using var b = await MemberAsync("member-b");
        var members = new Dictionary<string, TerraBaseTestServer>(StringComparer.Ordinal) { ["member-a"] = a, ["member-b"] = b };
        foreach (var member in members.Values)
        {
            await member.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(database, CancellationToken.None);
            var system = await member.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var registration = await system.BeginTransactionAsync(write: true, CancellationToken.None);
            await SystemCatalog.PutAsync(registration, "catalog", "version", ManagementCatalog.Version, TerraBaseJsonContext.Default.String, CancellationToken.None);
            await SystemCatalog.PutAsync(registration, "organizations", database.OrganizationId,
                new Organization(database.OrganizationId, "Shipping", database.OrganizationId, time.GetUtcNow()), TerraBaseJsonContext.Default.Organization, CancellationToken.None);
            await SystemCatalog.PutAsync(registration, $"organizations/{database.OrganizationId}/projects", database.ProjectId!,
                new Project(database.ProjectId!, database.OrganizationId, "Shipping", time.GetUtcNow()), TerraBaseJsonContext.Default.Project, CancellationToken.None);
            await SystemCatalog.PutAsync(registration, $"projects/{database.ProjectId}/databases", database.DatabaseId!,
                new ManagedDatabase(database.DatabaseId!, database.ProjectId!, "main", null, time.GetUtcNow()), TerraBaseJsonContext.Default.ManagedDatabase, CancellationToken.None);
            await registration.CommitAsync(CancellationToken.None);
        }

        // The database's primary on the ring ships it once its first term began: a lease fence after it saw the members.
        time.Advance(TimeSpan.FromSeconds(10));
        await PassAsync();
        time.Advance(TimeSpan.FromSeconds(10));
        await PassAsync();
        var holder = members.Single(member => Status(member.Value, file).Shipping).Key;
        var other = holder == "member-a" ? "member-b" : "member-a";
        var first = Status(members[holder], file);
        var otherFirst = Status(members[other], file);

        // Cut the holder off the directory: it stops shipping once its lease could be stale, while the other still lists it.
        store.Partition(holder);
        time.Advance(TimeSpan.FromSeconds(11));
        await PassAsync();
        var stopped = Status(members[holder], file);
        var waiting = Status(members[other], file);
        // The successor waits a full fence after observing the predecessor leave.
        store.Leave(holder);
        time.Advance(TimeSpan.FromSeconds(5));
        await PassAsync();
        await Assert.That(Status(members[other], file).Shipping).IsFalse();
        time.Advance(TimeSpan.FromSeconds(10));
        await PassAsync();
        var taken = Status(members[other], file);

        await Assert.That(first.Shipping).IsTrue();
        await Assert.That(first.Generation).IsNotNull();
        await Assert.That(otherFirst.Shipping).IsFalse();
        await Assert.That(stopped.Shipping).IsFalse();
        await Assert.That(waiting.Shipping).IsFalse();
        await Assert.That(taken.Shipping).IsTrue();
        await Assert.That(taken.Generation).IsNotNull().And.IsNotEqualTo(first.Generation);
        // Both generations start with their own snapshot of the file.
        var generations = s3.Objects.Keys.Where(key => key.StartsWith(file + "/snapshots/", StringComparison.Ordinal))
            .Select(key => key.Split('/')[^1].Split('-')[1]).ToHashSet(StringComparer.Ordinal);
        await Assert.That(generations).IsEquivalentTo([first.Generation!, taken.Generation!]);

        async Task<TerraBaseTestServer> MemberAsync(string node)
        {
            var member = await TerraBaseTestServer.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["node-name"] = node;
            }, services =>
            {
                ManualShipping(s3, services);
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<IMemberDirectory>(store);
            });
            Directory.CreateDirectory(Path.Combine(member.DataDirectory, ".backup"));
            return member;
        }

        async Task PassAsync()
        {
            foreach (var member in members.Values)
            {
                await member.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            }
        }
    }

    [Test]
    public async Task An_unavailable_plan_preserves_backup_history_and_retention_retries_after_recovery()
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var service = api.Service(api.User);
        (await service.PutAsJsonAsync(Uri($"/v1/management/organizations/{database.Organization}/plan"), new { plan = "pro" }, token))
            .EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        for (var day = 0; day < 2; day++)
        {
            time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
            await backup.RunOnceAsync(token);
        }

        var prefix = File(database) + "/snapshots/";
        var snapshots = s3.Objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        await Assert.That(snapshots.Length).IsEqualTo(3);
        var tenants = api.Server.Services.GetRequiredService<ITenantDirectory>();
        var found = await tenants.FindDatabaseAsync(database.Organization, database.Project, database.Name, token);
        var source = found is TenantContext tenant ? tenant : throw new InvalidOperationException("the seeded database must resolve");
        var restore = api.Server.Services.GetRequiredService<IPointInTimeRestore>();
        var target = source with { DatabaseId = Ulid.NewUlid().ToString() };
        var targetFile = Path.Combine(api.Server.DataDirectory, target.OrganizationId, target.ProjectId!, target.DatabaseId + ".db");
        await Assert.That(System.IO.File.Exists(targetFile)).IsFalse();
        var supportedVersion = await WriteCatalogVersionAsync(api.Server, "\"99\""u8.ToArray(), token);
        time.Advance(TimeSpan.FromDays(8));
        await backup.RunOnceAsync(token);
        await Assert.That(snapshots.All(s3.Objects.ContainsKey)).IsTrue();
        await Assert.That(await tenants.PlanAsync(source.OrganizationId, token) is Unavailable).IsTrue();
        await Assert.That(await restore.GetWindowAsync(source, token) is Unavailable).IsTrue();
        await Assert.That(await restore.PlanAsync(source, time.GetUtcNow(), token) is Unavailable).IsTrue();
        await Assert.That(await restore.RestoreAsync(source, time.GetUtcNow(), target, token) is Unavailable).IsTrue();
        await Assert.That(System.IO.File.Exists(targetFile)).IsFalse();
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri($"{database.Path}/point-in-time"), token),
            HttpStatusCode.ServiceUnavailable, "unavailable", "the management catalog has an old or unsupported format; discard the affected data");
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = time.GetUtcNow() }, token),
            HttpStatusCode.ServiceUnavailable, "unavailable", "the management catalog has an old or unsupported format; discard the affected data");

        await WriteCatalogVersionAsync(api.Server, supportedVersion, token);
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(token);
        // Recovery expires even the newest stale baseline; the fresh snapshot honestly shortens the window.
        await Assert.That(s3.Objects.ContainsKey(snapshots[0])).IsFalse();
        await Assert.That(s3.Objects.ContainsKey(snapshots[1])).IsFalse();
        await Assert.That(s3.Objects.ContainsKey(snapshots[2])).IsFalse();
        using var recovered = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time"), token);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var window = await recovered.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(window.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(Snapshots(s3, database)[0].At);
    }

    private static async Task<byte[]> WriteCatalogVersionAsync(TerraBaseTestServer server, byte[] version, CancellationToken cancellationToken)
    {
        var system = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken);
        await using var write = await system.BeginTransactionAsync(write: true, cancellationToken);
        var previous = (await write.GetAsync("catalog", "version", cancellationToken))!.Json.ToArray();
        await write.PutAsync("catalog", "version", version, cancellationToken);
        await write.CommitAsync(cancellationToken);
        return previous;
    }

    [Test]
    public async Task Retention_waits_for_the_buckets_retention_rule_without_failing()
    {
        var s3 = new FakeS3 { Locked = _ => true };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "kept")), new { text = Faker.Lorem.Sentence(8) })).EnsureSuccessStatusCode();
        for (var day = 0; day < 2; day++)
        {
            time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
            await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        }

        time.Advance(TimeSpan.FromHours(2));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var locked = Snapshots(s3, database).Count;
        var status = backup.Status.Files.Single(file => file.File == File(database));
        using var window = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{database.Path}/point-in-time")));

        // The rule lapses: the next hourly pass deletes what the plan no longer needs.
        s3.Locked = _ => false;
        time.Advance(TimeSpan.FromHours(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);

        await Assert.That(locked).IsEqualTo(3);
        await Assert.That(status.LastError).IsNull();
        await Assert.That(window.RootElement.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(time.GetUtcNow() - TimeSpan.FromHours(1) - TimeSpan.FromDays(1));
        await Assert.That(Snapshots(s3, database).Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_restore_drill_fails_when_the_bucket_no_longer_rebuilds_the_database()
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "doomed")), new { text = Faker.Lorem.Sentence(8) })).EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);

        // Flip a byte of the segment's last tag: it no longer authenticates.
        var segment = s3.Objects.Single(item => item.Key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal)).Value;
        segment[^1] ^= 0xff;
        time.Advance(TimeSpan.FromDays(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var drill = backup.Status.Files.Single(file => file.File == File(database)).LastDrill!;
        var record = await RecordAsync(api, database);
        var metrics = await api.Server.Client.GetStringAsync(Uri("/metrics"));

        await Assert.That(drill.Passed).IsFalse();
        await Assert.That(drill.Error).IsNotNull();
        await Assert.That(record.GetProperty("lastDrill").GetProperty("passed").GetBoolean()).IsFalse();
        await Assert.That(metrics).Contains("terrabase_backup_drills_total");
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

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";

    // Snapshot keys are <ms>-<generation>-<position>, oldest first.
    private static List<(DateTimeOffset At, long Position)> Snapshots(FakeS3 s3, ManagedDatabaseRef database) =>
        [.. s3.Objects.Keys.Where(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(key => key.Split('/')[^1].Split('-'))
            .Select(parts => (DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[0], CultureInfo.InvariantCulture)), long.Parse(parts[2], CultureInfo.InvariantCulture)))];

    // The backup record the shipper keeps in the system catalog, under the database's state.
    private static async Task<JsonElement> RecordAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await using (transaction)
        {
            var document = await transaction.GetAsync($"databases/{database.Id}/backup_status", "current", CancellationToken.None);
            return JsonDocument.Parse(document!.Json).RootElement.Clone();
        }
    }

    private static BackupFileStatus Status(TerraBaseTestServer member, string file) =>
        member.Services.GetRequiredService<IContinuousBackup>().Status.Files.Single(status => status.File == file);

    private static string DocumentPath(ManagedDatabaseRef database, string name, string document) =>
        $"/v1/projects/{database.Project}/databases/{name}/collections/people/documents/by-key?id={ManagementApi.DocumentId(document)}";

    private static void ManualShipping(FakeS3 s3, IServiceCollection services)
    {
        s3.Route(services);
        ManualBackupHosting.Configure(services);
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
