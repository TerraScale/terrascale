using System.Net;
using System.Text.Json;
using Bogus;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Management.Backups;
using TerraBase.Primitives;
using TerraBase.Tenancy;
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
    public async Task Concurrent_restores_that_both_precheck_the_same_name_retry_the_losing_registration()
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var document = await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "saved")), new { name = Faker.Name.FullName() });
        document.EnsureSuccessStatusCode();
        using var created = await api.Client.PostAsync(Uri($"{database.Path}/backups"), content: null);
        created.EnsureSuccessStatusCode();
        var backupId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        using var gate = new UsageCommitHook.Gate(fail: false);
        var bothPrechecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = 0;
        var prechecks = 0;
        hook.BeforeQuota = async cancellationToken =>
        {
            if (Interlocked.Increment(ref prechecks) <= 2)
            {
                if (Volatile.Read(ref prechecks) == 2)
                {
                    bothPrechecked.TrySetResult();
                }

                await gate.WaitAsync(cancellationToken);
            }
        };
        hook.BeforeCopy = (source, _) =>
        {
            if (source.DatabaseId == backupId)
            {
                Interlocked.Increment(ref copies);
            }

            return ValueTask.CompletedTask;
        };

        var first = api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{backupId}/restore"), new { });
        var second = api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{backupId}/restore"), new { });
        await bothPrechecked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        gate.Dispose();
        using var firstResponse = await first;
        using var secondResponse = await second;
        var restores = new[]
        {
            await firstResponse.Content.ReadFromJsonAsync<JsonElement>(),
            await secondResponse.Content.ReadFromJsonAsync<JsonElement>(),
        };

        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(secondResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(restores.Select(restore => restore.GetProperty("name").GetString()!)).IsEquivalentTo(["main-restore", "main-restore-2"]);
        await Assert.That(copies).IsEqualTo(3);
        foreach (var restore in restores)
        {
            using var read = await data.GetAsync(Uri($"/v1/projects/{database.Project}/databases/{restore.GetProperty("name").GetString()}/collections/people/documents/by-key?id={ManagementApi.DocumentId("saved")}"));
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(Directory.GetFiles(ProjectDirectory(api, database), "*.db").Select(Path.GetFileNameWithoutExtension))
            .IsEquivalentTo([database.Id, backupId, .. restores.Select(restore => restore.GetProperty("id").GetString()!)]);
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
    public async Task A_restart_counts_untracked_files_but_not_backup_snapshots_as_unknown_storage()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-backup-restart-").FullName;
        try
        {
            string user, backups, snapshot;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                user = api.User;
                var database = await api.CreateManagedDatabaseAsync();
                backups = $"{database.Path}/backups";
                using var created = await api.Client.PostAsync(Uri(backups), content: null);
                created.EnsureSuccessStatusCode();
                snapshot = SnapshotPath(api, database, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
            }

            // A copy no backup or database tracks, beside the snapshot.
            File.Copy(snapshot, Path.Combine(Path.GetDirectoryName(snapshot)!, EntityIds.New() + ".db"));
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                using var owner = api.As(user);
                using var listed = JsonDocument.Parse(await owner.GetStringAsync(Uri(backups)));
                var metrics = await api.Server.Client.GetStringAsync(Uri("/metrics"));
                var unknown = metrics.Split('\n').Single(line => line.StartsWith("terrabase_storage_unknown_files", StringComparison.Ordinal));

                await Assert.That(listed.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(1);
                await Assert.That(File.Exists(snapshot)).IsTrue();
                await Assert.That(unknown.Split(' ')[1]).IsEqualTo("1");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Concurrent_restores_to_one_explicit_name_create_it_once_and_delete_the_losing_copy()
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var created = await api.Client.PostAsync(Uri($"{database.Path}/backups"), content: null);
        created.EnsureSuccessStatusCode();
        var backupId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // Both requests pass the name precheck before either acquires the organization publication gate.
        using var gate = new UsageCommitHook.Gate(fail: false);
        var bothPrechecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prechecks = 0;
        var copies = 0;
        hook.BeforeQuota = async cancellationToken =>
        {
            if (Interlocked.Increment(ref prechecks) == 2)
            {
                bothPrechecked.TrySetResult();
            }

            await gate.WaitAsync(cancellationToken);
        };
        hook.BeforeCopy = (source, _) =>
        {
            if (source.DatabaseId == backupId)
            {
                Interlocked.Increment(ref copies);
            }

            return ValueTask.CompletedTask;
        };

        var restores = Enumerable.Range(0, 2)
            .Select(_ => api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{backupId}/restore"), new { name = "restored" }))
            .ToArray();
        await bothPrechecked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        gate.Dispose();
        var responses = await Task.WhenAll(restores);
        try
        {
            var winner = responses.Single(response => response.StatusCode == HttpStatusCode.Created);
            var restoredId = (await winner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

            await Assert.That(responses.Select(response => response.StatusCode)).IsEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict]);
            await Assert.That(copies).IsEqualTo(2);
            await Assert.That(Directory.GetFiles(ProjectDirectory(api, database), "*.db").Select(file => Path.GetFileNameWithoutExtension(file)!))
                .IsEquivalentTo([database.Id, backupId, restoredId]);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Test]
    public async Task Cancelling_a_sweep_finishes_its_current_cleanup_and_leaves_the_remaining_entries_for_retry()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
        {
            hook.Install(services);
            services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType?.Name == "BackupScheduler"));
        });
        var database = await api.CreateManagedDatabaseAsync();
        var backupIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 3; i++)
        {
            using var created = await api.Client.PostAsync(Uri($"{database.Path}/backups"), content: null);
            created.EnsureSuccessStatusCode();
            backupIds.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        using var policy = await api.Client.PutAsJsonAsync(Uri($"{database.Path}/backup-policy"), new { fullBackupCron = "", retentionDays = 1 });
        policy.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromDays(2));
        hook.BeforeDelete = (_, _) => throw new IOException("injected cleanup failure");
        var backups = api.Server.Services.GetRequiredService<IManagedBackupService>();
        await backups.RunScheduleAsync(CancellationToken.None);
        await Assert.That(await CountAsync(api.Server.DataDirectory, "storage_sweep")).IsEqualTo(3);

        using var cancellation = new CancellationTokenSource();
        var deletions = 0;
        hook.BeforeDelete = (storage, _) =>
        {
            if (backupIds.Contains(storage.DatabaseId!))
            {
                Interlocked.Increment(ref deletions);
                cancellation.Cancel();
            }

            return ValueTask.CompletedTask;
        };

        await Assert.That(async () => await backups.RunScheduleAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(deletions).IsEqualTo(1);
        await Assert.That(await CountAsync(api.Server.DataDirectory, "storage_sweep")).IsEqualTo(2);
        await Assert.That(backupIds.Count(id => File.Exists(SnapshotPath(api, database, id)))).IsEqualTo(2);

        hook.BeforeDelete = null;
        await backups.RunScheduleAsync(CancellationToken.None);
        await Assert.That(await CountAsync(api.Server.DataDirectory, "storage_sweep")).IsEqualTo(0);
        await Assert.That(backupIds.Count(id => File.Exists(SnapshotPath(api, database, id)))).IsEqualTo(0);
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
    public async Task Backup_errors_answer_their_code_and_message()
    {
        const string BadCron = "fullBackupCron must be a standard 5-field cron expression";
        const string BadRetention = "retentionDays must be an integer between 1 and 35";
        const string BadLimit = "limit must be an integer between 1 and 100";
        const string BadName = "name must be a lowercase database name of at most 63 characters";
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var created = await api.Client.PostAsync(Uri($"{database.Path}/backups"), content: null);
        var backup = $"{database.Path}/backups/{(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}";
        var unknown = $"{database.ProjectPath}/databases/{EntityIds.New()}";

        await ErrorAssert.ErrorAsync(
            api.Client.PutAsJsonAsync(Uri($"{database.Path}/backup-policy"), new { fullBackupCron = "every day", retentionDays = 7 }), HttpStatusCode.BadRequest, "invalid_request", BadCron);
        await ErrorAssert.ErrorAsync(
            api.Client.PutAsJsonAsync(Uri($"{database.Path}/backup-policy"), new { retentionDays = 36 }), HttpStatusCode.BadRequest, "invalid_request", BadRetention);
        await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(Uri($"{database.Path}/backup-policy"), new { }), HttpStatusCode.BadRequest, "invalid_request", BadRetention);
        await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(Uri($"{unknown}/backup-policy"), new { retentionDays = 7 }), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri($"{unknown}/backup-policy")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri($"{database.Path}/backups?limit=0")), HttpStatusCode.BadRequest, "invalid_request", BadLimit);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri($"{unknown}/backups")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.PostAsync(Uri($"{unknown}/backups"), content: null), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri($"{backup}/restore"), new { name = "Not Valid" }), HttpStatusCode.BadRequest, "invalid_request", BadName);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri($"{backup}/restore"), new { name = "main" }), HttpStatusCode.Conflict, "name_taken", "name is already taken");
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri($"{database.Path}/backups/{EntityIds.New()}/restore"), new { }), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri($"{unknown}/backups/{EntityIds.New()}/restore"), new { }), HttpStatusCode.NotFound, "not_found");
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
    [Arguments("pending", "none")]
    [Arguments("snapshotting", "none")]
    [Arguments("pending", "success")]
    [Arguments("snapshotting", "success")]
    [Arguments("pending", "failure")]
    [Arguments("snapshotting", "failure")]
    public async Task An_abandoned_snapshot_is_swept_and_records_failure_without_overwriting_newer_results(string state, string newerResult)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        (await api.Client.PostAsync(Uri($"{database.Path}/backups"), content: null)).EnsureSuccessStatusCode();

        // What a process that stopped mid-snapshot leaves behind: a snapshotting record of another run, its sweep entry
        // and snapshot files, including private temporary files and orphaned sidecars.
        var abandoned = EntityIds.New().ToString();
        var snapshot = SnapshotPath(api, database, abandoned);
        File.Copy(SnapshotFiles(api, database).Single(file => file.EndsWith(".db", StringComparison.Ordinal)), snapshot);
        var directory = ProjectDirectory(api, database);
        var temporary = Path.Combine(directory, $".{abandoned}.{EntityIds.New()}.creating");
        var orphan = Path.Combine(directory, $".{abandoned}.{EntityIds.New()}.creating");
        var unrelated = Path.Combine(directory, $".{abandoned}-other.{EntityIds.New()}.creating");
        foreach (var file in new[] { temporary, temporary + "-wal", temporary + "-shm", temporary + "-journal", orphan + "-wal", unrelated, unrelated + "-wal" })
        {
            await File.WriteAllTextAsync(file, "abandoned");
        }

        var startedAt = time.GetUtcNow().AddMinutes(-2);
        DateTimeOffset? succeededAt = newerResult == "success" ? time.GetUtcNow().AddMinutes(-1) : null;
        var failedAt = newerResult == "failure" ? time.GetUtcNow().AddMinutes(-1) : startedAt;
        if (newerResult != "none")
        {
            await InsertAsync(api.Server.DataDirectory, $"databases/{database.Id}/backup_schedule", "last",
                JsonSerializer.Serialize(new { lastSucceededAt = succeededAt, lastFailedAt = newerResult == "failure" ? failedAt : (DateTimeOffset?)null }));
        }

        var record = JsonSerializer.Serialize(new
        {
            id = abandoned,
            organizationId = database.Organization,
            projectId = database.Project,
            databaseId = database.Id,
            state,
            trigger = "scheduled",
            startedAt,
            completedAt = (DateTimeOffset?)null,
            sizeBytes = 0,
            order = (string?)null,
        });
        await InsertAsync(api.Server.DataDirectory, $"organizations/{database.Organization}/backups", abandoned, record);
        await InsertAsync(api.Server.DataDirectory, "storage_sweep", $"backup:{abandoned}",
            JsonSerializer.Serialize(new { kind = "backup", organizationId = database.Organization, id = abandoned }));

        await WaitAsync(time, async () =>
            !File.Exists(snapshot)
            && await CountAsync(api.Server.DataDirectory, "storage_sweep") == 0
            && await CountAsync(api.Server.DataDirectory, $"organizations/{database.Organization}/backups") == 1);

        // The registered backup is untouched.
        await Assert.That((await ListBackupsAsync(api, $"{database.Path}/backups")).Length).IsEqualTo(1);
        await Assert.That(Directory.GetFiles(directory, $".{abandoned}.*.creating*").Length).IsEqualTo(0);
        await Assert.That(File.Exists(unrelated)).IsTrue();
        await Assert.That(File.Exists(unrelated + "-wal")).IsTrue();
        var schedule = (await ReadScheduleAsync(api, database))!.Value;
        await Assert.That(schedule.GetProperty("lastSucceededAt").Deserialize<DateTimeOffset?>()).IsEqualTo(succeededAt);
        await Assert.That(schedule.GetProperty("lastFailedAt").Deserialize<DateTimeOffset?>()).IsEqualTo(newerResult == "success" ? null : failedAt);
    }

    [Test]
    [Arguments("manual")]
    [Arguments("scheduled")]
    public async Task An_attempt_whose_failure_could_not_be_recorded_is_swept_by_a_later_pass_without_a_restart(string trigger)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        // Intercepts the catalog commits that register or expire a backup.
        var hook = new UsageCommitHook((collection, _, json) =>
            collection.StartsWith("organizations/", StringComparison.Ordinal) && collection.EndsWith("/backups", StringComparison.Ordinal)
            && (json.Span.IndexOf("\"state\":\"registered\""u8) >= 0 || json.Span.IndexOf("\"state\":\"expiring\""u8) >= 0));
        await using var api = await ManagementApi.StartAsync(time: time, services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        var records = $"organizations/{database.Organization}/backups";

        // A brief catalog outage: registering the complete snapshot fails, then so does settling that failure, which is
        // held open while the attempt's state is read.
        hook.PauseCommit(fail: true).Dispose();
        using var settlement = hook.PauseCommit(fail: true);
        var created = trigger == "manual" ? api.Client.PostAsync(Uri($"{database.Path}/backups"), content: null) : null;
        if (created is null)
        {
            (await api.Client.PutAsJsonAsync(Uri($"{database.Path}/backup-policy"), new { fullBackupCron = "* * * * *", retentionDays = 35 })).EnsureSuccessStatusCode();
        }

        await WaitAsync(time, () => Task.FromResult(settlement.Entered.Task.IsCompleted));
        var (id, record) = (await ReadAsync(api.Server.DataDirectory, records)).Single();
        var snapshot = SnapshotPath(api, database, id);
        var snapshotted = File.Exists(snapshot);
        settlement.Dispose();
        if (created is not null)
        {
            using var response = await created;
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        }

        // The record is left snapshotting; once the attempt has ended, a later pass of the same process sweeps it.
        await WaitAsync(time, async () =>
            !(await ReadAsync(api.Server.DataDirectory, records)).ContainsKey(id)
            && !(await ReadAsync(api.Server.DataDirectory, "storage_sweep")).ContainsKey($"backup:{id}")
            && !File.Exists(snapshot));

        await Assert.That(record.GetProperty("state").GetString()).IsEqualTo("snapshotting");
        await Assert.That(record.GetProperty("trigger").GetString()).IsEqualTo(trigger);
        await Assert.That(snapshotted).IsTrue();
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
    public async Task Retention_expires_backups_by_completion_time_across_a_clock_step_back()
    {
        var time = new SteppedTime(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var backups = $"{database.Path}/backups";
        (await api.Client.PutAsJsonAsync(Uri($"{database.Path}/backup-policy"), new { fullBackupCron = "", retentionDays = 1 })).EnsureSuccessStatusCode();
        async Task<string> BackUpAsync()
        {
            using var created = await api.Client.PostAsync(Uri(backups), content: null);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
            return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        // The first backup completes at 10:30; then the wall clock is corrected 12 hours back, and the next two complete
        // at 22:30 the day before and at 08:30.
        var first = await BackUpAsync();
        time.Offset = TimeSpan.FromHours(-12);
        var earliest = await BackUpAsync();
        time.Advance(TimeSpan.FromHours(10));
        var middle = await BackUpAsync();
        var listed = await ListBackupsAsync(api, backups);
        var snapshot = SnapshotPath(api, database, earliest);

        // At 23:30 only the earliest is past the 1-day retention, although it was registered after the first. The list
        // stays in registration order.
        time.Advance(TimeSpan.FromHours(15));
        await WaitAsync(time, async () => (await ListBackupsAsync(api, backups)).Length == 2 && !File.Exists(snapshot));
        var kept = await ListBackupsAsync(api, backups);

        await Assert.That(string.Join(',', listed.Select(item => item.GetProperty("id").GetString()))).IsEqualTo(string.Join(',', first, earliest, middle));
        await Assert.That(string.Join(',', kept.Select(item => item.GetProperty("id").GetString()))).IsEqualTo(string.Join(',', first, middle));
    }

    [Test]
    public async Task Backup_routes_return_404_for_an_unknown_organization_project_or_database()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var unknown = EntityIds.New().ToString();
        string[] paths =
        [
            $"/v1/management/organizations/{unknown}/projects/{database.Project}/databases/{database.Id}",
            $"/v1/management/organizations/{database.Organization}/projects/{unknown}/databases/{database.Id}",
            $"{database.ProjectPath}/databases/{unknown}",
        ];

        var statuses = new List<HttpStatusCode>();
        foreach (var path in paths)
        {
            using var list = await api.Client.GetAsync(Uri($"{path}/backups"));
            using var create = await api.Client.PostAsync(Uri($"{path}/backups"), content: null);
            using var policy = await api.Client.GetAsync(Uri($"{path}/backup-policy"));
            using var setPolicy = await api.Client.PutAsJsonAsync(Uri($"{path}/backup-policy"), new { fullBackupCron = "0 2 * * *", retentionDays = 7 });
            using var restore = await api.Client.PostAsJsonAsync(Uri($"{path}/backups/{unknown}/restore"), new { });
            statuses.AddRange([list.StatusCode, create.StatusCode, policy.StatusCode, setPolicy.StatusCode, restore.StatusCode]);
        }

        await Assert.That(statuses).IsEquivalentTo(Enumerable.Repeat(HttpStatusCode.NotFound, 15));
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

    [Test]
    public async Task Every_database_gets_its_scheduled_backup_when_more_are_due_than_one_pass_takes()
    {
        var output = TUnit.Core.TestContext.Current!.OutputWriter;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        // Drive complete passes so polling never advances cron time during a snapshot copy.
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
            services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(BackupScheduler))));
        var organization = await api.CreateOrganizationAsync(Faker.Company.CompanyName());
        var project = await api.CreateProjectAsync(organization, Faker.Commerce.ProductName());
        var databases = new List<ManagedDatabaseRef>();
        for (var i = 0; i < 17; i++)
        {
            var database = await api.CreateManagedDatabaseAsync(organization, project, $"db-{i}");
            (await api.Client.PutAsJsonAsync(Uri($"{database.Path}/backup-policy"), new { fullBackupCron = "* * * * *", retentionDays = 35 })).EnsureSuccessStatusCode();
            databases.Add(database);
            output.WriteLine($"backup schedule: {databases.Count}/17 databases and policies configured");
        }

        var scheduler = api.Server.Services.GetRequiredService<IManagedBackupService>();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var backedUp = new HashSet<string>();
        for (var pass = 0; pass < 2; pass++)
        {
            time.AdjustTime(time.GetUtcNow() + TimeSpan.FromMinutes(1));
            await scheduler.RunScheduleAsync(token);
            foreach (var database in databases.Where(database => !backedUp.Contains(database.Id)))
            {
                if ((await ListBackupsAsync(api, $"{database.Path}/backups")).Any(item => item.GetProperty("trigger").GetString() == "scheduled"))
                {
                    backedUp.Add(database.Id);
                }
            }

            await Assert.That(backedUp.Count).IsEqualTo(pass == 0 ? 16 : 17);
            output.WriteLine($"backup schedule: {backedUp.Count}/17 scheduled backups observed after pass {pass + 1}");
        }

        await Assert.That(backedUp.Count).IsEqualTo(17);
        output.WriteLine("backup schedule: all 17 backups verified before cleanup");
    }

    [Test]
    public async Task A_weekly_backup_is_not_taken_again_after_retention_deletes_it()
    {
        // A Sunday; the cron runs on Mondays at midnight.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var backups = $"{database.Path}/backups";
        (await api.Client.PutAsJsonAsync(Uri($"{database.Path}/backup-policy"), new { fullBackupCron = "0 0 * * 1", retentionDays = 1 })).EnsureSuccessStatusCode();

        time.Advance(TimeSpan.FromHours(13.5));
        var monday = await WaitForBackupsAsync(api, time, backups, items => items.Length > 0);
        time.Advance(TimeSpan.FromDays(2));
        await WaitForCleanupAsync(api, time, database);

        // A database backed up every minute shows when later passes completed: each takes its backup only after
        // evaluating every database, so two of them mean the first pass after the cleanup finished entirely.
        var witness = await api.CreateManagedDatabaseAsync();
        (await api.Client.PutAsJsonAsync(Uri($"{witness.Path}/backup-policy"), new { fullBackupCron = "* * * * *", retentionDays = 35 })).EnsureSuccessStatusCode();
        await WaitForBackupsAsync(api, time, $"{witness.Path}/backups", items => items.Length >= 2);
        var later = await ListBackupsAsync(api, backups);

        await Assert.That(monday.Single().GetProperty("trigger").GetString()).IsEqualTo("scheduled");
        await Assert.That(later.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Retention_still_runs_when_the_scheduled_copy_fails()
    {
        // The passes run here: with the hosted scheduler, moving the fake clock two days would run one failing pass per minute.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
            services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType?.Name == "BackupScheduler")));
        var database = await api.CreateManagedDatabaseAsync();
        var backups = $"{database.Path}/backups";
        (await api.Client.PostAsync(Uri(backups), content: null)).EnsureSuccessStatusCode();
        (await api.Client.PutAsJsonAsync(Uri($"{database.Path}/backup-policy"), new { fullBackupCron = "* * * * *", retentionDays = 1 })).EnsureSuccessStatusCode();

        await SetNewerSchemaVersionAsync(api, database);

        time.Advance(TimeSpan.FromDays(2));
        // The first pass expires the backup and fails its scheduled copy; the second sweeps the expired files.
        var scheduler = api.Server.Services.GetRequiredService<IManagedBackupService>();
        await scheduler.RunScheduleAsync(CancellationToken.None);
        await scheduler.RunScheduleAsync(CancellationToken.None);

        await Assert.That((await ReadScheduleAsync(api, database))!.Value.GetProperty("lastFailedAt").ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That((await ListBackupsAsync(api, backups)).Length).IsEqualTo(0);
        await Assert.That(SnapshotFiles(api, database).Length).IsEqualTo(0);
        await Assert.That(await CountAsync(api.Server.DataDirectory, "storage_sweep")).IsEqualTo(0);
        await Assert.That(Directory.GetFiles(api.Server.DataDirectory, "*.creating", SearchOption.AllDirectories).Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("disabled")]
    [Arguments("cron")]
    [Arguments("success")]
    public async Task A_selected_schedule_is_rechecked_in_the_pending_transaction(string change)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
        {
            hook.Install(services);
            // This test owns the backup clock; usage's unrelated periodic writes have their own timer.
            var usage = services.Single(service => service.ImplementationType == typeof(TerraBase.Features.Management.Usage.UsageFlushService));
            services[services.IndexOf(usage)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                ActivatorUtilities.CreateInstance<TerraBase.Features.Management.Usage.UsageFlushService>(provider, new FakeTimeProvider(time.GetUtcNow())));
            services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType?.Name == "BackupScheduler"));
        });
        var database = await api.CreateManagedDatabaseAsync();
        var policy = Uri($"{database.Path}/backup-policy");
        (await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = "* * * * *", retentionDays = 35 })).EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromMinutes(1));

        // Selection has finished when the first write (pending creation) starts. Pause before it takes the write lock,
        // so a real policy request or another completed schedule can commit first.
        using var gate = hook.PauseWrite();
        var pass = api.Server.Services.GetRequiredService<IManagedBackupService>().RunScheduleAsync(CancellationToken.None).AsTask();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (change == "success")
            {
                // Share the engine's writer gate with the running catalog services.
                var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
                await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
                _ = await transaction.PutAsync($"databases/{database.Id}/backup_schedule", "last",
                    JsonSerializer.SerializeToUtf8Bytes(new { lastSucceededAt = time.GetUtcNow(), lastFailedAt = (DateTimeOffset?)null }), CancellationToken.None);
                _ = await transaction.CommitAsync(CancellationToken.None);
            }
            else
            {
                (await api.Client.PutAsJsonAsync(policy, new { fullBackupCron = change == "cron" ? "0 0 * * *" : "* * * * *", retentionDays = 35, disabled = change == "disabled" }))
                    .EnsureSuccessStatusCode();
            }
        }
        finally
        {
            gate.Dispose();
            await pass.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await Assert.That((await ListBackupsAsync(api, $"{database.Path}/backups")).Length).IsEqualTo(0);
        await Assert.That(await CountAsync(api.Server.DataDirectory, $"organizations/{database.Organization}/backups")).IsEqualTo(0);
        await Assert.That(await CountAsync(api.Server.DataDirectory, "storage_sweep")).IsEqualTo(0);
        await Assert.That(SnapshotFiles(api, database).Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_database_whose_scheduled_backups_keep_failing_does_not_starve_the_others(bool copyThrows)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero));
        // Own each pass so polling cannot advance the clock during a copy or leave the scheduler busy at cleanup.
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
            services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType?.Name == "BackupScheduler")));
        var organization = await api.CreateOrganizationAsync(Faker.Company.CompanyName());
        var project = await api.CreateProjectAsync(organization, Faker.Commerce.ProductName());
        var failed = new List<ManagedDatabaseRef>();

        // More failing databases than one pass takes, all due at the same time and sorting before the healthy one.
        for (var i = 0; i < 17; i++)
        {
            var failing = await api.CreateManagedDatabaseAsync(organization, project, $"failing-{i}");
            failed.Add(failing);
            (await api.Client.PutAsJsonAsync(Uri($"{failing.Path}/backup-policy"), new { fullBackupCron = "* * * * *", retentionDays = 35 })).EnsureSuccessStatusCode();
            // A copy that throws (it cannot open the snapshot) or one that copies nothing (the source files are gone
            // although the catalog still names the database): both are failed attempts.
            if (copyThrows)
            {
                await SetNewerSchemaVersionAsync(api, failing);
            }
            else
            {
                var storage = new TenantContext(failing.Organization, PlanTier.Hobby, failing.Project, failing.Id);
                await Assert.That(await api.Server.Services.GetRequiredService<IStorageEngine>().DeleteDatabaseAsync(storage, CancellationToken.None)).IsTrue();
            }
        }

        // Database ids use the application clock: a later millisecond sorts the healthy one after every failing one.
        time.AdjustTime(time.GetUtcNow() + TimeSpan.FromMilliseconds(1));
        var healthy = await api.CreateManagedDatabaseAsync(organization, project, "healthy");
        (await api.Client.PutAsJsonAsync(Uri($"{healthy.Path}/backup-policy"), new { fullBackupCron = "* * * * *", retentionDays = 35 })).EnsureSuccessStatusCode();
        await Assert.That(failed.All(database => string.CompareOrdinal(database.Id, healthy.Id) < 0)).IsTrue();

        var scheduler = api.Server.Services.GetRequiredService<IManagedBackupService>();
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        time.AdjustTime(time.GetUtcNow() + TimeSpan.FromMinutes(1));
        await scheduler.RunScheduleAsync(cancellationToken);
        foreach (var database in failed.Take(16))
        {
            var schedule = (await ReadScheduleAsync(api, database))!.Value;
            await Assert.That(schedule.GetProperty("lastFailedAt").ValueKind).IsEqualTo(JsonValueKind.String);
        }

        await Assert.That(await ReadScheduleAsync(api, failed[^1])).IsNull();
        await Assert.That((await ListBackupsAsync(api, $"{healthy.Path}/backups")).Length).IsEqualTo(0);

        // The unattempted failing database and the healthy one precede retries on the second pass.
        time.AdjustTime(time.GetUtcNow() + TimeSpan.FromMinutes(1));
        await scheduler.RunScheduleAsync(cancellationToken);
        var backups = await ListBackupsAsync(api, $"{healthy.Path}/backups");

        await Assert.That(backups.Single().GetProperty("trigger").GetString()).IsEqualTo("scheduled");
        foreach (var database in failed)
        {
            var schedule = (await ReadScheduleAsync(api, database))!.Value;
            await Assert.That(schedule.GetProperty("lastFailedAt").ValueKind).IsEqualTo(JsonValueKind.String);
            await Assert.That(schedule.GetProperty("lastSucceededAt").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That((await ListBackupsAsync(api, $"{database.Path}/backups")).Length).IsEqualTo(0);
        }
    }

    private static async Task<JsonElement?> ReadScheduleAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var document = await transaction.GetAsync($"databases/{database.Id}/backup_schedule", "last", CancellationToken.None);
        return document is null ? null : JsonSerializer.Deserialize<JsonElement>(document.Json.Span);
    }

    // A copy of a database with a newer schema version fails to open, so every scheduled copy of it throws.
    private static async Task SetNewerSchemaVersionAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        var path = Path.Combine(ProjectDirectory(api, database), $"{database.Id}.db");
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version = 99;";
        await command.ExecuteNonQueryAsync();
    }

    // Expiry unlists a backup first and the sweep deletes its files after; waits until no backup, backup file or sweep
    // entry is left.
    private static Task WaitForCleanupAsync(ManagementApi api, FakeTimeProvider time, ManagedDatabaseRef database) =>
        WaitAsync(time, async () =>
            (await ListBackupsAsync(api, $"{database.Path}/backups")).Length == 0
            && SnapshotFiles(api, database).Length == 0
            && await CountAsync(api.Server.DataDirectory, "storage_sweep") == 0);

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
    private static async Task InsertAsync(string dataDirectory, string collection, string id, string body)
    {
        var path = Path.Combine(dataDirectory, "_system.db");
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }));
        await connection.OpenAsync();
        await StoredStates.WriteAsync(connection, collection, id, body, system: true);
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);

    /// <summary>A fake clock whose wall time is moved by <see cref="Offset"/>, as a clock correction does; timers keep the fake time.</summary>
    private sealed class SteppedTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }
}
