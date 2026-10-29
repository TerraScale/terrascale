using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Features.Management.Backups;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupKeyRotationTests
{
    [Test]
    public async Task Generated_keys_remain_cached_for_restore_after_rotation_and_stray_key_objects_are_ignored()
    {
        var elapsed = Stopwatch.StartNew();
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var directory = Directory.CreateTempSubdirectory("terrabase-key-history-").FullName;
        var before = new Faker().Lorem.Sentence(12);
        var after = new Faker().Lorem.Sentence(12);
        var phases = new (double Elapsed, double Cpu, double Pause)[5];
        void Mark(int index)
        {
            phases[index] = (elapsed.Elapsed.TotalSeconds, Environment.CpuUsage.TotalTime.TotalSeconds, GC.GetTotalPauseDuration().TotalSeconds);
        }
        Mark(0);
        try
        {
            await using var api = await StartAsync(s3, time, directory);
            Mark(1);
            var (database, _) = await SeedAsync(api, time, before);
            Mark(2);
            var keys = Keys(database);
            var oldKey = s3.Objects.Keys.Single(key => key.StartsWith(keys, StringComparison.Ordinal));
            var oldRequests = s3.ObserveKey(oldKey);
            s3.StoreObject(keys + "zz-unrelated", "unrelated"u8.ToArray());
            s3.StoreObject(keys + "zz-directory/" + oldKey[keys.Length..], "unrelated"u8.ToArray());
            var nestedKey = keys + Ulid.NewUlid(time.GetUtcNow().AddSeconds(1)).ToString()[..10] + "Z/" + new string('Z', 14);
            s3.StoreObject(nestedKey, "unrelated"u8.ToArray());
            var nestedRequests = s3.ObserveKey(nestedKey);
            var futureKey = keys + Ulid.NewUlid(time.GetUtcNow().AddYears(10)).ToString();
            s3.StoreObject(futureKey, Encoding.ASCII.GetBytes(s3.Wrap(RandomNumberGenerator.GetBytes(32))));
            var futureRequests = s3.ObserveKey(futureKey);
            var outOfRangeKey = keys + "7" + new string('Z', 25);
            s3.StoreObject(outOfRangeKey, Encoding.ASCII.GetBytes(s3.Wrap(RandomNumberGenerator.GetBytes(32))));
            var outOfRangeRequests = s3.ObserveKey(outOfRangeKey);
            var generated = s3.GeneratedKeys;
            var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
            using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            Mark(3);

            time.Advance(TimeSpan.FromHours(23));
            Mark(4);
            using var refreshed = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "refresh")), new { text = before }, cancellationToken);
            refreshed.EnsureSuccessStatusCode();
            await backup.RunOnceAsync(cancellationToken);
            await Assert.That(s3.GeneratedKeys).IsEqualTo(generated);
            await Assert.That(s3.Objects.Where(item => item.Key.StartsWith(FilePrefix(database) + "/", StringComparison.Ordinal))
                .All(item => KeyId(item.Value) == oldKey[keys.Length..])).IsTrue();
            await Assert.That(futureRequests.Gets).IsEqualTo(0);
            await Assert.That(futureRequests.Unwraps).IsEqualTo(0);
            await Assert.That(outOfRangeRequests.Gets).IsEqualTo(0);
            await Assert.That(outOfRangeRequests.Unwraps).IsEqualTo(0);
            await Assert.That(nestedRequests.Gets).IsEqualTo(0);
            await Assert.That(nestedRequests.Unwraps).IsEqualTo(0);
            await Assert.That((await StatusAsync(api, database, cancellationToken)).GetProperty("lastError").ValueKind).IsEqualTo(JsonValueKind.Null);
            var pointInTime = time.GetUtcNow();

            time.Advance(TimeSpan.FromHours(2));
            using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "after")), new { text = after }, cancellationToken);
            written.EnsureSuccessStatusCode();
            await backup.RunOnceAsync(cancellationToken);
            await Assert.That(s3.GeneratedKeys).IsEqualTo(generated + 2);
            time.Advance(TimeSpan.FromSeconds(1));

            using var restored = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime, name = "restored" }, cancellationToken);
            await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
            foreach (var name in new[] { "before", "refresh" })
            {
                using var read = await data.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/restored/collections/people/documents/by-key?id={ManagementApi.DocumentId(name)}"), cancellationToken);
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(before);
            }

            using var later = await data.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/restored/collections/people/documents/by-key?id={ManagementApi.DocumentId("after")}"), cancellationToken);
            await Assert.That(later.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(oldRequests.Gets).IsEqualTo(0);
            await Assert.That(oldRequests.Unwraps).IsEqualTo(0);
            await Assert.That(futureRequests.Gets).IsEqualTo(0);
            await Assert.That(futureRequests.Unwraps).IsEqualTo(0);
            await Assert.That(outOfRangeRequests.Gets).IsEqualTo(0);
            await Assert.That(outOfRangeRequests.Unwraps).IsEqualTo(0);
            await Assert.That(nestedRequests.Gets).IsEqualTo(0);
            await Assert.That(nestedRequests.Unwraps).IsEqualTo(0);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Backup rotation setup: phases=prepared,host,seed,client,advance23h; index,elapsed_s,cpu_s,gc_pause_s");
            for (var index = 0; index < phases.Length; index++)
            {
                var (elapsedSeconds, cpuSeconds, pauseSeconds) = phases[index];
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Backup rotation setup: {index},{elapsedSeconds:F3},{cpuSeconds:F3},{pauseSeconds:F3}"));
            }

            throw;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_live_node_refreshes_its_unexpired_current_key_from_object_storage()
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var directory = Directory.CreateTempSubdirectory("terrabase-key-current-").FullName;
        try
        {
            var (database, _) = await SeedAsync(s3, time, directory, new Faker().Lorem.Sentence(8), rotation: "");
            var keys = Keys(database);
            var oldKey = s3.Objects.Keys.Single(key => key.StartsWith(keys, StringComparison.Ordinal));
            var oldRequests = s3.ObserveKey(oldKey);
            s3.ObservedListPrefix = keys;
            await using var live = await StartAsync(s3, time, directory, rotation: "");
            await Assert.That(live.Server.Services.GetRequiredService<TerraBaseOptions>().Backup!.KeyRotationDays).IsEqualTo(90);
            var backup = live.Server.Services.GetRequiredService<IContinuousBackup>();
            await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            var lists = s3.ListRequests;
            await Assert.That(lists).IsGreaterThan(0);
            await Assert.That(s3.LastListStartAfter).IsEqualTo("");
            await Assert.That(oldRequests.Gets).IsEqualTo(1);
            await Assert.That(oldRequests.Unwraps).IsEqualTo(1);
            using var data = await live.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            time.Advance(TimeSpan.FromHours(23));
            using var warm = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "keep-warm")), new { text = new Faker().Lorem.Sentence(8) });
            warm.EnsureSuccessStatusCode();
            await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await Assert.That(s3.ListRequests).IsEqualTo(lists + 1);
            await Assert.That(s3.LastListStartAfter).IsEqualTo(oldKey);
            await Assert.That(oldRequests.Gets).IsEqualTo(1);
            await Assert.That(oldRequests.Unwraps).IsEqualTo(1);
            lists = s3.ListRequests;
            time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
            await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await Assert.That(s3.Objects.Keys.Count(key => key.StartsWith(keys, StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(s3.ListRequests).IsEqualTo(lists + 1);
            await Assert.That(s3.LastListStartAfter).IsEqualTo(oldKey);
            await Assert.That(oldRequests.Gets).IsEqualTo(1);
            await Assert.That(oldRequests.Unwraps).IsEqualTo(1);
            var priorSegments = s3.Objects.Keys.Where(key => key.StartsWith(FilePrefix(database) + "/wal/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            await Assert.That(priorSegments.All(key => KeyId(s3.Objects[key]) == oldKey[keys.Length..])).IsTrue();
            var newest = keys + Ulid.NewUlid(time.GetUtcNow()).ToString();
            var rotatedKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                s3.StoreObject(newest, Encoding.ASCII.GetBytes(s3.Wrap(rotatedKey)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rotatedKey);
            }
            var newRequests = s3.ObserveKey(newest);
            var generated = s3.GeneratedKeys;
            lists = s3.ListRequests;

            time.Advance(TimeSpan.FromSeconds(61));
            using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "after-rotation")), new { text = new Faker().Lorem.Sentence(8) });
            written.EnsureSuccessStatusCode();
            await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            var status = await StatusAsync(live, database);
            var walPrefix = FilePrefix(database) + "/wal/" + status.GetProperty("generation").GetString() + "/";
            var segments = s3.Objects.Where(item => item.Key.StartsWith(walPrefix, StringComparison.Ordinal) && !priorSegments.Contains(item.Key)).ToArray();
            await Assert.That(segments.Length).IsGreaterThan(0);
            await Assert.That(segments.All(item => KeyId(item.Value) == newest[keys.Length..])).IsTrue();
            await Assert.That(s3.ListRequests).IsEqualTo(lists + 1);
            await Assert.That(s3.LastListStartAfter).IsEqualTo(oldKey);
            await Assert.That(newRequests.Gets).IsEqualTo(1);
            await Assert.That(newRequests.Unwraps).IsEqualTo(1);
            lists = s3.ListRequests;
            time.Advance(TimeSpan.FromSeconds(61));
            using var unchanged = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "after-refresh")), new { text = new Faker().Lorem.Sentence(8) });
            unchanged.EnsureSuccessStatusCode();
            await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            var refreshed = s3.Objects.Where(item => item.Key.StartsWith(walPrefix, StringComparison.Ordinal) && !priorSegments.Contains(item.Key)).ToArray();
            await Assert.That(refreshed.Length).IsGreaterThan(segments.Length);
            await Assert.That(refreshed.All(item => KeyId(item.Value) == newest[keys.Length..])).IsTrue();
            await Assert.That(s3.ListRequests).IsEqualTo(lists + 1);
            await Assert.That(s3.LastListStartAfter).IsEqualTo(newest);
            await Assert.That(newRequests.Gets).IsEqualTo(1);
            await Assert.That(newRequests.Unwraps).IsEqualTo(1);
            await Assert.That(oldRequests.Gets).IsEqualTo(1);
            await Assert.That(oldRequests.Unwraps).IsEqualTo(1);
            await Assert.That(s3.GeneratedKeys).IsEqualTo(generated);
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<ManagementApi> StartAsync(FakeS3 s3, FakeTimeProvider time, string directory, string rotation = "1")
    {
        var api = await ManagementApi.StartAsync(settings =>
        {
            s3.Configure(settings);
            settings["data-directory"] = directory;
            settings["backup-key-rotation-days"] = rotation;

        }, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            var scheduler = services.Single(service => service.ServiceType == typeof(IHostedService)
                && service.ImplementationType == typeof(BackupScheduler));
            services[services.IndexOf(scheduler)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                ActivatorUtilities.CreateInstance<BackupScheduler>(provider, new FakeTimeProvider(time.GetUtcNow())));
        }, time);

        return api;
    }

    private static async Task<(ManagedDatabaseRef Database, string User)> SeedAsync(
        FakeS3 s3, FakeTimeProvider time, string directory, string text, string rotation = "1")
    {
        await using var api = await StartAsync(s3, time, directory, rotation);
        return await SeedAsync(api, time, text);
    }

    private static async Task<(ManagedDatabaseRef Database, string User)> SeedAsync(ManagementApi api, FakeTimeProvider time, string text)
    {
        var database = await api.CreateManagedDatabaseAsync("main");
        await api.SetPlanAsync(api.User, database.Organization, "pro");

        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "before")), new { text });
        written.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        return (database, api.User);
    }

    private static async Task<JsonElement> StatusAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken cancellationToken = default)
    {
        using var response = await api.Server.Client.GetAsync(ManagementApi.Uri("/v1/backup"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return status.GetProperty("files").EnumerateArray().Single(file => file.GetProperty("file").GetString() == FilePrefix(database));
    }

    private static string FilePrefix(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private static string Keys(ManagedDatabaseRef database) => $"v1/keys/{database.Organization}/{database.Project}/";

    private static string KeyId(byte[] content) => Encoding.ASCII.GetString(content, 4, 26);
}
