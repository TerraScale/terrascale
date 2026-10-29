using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;

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
        using var named = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{backupId}/restore"), new { name = "recovered" });
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
        await Assert.That(named.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(databases.RootElement.GetProperty("items").EnumerateArray().Select(database => database.GetProperty("name").GetString()!))
            .IsEquivalentTo(["main", "main-restore", "recovered"]);
    }

    [Test]
    public async Task The_backup_policy_has_defaults_and_is_saved()
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

    // The backup snapshot is stored alongside the source database.
    private static string ProjectDirectory(ManagementApi api, ManagedDatabaseRef database) =>
        Path.Combine(api.Server.DataDirectory, database.Organization, database.Project);

    private static string SnapshotPath(ManagementApi api, ManagedDatabaseRef database, string backupId) =>
        Path.Combine(ProjectDirectory(api, database), backupId + ".db");

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
