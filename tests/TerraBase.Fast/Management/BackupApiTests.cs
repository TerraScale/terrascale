using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Primitives;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Management;

public sealed class BackupApiTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_backup_of_another_database_cannot_be_restored_through_this_database()
    {
        await using var api = await ManagementApi.StartAsync();
        var source = await api.CreateManagedDatabaseAsync();
        var other = await api.CreateManagedDatabaseAsync(source.Organization, source.Project, "other");
        using var created = await api.Client.PostAsync(Uri($"{source.Path}/backups"), content: null);
        created.EnsureSuccessStatusCode();
        var backupId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        using var restored = await api.Client.PostAsJsonAsync(Uri($"{other.Path}/backups/{backupId}/restore"), new { name = "stolen" });
        using var databases = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{source.ProjectPath}/databases")));

        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(databases.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await Assert.That(File.Exists(SnapshotPath(api, source, backupId))).IsTrue();
    }

    [Test]
    public async Task The_default_policy_backs_a_database_up_daily_from_its_creation()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 1, 55, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var policy = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/backup-policy"));

        var backups = await WaitForBackupsAsync(api, time, $"{database.Path}/backups", items => items.Length > 0);

        await Assert.That(policy.GetProperty("fullBackupCron").GetString()).IsEqualTo("0 2 * * *");
        await Assert.That(policy.GetProperty("updatedAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(backups.Single().GetProperty("trigger").GetString()).IsEqualTo("scheduled");
        await Assert.That(backups.Single().GetProperty("createdAt").GetDateTimeOffset()).IsGreaterThanOrEqualTo(new DateTimeOffset(2026, 10, 4, 2, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task A_registered_database_keeps_its_default_backup_schedule_after_restart()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-backup-enrollment-").FullName;
        try
        {
            string user, backups, policyPath;
            await using (var api = await ManagementApi.StartAsync(
                settings => settings["data-directory"] = directory, time: new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 1, 50, 0, TimeSpan.Zero))))
            {
                user = api.User;
                var database = await api.CreateManagedDatabaseAsync();
                backups = $"{database.Path}/backups";
                policyPath = $"{database.Path}/backup-policy";
            }

            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 1, 55, 0, TimeSpan.Zero));
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: time))
            {
                using var owner = api.As(user);
                var policy = await owner.GetFromJsonAsync<JsonElement>(Uri(policyPath));
                JsonElement[] items = [];
                await WaitAsync(time, async () =>
                {
                    using var page = JsonDocument.Parse(await owner.GetStringAsync(Uri(backups)));
                    items = [.. page.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone())];
                    return items.Length > 0;
                });

                await Assert.That(policy.GetProperty("fullBackupCron").GetString()).IsEqualTo("0 2 * * *");
                await Assert.That(policy.GetProperty("updatedAt").ValueKind).IsEqualTo(JsonValueKind.Null);
                await Assert.That(items.Single().GetProperty("trigger").GetString()).IsEqualTo("scheduled");
                await Assert.That(items.Single().GetProperty("createdAt").GetDateTimeOffset()).IsGreaterThanOrEqualTo(new DateTimeOffset(2026, 10, 4, 2, 0, 0, TimeSpan.Zero));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_backup_restores_into_a_new_database_with_the_data_at_backup_time()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "before")), new { name = Faker.Name.FullName() })).EnsureSuccessStatusCode();

        using var created = await api.Client.PostAsync(Uri($"{database.Path}/backups"), content: null);
        var backup = await created.Content.ReadFromJsonAsync<JsonElement>();
        var backupId = backup.GetProperty("id").GetString()!;
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "after")), new { name = Faker.Name.FullName() })).EnsureSuccessStatusCode();
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{database.Path}/backups")));
        using var restored = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{backupId}/restore"), new { });
        var restore = await restored.Content.ReadFromJsonAsync<JsonElement>();
        var restoreData = $"/v1/projects/{database.Project}/databases/{restore.GetProperty("name").GetString()}/collections/people/documents";
        using var before = await data.GetAsync(Uri($"{restoreData}/by-key?id={ManagementApi.DocumentId("before")}"));
        using var after = await data.GetAsync(Uri($"{restoreData}/by-key?id={ManagementApi.DocumentId("after")}"));
        using var second = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{backupId}/restore"), new { });
        var secondName = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString();
        using var named = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{backupId}/restore"), new { name = "recovered" });
        using var duplicate = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{backupId}/restore"), new { name = "main-restore" });
        using var invalid = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{backupId}/restore"), new { name = "Not Valid" });
        using var unknown = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{EntityIds.New()}/restore"), new { });
        using var databases = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{database.ProjectPath}/databases")));

        var item = list.RootElement.GetProperty("items").EnumerateArray().Single();
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"{database.Path}/backups/{backupId}");
        await Assert.That(item.GetProperty("id").GetString()).IsEqualTo(backupId);
        await Assert.That(item.GetProperty("databaseId").GetString()).IsEqualTo(database.Id);
        await Assert.That(item.GetProperty("status").GetString()).IsEqualTo("ready");
        await Assert.That(item.GetProperty("trigger").GetString()).IsEqualTo("manual");
        await Assert.That(item.GetProperty("sizeBytes").GetInt64()).IsGreaterThan(0);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(restore.GetProperty("name").GetString()).IsEqualTo("main-restore");
        await Assert.That(restore.GetProperty("branchedFrom").GetString()).IsEqualTo(database.Id);
        await Assert.That(restored.Headers.Location!.ToString()).IsEqualTo($"{database.ProjectPath}/databases/{restore.GetProperty("id").GetString()}");
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(secondName).IsEqualTo("main-restore-2");
        await Assert.That(named.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(databases.RootElement.GetProperty("items").EnumerateArray().Select(database => database.GetProperty("name").GetString()!))
            .IsEquivalentTo(["main", "main-restore", "main-restore-2", "recovered"]);
    }

    [Test]
    public async Task The_backup_policy_has_defaults_and_is_validated_and_saved()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var policy = Uri($"{database.Path}/backup-policy");
        var retentionDays = Faker.Random.Int(1, 35);

        var initial = await api.Client.GetFromJsonAsync<JsonElement>(policy);
        using var saved = await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "30 4 * * 1-5", retentionDays, disabled = true });
        var savedBody = await saved.Content.ReadFromJsonAsync<JsonElement>();
        var read = await api.Client.GetFromJsonAsync<JsonElement>(policy);
        using var noSchedule = await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "", retentionDays = 35 });
        var noScheduleBody = await noSchedule.Content.ReadFromJsonAsync<JsonElement>();
        using var badCron = await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "every day", retentionDays = 7 });
        using var sixFields = await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "0 0 2 * * *", retentionDays = 7 });
        using var zeroDays = await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "0 2 * * *", retentionDays = 0 });
        using var tooManyDays = await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "0 2 * * *", retentionDays = 36 });
        using var missingDays = await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "0 2 * * *" });

        await Assert.That(initial.GetProperty("fullBackupCron").GetString()).IsEqualTo("0 2 * * *");
        await Assert.That(initial.GetProperty("retentionDays").GetInt32()).IsEqualTo(7);
        await Assert.That(initial.GetProperty("disabled").GetBoolean()).IsFalse();
        await Assert.That(initial.GetProperty("updatedAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(savedBody.GetProperty("updatedAt").ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(read.GetProperty("fullBackupCron").GetString()).IsEqualTo("30 4 * * 1-5");
        await Assert.That(read.GetProperty("retentionDays").GetInt32()).IsEqualTo(retentionDays);
        await Assert.That(read.GetProperty("disabled").GetBoolean()).IsTrue();
        await Assert.That(noSchedule.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(noScheduleBody.GetProperty("fullBackupCron").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(noScheduleBody.GetProperty("disabled").GetBoolean()).IsFalse();
        await Assert.That(badCron.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(sixFields.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(zeroDays.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(tooManyDays.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(missingDays.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Deleting_a_database_deletes_its_files_now_and_its_backups_through_the_sweep()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        (await api.Client.PostAsync(Uri($"{database.Path}/backups"), content: null)).EnsureSuccessStatusCode();
        (await api.Client.PostAsync(Uri($"{database.Path}/backups"), content: null)).EnsureSuccessStatusCode();
        var backupFiles = SnapshotFiles(api, database).Count(file => file.EndsWith(".db", StringComparison.Ordinal));

        using var deleted = await api.Client.DeleteAsync(Uri(database.Path));
        var databaseFiles = RemainingFiles(ProjectDirectory(api, database), $"{database.Id}*").Length;
        await WaitAsync(time, async () =>
            SnapshotFiles(api, database).Length == 0
            && await CountAsync(api.Server.DataDirectory, "storage_sweep") == 0
            && await CountAsync(api.Server.DataDirectory, $"organizations/{database.Organization}/backups") == 0);

        await Assert.That(backupFiles).IsEqualTo(2);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(databaseFiles).IsEqualTo(0);
    }

    [Test]
    public async Task Backups_are_listed_in_registration_order_with_an_opaque_cursor()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var backups = $"{database.Path}/backups";
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            using var created = await api.Client.PostAsync(Uri(backups), content: null);
            ids.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        var seen = new List<string>();
        string? next = null;
        do
        {
            using var page = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(next is null ? $"{backups}?limit=1" : $"{backups}?limit=1&after={next}")));
            seen.AddRange(page.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
            next = page.RootElement.GetProperty("next").GetString();
        }
        while (next is not null && seen.Count < 10);

        await Assert.That(string.Join(',', seen)).IsEqualTo(string.Join(',', ids));
    }

    [Test]
    public async Task Retention_applies_the_policy_as_saved_when_the_pass_runs()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        var backups = $"{database.Path}/backups";
        var policy = Uri($"{database.Path}/backup-policy");
        using var created = await api.Client.PostAsync(Uri(backups), content: null);
        var backupId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var snapshot = SnapshotPath(api, database, backupId);
        (await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "", retentionDays = 1 })).EnsureSuccessStatusCode();
        (await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "", retentionDays = 35 })).EnsureSuccessStatusCode();

        // Two days on, the backup is past the first policy's retention but within the saved one; a witness backed up
        // every minute shows that passes ran meanwhile.
        time.Advance(TimeSpan.FromDays(2));
        var witness = await api.CreateManagedDatabaseAsync();
        (await api.Client.PutAsJsonAsync(Uri($"{witness.Path}/backup-policy"), new { fullBackupCron = "* * * * *", retentionDays = 35 })).EnsureSuccessStatusCode();
        await WaitForBackupsAsync(api, time, $"{witness.Path}/backups", items => items.Length >= 2);
        var kept = await ListBackupsAsync(api, backups);
        (await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "", retentionDays = 1 })).EnsureSuccessStatusCode();
        // The witness keeps its backups: wait for this backup's own file.
        await WaitAsync(time, async () => (await ListBackupsAsync(api, backups)).Length == 0 && !File.Exists(snapshot));

        await Assert.That(kept.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Scheduled_backups_follow_the_cron_and_expire_after_the_retention()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var backups = $"{database.Path}/backups";
        using var manual = await api.Client.PostAsync(Uri(backups), content: null);
        var manualId = (await manual.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        (await api.Client.PutAsJsonAsync(Uri($"{database.Path}/backup-policy"), new { fullBackupCron = "0 * * * *", retentionDays = 1 })).EnsureSuccessStatusCode();

        time.Advance(TimeSpan.FromHours(1));
        var scheduled = await WaitForBackupsAsync(api, time, backups, items => items.Any(item => item.GetProperty("trigger").GetString() == "scheduled"));
        var firstScheduledId = scheduled.First(item => item.GetProperty("trigger").GetString() == "scheduled").GetProperty("id").GetString();
        time.Advance(TimeSpan.FromDays(2));
        // Retention unlists the old backups before the next scheduled one registers: wait for a list holding only new ones.
        var remaining = await WaitForBackupsAsync(api, time, backups, items => items.Length > 0
            && items.All(item => item.GetProperty("id").GetString() != manualId && item.GetProperty("id").GetString() != firstScheduledId));

        await Assert.That(scheduled.Select(item => item.GetProperty("id").GetString())).Contains(manualId);
        await Assert.That(remaining.Length).IsGreaterThan(0);
        await Assert.That(remaining.All(item => item.GetProperty("trigger").GetString() == "scheduled")).IsTrue();
        await Assert.That(remaining.All(item => item.GetProperty("createdAt").GetDateTimeOffset() > time.GetUtcNow().AddDays(-1))).IsTrue();
    }

    // A copy of a database with a newer schema version fails to open, so every scheduled copy of it throws.
    // Expiry unlists a backup first and the sweep deletes its files after; waits until no backup, backup file or sweep
    // entry is left.
    // Where the engine keeps a database's files, and its backups' snapshots: its tenant's project directory.
    private static string ProjectDirectory(ManagementApi api, ManagedDatabaseRef database) =>
        Path.Combine(api.Server.DataDirectory, database.Organization, database.Project);

    private static string SnapshotPath(ManagementApi api, ManagedDatabaseRef database, string backupId) =>
        Path.Combine(ProjectDirectory(api, database), backupId + ".db");

    // The snapshot files (with sidecars and temporary files) next to a database: every file of its project directory but its own.
    private static string[] SnapshotFiles(ManagementApi api, ManagedDatabaseRef database)
    {
        return [.. RemainingFiles(ProjectDirectory(api, database))
            .Where(file => !Path.GetFileName(file).StartsWith(database.Id, StringComparison.Ordinal))];
    }

    private static string[] RemainingFiles(string directory, string pattern = "*")
    {
        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (DirectoryNotFoundException)
        {
            // The engine removes the empty project directory as the sweep finishes.
            return [];
        }
    }

    // Steps the fake clock a minute at a time so the scheduler ticks, until the backups satisfy done (see WaitAsync).
    private static async Task<JsonElement[]> WaitForBackupsAsync(ManagementApi api, FakeTimeProvider time, string backups, Func<JsonElement[], bool> done)
    {
        JsonElement[] items = [];
        await WaitAsync(time, async () => done(items = await ListBackupsAsync(api, backups)));
        return items;
    }

    // Steps the fake clock a minute at a time so the scheduler ticks, until done: at most 600 steps, each waiting 100 ms
    // of real time, so a loaded parallel run still has a minute.
    private static async Task WaitAsync(FakeTimeProvider time, Func<Task<bool>> done)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            if (await done())
            {
                return;
            }

            time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(100);
        }

        throw new TimeoutException("the backups did not reach the expected state");
    }

    private static async Task<JsonElement[]> ListBackupsAsync(ManagementApi api, string backups)
    {
        using var page = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{backups}?limit=100")));
        return [.. page.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone())];
    }

    // Counts the catalog documents of a collection, read-only, straight from the catalog file.
    private static async Task<long> CountAsync(string dataDirectory, string collection)
    {
        var path = Path.Combine(dataDirectory, "_system.db");
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM documents WHERE collection = $collection;";
        command.Parameters.AddWithValue("$collection", collection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // The documents of a catalog collection by id, read-only, straight from the catalog file.
    private static async Task<Dictionary<string, JsonElement>> ReadAsync(string dataDirectory, string collection)
    {
        var path = Path.Combine(dataDirectory, "_system.db");
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, body FROM documents WHERE collection = $collection;";
        command.Parameters.AddWithValue("$collection", collection);
        await using var reader = await command.ExecuteReaderAsync();
        var documents = new Dictionary<string, JsonElement>();
        while (await reader.ReadAsync())
        {
            documents[reader.GetString(0)] = JsonSerializer.Deserialize<JsonElement>(reader.GetString(1));
        }

        return documents;
    }

    // Writes a catalog document straight into the catalog file, as a process that stopped mid-way would have left it.
    private static Uri Uri(string path) => ManagementApi.Uri(path);

    /// <summary>A fake clock whose wall time is moved by <see cref="Offset"/>, as a clock correction does; timers keep the fake time.</summary>
    private sealed class SteppedTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }
}
