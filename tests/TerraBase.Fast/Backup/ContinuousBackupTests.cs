using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
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
        await Assert.That(tooEarly.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(future.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var files = status.RootElement.GetProperty("files").EnumerateArray().ToList();
        await Assert.That(status.RootElement.GetProperty("shipping").GetBoolean()).IsTrue();
        await Assert.That(files.Select(file => file.GetProperty("file").GetString())).Contains("v1/_system/nodes/local");
        await Assert.That(files.Select(file => file.GetProperty("file").GetString())).Contains($"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local");
        await Assert.That(files.All(file => file.GetProperty("generation").GetString() is not null && file.GetProperty("lagSeconds").GetDouble() == 0)).IsTrue();

        foreach (var metric in new[] { "terrabase_backup_lag_seconds", "terrabase_backup_last_success_seconds", "terrabase_backup_uploaded_bytes_total", "terrabase_backup_files" })
        {
            await Assert.That(metrics).Contains(metric);
        }
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
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        using var first = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        using var second = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var clients = new[] { first, second };
        await ManagementApi.CreateCollectionAsync(first, database, "people");
        time.AdjustTime(time.GetUtcNow() + TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var generation = Generation(backup, database);
        for (var round = 0; round < 10; round++)
        {
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
            time.AdjustTime(time.GetUtcNow() + TimeSpan.FromMinutes(1));
            await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await Assert.That(Generation(backup, database)).IsEqualTo(generation);
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
        backup.Status.Files.Single(file => file.File == $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local").Generation;

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";
    private static string DocumentPath(ManagedDatabaseRef database, string name, string document) =>
        $"/v1/projects/{database.Project}/databases/{name}/collections/people/documents/by-key?id={ManagementApi.DocumentId(document)}";

    private static void ManualShipping(FakeS3 s3, IServiceCollection services)
    {
        s3.Route(services);
        ManualBackupHosting.Configure(services);
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
