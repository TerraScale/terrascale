using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Management;

// Scheduler, restart and retention timing scenarios run independently from the Fast backup workflows.
public sealed class BackupScheduleTests
{
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

    private static string ProjectDirectory(ManagementApi api, ManagedDatabaseRef database) =>
        Path.Combine(api.Server.DataDirectory, database.Organization, database.Project);

    private static string SnapshotPath(ManagementApi api, ManagedDatabaseRef database, string backupId) =>
        Path.Combine(ProjectDirectory(api, database), backupId + ".db");

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

    private static async Task<JsonElement[]> WaitForBackupsAsync(ManagementApi api, FakeTimeProvider time, string backups, Func<JsonElement[], bool> done)
    {
        JsonElement[] items = [];
        await WaitAsync(time, async () => done(items = await ListBackupsAsync(api, backups)));
        return items;
    }

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

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
