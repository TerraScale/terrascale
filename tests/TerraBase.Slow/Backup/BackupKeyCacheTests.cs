using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class BackupKeyCacheTests
{
    [Test]
    public async Task A_cold_node_reuses_more_than_1024_historical_keys_across_repeated_restores()
    {
        const int peerKeys = 1025;
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var directory = Directory.CreateTempSubdirectory("terrabase-many-keys-").FullName;
        var coldDirectory = Directory.CreateTempSubdirectory("terrabase-many-keys-cold-").FullName;
        var text = new Faker().Lorem.Sentence(8);
        try
        {
            ManagedDatabaseRef database;
            string user;
            string snapshotKey;
            var peerKeyIds = new string[peerKeys];
            var points = new Dictionary<string, (DateTimeOffset At, int Index)>(StringComparer.Ordinal);
            await using (var live = await StartAsync(directory))
            {
                database = await live.CreateManagedDatabaseAsync();
                user = live.User;
                await live.SetPlanAsync(user, database.Organization, "enterprise", cancellationToken);
                using var data = await live.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(data, database, "items");
                using (var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "before")), new { text }, cancellationToken))
                {
                    written.EnsureSuccessStatusCode();
                }

                var backup = live.Server.Services.GetRequiredService<IContinuousBackup>();
                time.Advance(TimeSpan.FromSeconds(10));
                await backup.RunOnceAsync(cancellationToken);
                var file = FilePrefix(database);
                var snapshot = s3.Objects.Single(item => item.Key.StartsWith(file + "/snapshots/", StringComparison.Ordinal));
                var keys = Keys(database);
                snapshotKey = keys + KeyId(snapshot.Value);
                var generated = s3.GeneratedKeys;

                // Simulate legitimate peer publication using FakeS3's existing wrapped-key contract. Advancing only
                // 61 seconds per key refreshes the live selection. WAL restarts may create additional generations;
                // retain each generation's last restore point so every published key is read by the cold node.
                for (var index = 0; index < peerKeys; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var next = time.GetUtcNow() + TimeSpan.FromSeconds(61);
                    var id = peerKeyIds[index] = Ulid.NewUlid(next).ToString();
                    s3.StoreObject(keys + id, Encoding.ASCII.GetBytes(s3.Wrap(RandomNumberGenerator.GetBytes(32))));
                    time.Advance(TimeSpan.FromSeconds(61));
                    using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "state")), new { index, text }, cancellationToken);
                    written.EnsureSuccessStatusCode();
                    await backup.RunOnceAsync(cancellationToken);
                    var status = backup.Status.Files.Single(status => status.File == file);
                    await Assert.That(status.LastError).IsNull();
                    points[status.Generation!] = (status.LastShippedAt!.Value, index);
                }

                var payloads = s3.Objects.Where(item => item.Key.StartsWith(file + "/", StringComparison.Ordinal)).ToArray();
                await Assert.That(payloads.Select(item => KeyId(item.Value)).Distinct(StringComparer.Ordinal))
                    .IsEquivalentTo(peerKeyIds.Append(KeyId(snapshot.Value)));
                await Assert.That(s3.Objects.Keys.Count(key => key.StartsWith(file + "/snapshots/", StringComparison.Ordinal))).IsEqualTo(points.Count);
                await Assert.That(s3.GeneratedKeys).IsEqualTo(generated);
                await Assert.That(backup.Status.Files.Single(status => status.File == file).LastError).IsNull();
            }

            // Only the stopped catalog and layout marker reach the cold node, never the source database or its cache.
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                File.Copy(file, Path.Combine(coldDirectory, Path.GetFileName(file)));
            }

            var prefix = Keys(database);
            var keyGets = 0;
            var parallelKeys = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            s3.BeforeGet = async (key, token) =>
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var count = Interlocked.Increment(ref keyGets);
                    if (count >= 3)
                    {
                        parallelKeys.TrySetResult();
                    }

                    if (count == 2)
                    {
                        // The snapshot uses the first GET. Hold the first WAL key until another distinct key starts,
                        // proving the prefetch window can retrieve cold keys concurrently within the same scope.
                        await parallelKeys.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                    }
                }
            };
            var snapshotRequests = s3.ObserveKey(snapshotKey);
            var latestRequests = s3.ObserveKey(prefix + peerKeyIds[^1]);
            await using var cold = await StartAsync(coldDirectory);
            // Complete initial discovery before importing either restored file. With the clock frozen, the shipper
            // cannot discover those copies and warm a current project key between the two historical reads.
            await cold.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
            await Assert.That(File.Exists(Path.Combine(coldDirectory, database.Organization, database.Project, database.Id + ".db"))).IsFalse();
            using var owner = cold.As(user);
            using var reader = await cold.CreateClientAsync(database.Organization, "documents:read");
            var coldGenerated = s3.GeneratedKeys;
            for (var round = 0; round < 2; round++)
            {
                foreach (var point in points.Values.OrderBy(point => point.At))
                {
                    var name = $"restored-{round}-{point.Index}";
                    var path = $"/v1/projects/{database.Project}/databases/{name}";
                    using var restored = await owner.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime = point.At, name }, cancellationToken);
                    await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    using var before = await reader.GetAsync(ManagementApi.Uri($"{path}/collections/items/documents/by-key?id={ManagementApi.DocumentId("before")}"), cancellationToken);
                    before.EnsureSuccessStatusCode();
                    await Assert.That((await before.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
                    using var state = await reader.GetAsync(ManagementApi.Uri($"{path}/collections/items/documents/by-key?id={ManagementApi.DocumentId("state")}"), cancellationToken);
                    state.EnsureSuccessStatusCode();
                    await Assert.That((await state.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("index").GetInt32()).IsEqualTo(point.Index);
                    using var deleted = await owner.DeleteAsync(restored.Headers.Location, cancellationToken);
                    deleted.EnsureSuccessStatusCode();
                }

                // Every generation's snapshot and WAL keys are read once in the first round, then reused in the second.
                // Counting the entire prefix catches whichever key the former 1,024-entry cache rejected or evicted.
                await Assert.That(Volatile.Read(ref keyGets)).IsEqualTo(peerKeys + 1);
                await Assert.That(parallelKeys.Task.IsCompletedSuccessfully).IsTrue();
                foreach (var requests in new[] { snapshotRequests, latestRequests })
                {
                    await Assert.That(requests.Gets).IsEqualTo(1);
                    await Assert.That(requests.Unwraps).IsEqualTo(1);
                }

                await Assert.That(s3.GeneratedKeys).IsEqualTo(coldGenerated);
            }

            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(coldDirectory, recursive: true);
        }

        async Task<ManagementApi> StartAsync(string dataDirectory)
        {
            var api = await ManagementApi.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = dataDirectory;
            }, services =>
            {
                s3.Route(services);
                ManualBackupHosting.Configure(services);
            }, time);
            return api;
        }
    }

    [Test]
    public async Task An_idle_cold_node_reloads_a_retained_key_once_after_both_key_caches_expire()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var cancellationToken = deadline.Token;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var directory = Directory.CreateTempSubdirectory("terrabase-key-expiry-").FullName;
        var coldDirectory = Directory.CreateTempSubdirectory("terrabase-key-expiry-cold-").FullName;
        var text = new Faker().Lorem.Sentence(8);
        try
        {
            ManagedDatabaseRef database;
            string user;
            DateTimeOffset pointInTime;
            await using (var live = await StartAsync(directory))
            {
                database = await live.CreateManagedDatabaseAsync();
                user = live.User;
                await live.SetPlanAsync(live.User, database.Organization, "pro", cancellationToken);

                using var data = await live.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(data, database, "items");
                using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "before")), new { text }, cancellationToken);
                written.EnsureSuccessStatusCode();
                time.Advance(TimeSpan.FromSeconds(10));
                var backup = live.Server.Services.GetRequiredService<IContinuousBackup>();
                await backup.RunOnceAsync(cancellationToken);
                var status = backup.Status.Files.Single(status => status.File == FilePrefix(database));
                await Assert.That(status.LastError).IsNull();
                pointInTime = status.LastShippedAt!.Value;
            }

            // Copy only the stopped catalog, its intent journal and layout marker, leaving the source's physical database behind.
            foreach (var file in new[] { "_system.db", "_system.db.key", "_intents.db", "_intents.db.key", ".terrabase.lock" })
            {
                File.Copy(Path.Combine(directory, file), Path.Combine(coldDirectory, file));
            }

            var keys = Keys(database);
            var persistedKey = s3.Objects.Keys.Single(key => key.StartsWith(keys, StringComparison.Ordinal));
            var requests = s3.ObserveKey(persistedKey);
            var generated = s3.GeneratedKeys;
            await using var cold = await StartAsync(coldDirectory);
            await Assert.That(cold.Server.Services.GetRequiredService<TerraBase.Configuration.TerraBaseOptions>().Backup!.KeyRotationDays).IsEqualTo(90);
            var coldBackup = cold.Server.Services.GetRequiredService<IContinuousBackup>();
            await coldBackup.RunOnceAsync(cancellationToken);
            var projectDirectory = Path.Combine(coldDirectory, database.Organization, database.Project);
            var projectFiles = $"v1/{database.Organization}/{database.Project}/";
            await Assert.That(File.Exists(Path.Combine(projectDirectory, database.Id + ".db"))).IsFalse();
            await Assert.That(coldBackup.Status.Files.Any(status => status.File.StartsWith(projectFiles, StringComparison.Ordinal))).IsFalse();
            await Assert.That(requests.Gets).IsEqualTo(0);
            await Assert.That(requests.Unwraps).IsEqualTo(0);
            using var owner = cold.As(user);
            using var reader = await cold.CreateClientAsync(database.Organization, "documents:read");
            for (var round = 0; round < 2; round++)
            {
                var location = await RestoreAsync($"warm-{round}");
                if (round == 0)
                {
                    // The historical restore primes _keys; shipping its copy also selects that key into _current.
                    // Without both caches being warm, expiring only one could incorrectly pass this regression.
                    time.Advance(TimeSpan.FromSeconds(10));
                    await coldBackup.RunOnceAsync(cancellationToken);
                    var shipped = coldBackup.Status.Files.Single(status => status.File.StartsWith(projectFiles, StringComparison.Ordinal));
                    await Assert.That(shipped.LastError).IsNull();
                    await Assert.That(shipped.Generation).IsNotNull();
                    var snapshot = s3.Objects.Single(item => item.Key.StartsWith(shipped.File + "/snapshots/", StringComparison.Ordinal));
                    await Assert.That(KeyId(snapshot.Value)).IsEqualTo(persistedKey[keys.Length..]);
                }

                await Assert.That(requests.Gets).IsEqualTo(1);
                await Assert.That(requests.Unwraps).IsEqualTo(1);
                using var deleted = await owner.DeleteAsync(location, cancellationToken);
                deleted.EnsureSuccessStatusCode();
                time.Advance(TimeSpan.FromSeconds(10));
                await coldBackup.RunOnceAsync(cancellationToken);
            }

            // The original catalog record stays restorable. No project file or tap can refresh either cache while
            // time moves; the managed scheduler's copy of a missing source returns false without creating a file.
            await Assert.That(Directory.Exists(projectDirectory) && Directory.EnumerateFiles(projectDirectory, "*.db").Any()).IsFalse();
            await Assert.That(coldBackup.Status.Files.Any(status => status.File.StartsWith(projectFiles, StringComparison.Ordinal))).IsFalse();
            await Assert.That(s3.GeneratedKeys).IsEqualTo(generated);
            time.Advance(TimeSpan.FromHours(25));
            await coldBackup.RunOnceAsync(cancellationToken);
            await Assert.That(Directory.Exists(projectDirectory) && Directory.EnumerateFiles(projectDirectory, "*.db").Any()).IsFalse();
            await Assert.That(coldBackup.Status.Files.Any(status => status.File.StartsWith(projectFiles, StringComparison.Ordinal))).IsFalse();
            await Assert.That(requests.Gets).IsEqualTo(1);
            await Assert.That(requests.Unwraps).IsEqualTo(1);
            for (var round = 0; round < 2; round++)
            {
                // Frozen time keeps discovery from shipping these copies between the expired and immediate reads.
                _ = await RestoreAsync($"expired-{round}");
                await Assert.That(requests.Gets).IsEqualTo(2);
                await Assert.That(requests.Unwraps).IsEqualTo(2);
                await Assert.That(s3.GeneratedKeys).IsEqualTo(generated);
                await Assert.That(s3.Objects.Keys.Where(key => key.StartsWith(keys, StringComparison.Ordinal))).IsEquivalentTo([persistedKey]);
            }

            await Assert.That(s3.BadSignatures).IsEqualTo(0);

            async Task<Uri> RestoreAsync(string name)
            {
                using var restored = await owner.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime, name }, cancellationToken);
                await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
                using var read = await reader.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{name}/collections/items/documents/by-key?id={ManagementApi.DocumentId("before")}"), cancellationToken);
                read.EnsureSuccessStatusCode();
                await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
                return restored.Headers.Location!;
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(coldDirectory, recursive: true);
        }

        async Task<ManagementApi> StartAsync(string dataDirectory)
        {
            var api = await ManagementApi.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = dataDirectory;
            }, services =>
            {
                s3.Route(services);
                ManualBackupHosting.Configure(services);
            }, time);
            return api;
        }
    }

    private static string FilePrefix(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private static string Keys(ManagedDatabaseRef database) => $"v1/keys/{database.Organization}/{database.Project}/";

    private static string KeyId(byte[] content) => Encoding.ASCII.GetString(content, 4, 26);
}
