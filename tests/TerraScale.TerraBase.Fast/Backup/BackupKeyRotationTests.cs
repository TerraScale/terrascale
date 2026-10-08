using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupKeyRotationTests
{
    [Test]
    public async Task Generated_keys_remain_cached_for_restore_after_rotation_and_stray_key_objects_are_ignored()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var cancellationToken = deadline.Token;
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var directory = Directory.CreateTempSubdirectory("terrabase-key-history-").FullName;
        var before = new Faker().Lorem.Sentence(12);
        var after = new Faker().Lorem.Sentence(12);
        try
        {
            await using var api = await StartAsync(oci, time, directory);
            var (database, _) = await SeedAsync(api, time, before);
            var keys = Keys(database);
            var oldKey = oci.Objects.Keys.Single(key => key.StartsWith(keys, StringComparison.Ordinal));
            var oldRequests = oci.ObserveKey(oldKey);
            oci.StoreObject(keys + "zz-unrelated", "unrelated"u8.ToArray());
            oci.StoreObject(keys + "zz-directory/" + oldKey[keys.Length..], "unrelated"u8.ToArray());
            var nestedKey = keys + Ulid.NewUlid(time.GetUtcNow().AddSeconds(1)).ToString()[..10] + "Z/" + new string('Z', 14);
            oci.StoreObject(nestedKey, "unrelated"u8.ToArray());
            var nestedRequests = oci.ObserveKey(nestedKey);
            var futureKey = keys + Ulid.NewUlid(time.GetUtcNow().AddYears(10)).ToString();
            oci.StoreObject(futureKey, Encoding.ASCII.GetBytes("wrapped:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
            var futureRequests = oci.ObserveKey(futureKey);
            var outOfRangeKey = keys + "7" + new string('Z', 25);
            oci.StoreObject(outOfRangeKey, Encoding.ASCII.GetBytes("wrapped:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
            var outOfRangeRequests = oci.ObserveKey(outOfRangeKey);
            var generated = oci.GeneratedKeys;
            var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
            using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");

            time.Advance(TimeSpan.FromHours(23));
            using var refreshed = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "refresh")), new { text = before }, cancellationToken);
            refreshed.EnsureSuccessStatusCode();
            await backup.RunOnceAsync(cancellationToken);
            await Assert.That(oci.GeneratedKeys).IsEqualTo(generated);
            await Assert.That(oci.Objects.Where(item => item.Key.StartsWith(FilePrefix(database) + "/", StringComparison.Ordinal))
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
            await Assert.That(oci.GeneratedKeys).IsEqualTo(generated + 2); // Project and system keys rotate independently.
            time.Advance(TimeSpan.FromSeconds(1));

            using var restored = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime, name = "restored" }, cancellationToken);
            await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
            foreach (var name in new[] { "before", "refresh" })
            {
                using var read = await data.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/restored/collections/people/documents/{ManagementApi.DocumentId(name)}"), cancellationToken);
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(before);
            }

            using var later = await data.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/restored/collections/people/documents/{ManagementApi.DocumentId("after")}"), cancellationToken);
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
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_cold_node_restores_with_an_uncached_historical_key_while_current_key_publication_is_blocked()
    {
        var testCancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var directory = Directory.CreateTempSubdirectory("terrabase-key-publication-").FullName;
        var coldDirectory = Directory.CreateTempSubdirectory("terrabase-key-publication-cold-").FullName;
        var text = new Faker().Lorem.Sentence(12);
        try
        {
            // SeedAsync stops the source with a pro plan, keeping this point inside its seven-day restore window.
            var (database, user) = await SeedAsync(oci, time, directory, text);
            var pointInTime = time.GetUtcNow();
            var keys = Keys(database);
            var oldKey = oci.Objects.Keys.Single(key => key.StartsWith(keys, StringComparison.Ordinal));
            var oldRequests = oci.ObserveKey(oldKey);
            var generated = oci.GeneratedKeys;
            CopyCatalog(directory, coldDirectory);
            var coldFile = DatabaseFile(coldDirectory, database);
            Directory.CreateDirectory(Path.GetDirectoryName(coldFile)!);
            File.Copy(DatabaseFile(directory, database), coldFile);
            time.Advance(TimeSpan.FromHours(25));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var cancellationToken = deadline.Token;
            var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            oci.BeforePut = async (key, token) =>
            {
                if (key.StartsWith(keys, StringComparison.Ordinal))
                {
                    publishing.TrySetResult();
                    using var barrier = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                    await release.Task.WaitAsync(barrier.Token);
                }
            };

            await using var cold = await StartAsync(oci, time, coldDirectory);
            var backup = cold.Server.Services.GetRequiredService<IContinuousBackup>();
            // ExecuteAsync may already own the pass. This call also ensures shipping completes after the PUT is released.
            var pass = backup.RunOnceAsync(cancellationToken);
            Exception? primaryFailure = null;
            try
            {
                await publishing.Task.WaitAsync(cancellationToken);
                await Assert.That(pass.IsCompleted).IsFalse();
                await Assert.That(oldRequests.Gets).IsEqualTo(0);
                await Assert.That(oldRequests.Unwraps).IsEqualTo(0);
                await Assert.That(oci.Objects.Keys.Count(key => key.StartsWith(keys, StringComparison.Ordinal))).IsEqualTo(1);

                using var owner = cold.As(user);
                using var restored = await owner.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime, name = "restored" }, cancellationToken);
                await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
                using var reader = await cold.CreateClientAsync(database.Organization, "documents:read");
                using var read = await reader.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/restored/collections/people/documents/{ManagementApi.DocumentId("before")}"), cancellationToken);
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
                await Assert.That(oldRequests.Gets).IsEqualTo(1);
                await Assert.That(oldRequests.Unwraps).IsEqualTo(1);
                await Assert.That(release.Task.IsCompleted).IsFalse();
                await Assert.That(deadline.IsCancellationRequested).IsFalse();
                await Assert.That(pass.IsCompleted).IsFalse();
            }
            catch (Exception error)
            {
                primaryFailure = error;
                throw;
            }
            finally
            {
                // Release before disposing the server, even when the restore times out under the old scope gate.
                release.TrySetResult();
                try
                {
                    await pass.WaitAsync(TimeSpan.FromSeconds(30), testCancellationToken);
                }
                catch (Exception error) when (primaryFailure is not null)
                {
                    primaryFailure.Data["BackupCompletionFailure"] = error;
                }
            }

            var newKey = oci.Objects.Keys.Single(key => key.StartsWith(keys, StringComparison.Ordinal) && key != oldKey);
            await Assert.That(oci.Objects.Any(item => item.Key.StartsWith(FilePrefix(database) + "/snapshots/", StringComparison.Ordinal)
                && KeyId(item.Value) == newKey[keys.Length..])).IsTrue();
            await Assert.That((await StatusAsync(cold, database, cancellationToken)).GetProperty("lastError").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(oci.GeneratedKeys).IsEqualTo(generated + 2);
            await Assert.That(oldRequests.Gets).IsEqualTo(1);
            await Assert.That(oldRequests.Unwraps).IsEqualTo(1);
            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(coldDirectory, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Rotation_preserves_a_mixed_key_generation_and_recovers_failed_key_publication_on_a_cold_node(bool failPublication)
    {
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var directory = Directory.CreateTempSubdirectory("terrabase-key-rotation-").FullName;
        var coldDirectory = Directory.CreateTempSubdirectory("terrabase-key-restore-").FullName;
        var faker = new Faker();
        var before = faker.Lorem.Sentence(12);
        var after = faker.Lorem.Sentence(12);
        var extraDocuments = Enumerable.Range(0, 7).Select(index => (Name: $"after-{index}", Text: faker.Lorem.Sentence(12))).ToArray();
        try
        {
            var (database, user) = await SeedAsync(oci, time, directory, before);
            var keys = Keys(database);
            var oldKey = oci.Objects.Keys.Single(key => key.StartsWith(keys, StringComparison.Ordinal));
            var oldSystemKey = oci.Objects.Keys.Single(key => key.StartsWith("v1/keys/_system/", StringComparison.Ordinal));
            string newKey;
            string walPrefix;
            DateTimeOffset pointInTime;

            // The persisted ULID is already 23 hours old. This restart takes a snapshot near key expiry, so the
            // rotation happens less than a day after the snapshot and cannot be hidden by a daily replacement.
            time.Advance(TimeSpan.FromHours(23));
            await using (var api = await StartAsync(oci, time, directory))
            {
                var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
                await backup.RunOnceAsync(CancellationToken.None);
                var snapshot = oci.Objects.Where(item => item.Key.StartsWith(FilePrefix(database) + "/snapshots/", StringComparison.Ordinal))
                    .OrderBy(item => item.Key, StringComparer.Ordinal).Last();
                var generation = snapshot.Key.Split('/')[^1].Split('-')[1];
                walPrefix = FilePrefix(database) + "/wal/" + generation + "/";
                await Assert.That(KeyId(snapshot.Value)).IsEqualTo(oldKey[keys.Length..]);

                var rejected = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
                if (failPublication)
                {
                    oci.FailPut = key =>
                    {
                        if (!key.StartsWith(keys, StringComparison.Ordinal))
                        {
                            return false;
                        }

                        rejected.TryAdd(key, 0);
                        return true;
                    };
                }

                time.Advance(TimeSpan.FromHours(2));
                using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                using (var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "after")), new { text = after }))
                {
                    written.EnsureSuccessStatusCode();
                }

                await backup.RunOnceAsync(CancellationToken.None);
                if (failPublication)
                {
                    var failed = await StatusAsync(api, database);
                    await Assert.That(rejected.Count).IsGreaterThan(0);
                    await Assert.That(failed.GetProperty("lastError").ValueKind).IsEqualTo(JsonValueKind.String);
                    await Assert.That(failed.GetProperty("pendingBytes").GetInt64()).IsGreaterThan(0);
                    await Assert.That(oci.Objects.Keys.Count(key => key.StartsWith(keys, StringComparison.Ordinal))).IsEqualTo(1);
                    await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith(FilePrefix(database) + "/wal/" + generation + "/", StringComparison.Ordinal))).IsFalse();

                    oci.FailPut = _ => false;
                    // Exceed the shipper's maximum backoff without approaching the next daily snapshot.
                    time.Advance(TimeSpan.FromMinutes(6));
                    await backup.RunOnceAsync(CancellationToken.None);
                }

                // Each acknowledged write is shipped before the next: at least eight consecutive segments use the
                // rotated key, enough to fill the restore reader's prefetch window without a replacement snapshot.
                foreach (var (name, text) in extraDocuments)
                {
                    using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", name)), new { text });
                    written.EnsureSuccessStatusCode();
                    await backup.RunOnceAsync(CancellationToken.None);
                }

                newKey = oci.Objects.Keys.Single(key => key.StartsWith(keys, StringComparison.Ordinal) && key != oldKey);
                var segments = oci.Objects.Where(item => item.Key.StartsWith(walPrefix, StringComparison.Ordinal)).ToArray();
                var status = await StatusAsync(api, database);
                pointInTime = time.GetUtcNow();
                await Assert.That(segments.Length).IsGreaterThanOrEqualTo(8);
                await Assert.That(segments.All(item => KeyId(item.Value) == newKey[keys.Length..])).IsTrue();
                await Assert.That(status.GetProperty("generation").GetString()).IsEqualTo(generation);
                await Assert.That(status.GetProperty("lastError").ValueKind).IsEqualTo(JsonValueKind.Null);
                await Assert.That(status.GetProperty("pendingBytes").GetInt64()).IsEqualTo(0);
                await Assert.That(oci.Objects.Keys.Count(key => key.StartsWith(FilePrefix(database) + "/snapshots/", StringComparison.Ordinal)
                    && key.Split('/')[^1].Split('-')[1] == generation)).IsEqualTo(1);
                await Assert.That(oci.Objects.ContainsKey(oldKey)).IsTrue();
                await Assert.That(oci.Objects.ContainsKey(oldSystemKey)).IsTrue();
                var newSystemKey = oci.Objects.Keys.Single(key => key.StartsWith("v1/keys/_system/", StringComparison.Ordinal) && key != oldSystemKey);
                await Assert.That(oci.Objects.Any(item => item.Key.StartsWith("v1/_system/", StringComparison.Ordinal)
                    && KeyId(item.Value) == newSystemKey["v1/keys/_system/".Length..])).IsTrue();
                await Assert.That(oci.Objects.Where(item => item.Key.StartsWith(FilePrefix(database) + "/", StringComparison.Ordinal))
                    .All(item => oci.Objects.ContainsKey(keys + KeyId(item.Value)) && !rejected.ContainsKey(keys + KeyId(item.Value)))).IsTrue();
            }

            // Copy only the stopped node's catalog and layout marker: no source documents or key cache reach this node.
            CopyCatalog(directory, coldDirectory);
            time.Advance(TimeSpan.FromSeconds(1));
            var generated = oci.GeneratedKeys;
            var oldRequests = oci.ObserveKey(oldKey);
            var newRequests = oci.ObserveKey(newKey);
            var prefetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloads = 0;
            oci.BeforeGet = async (key, cancellationToken) =>
            {
                if (key.StartsWith(walPrefix, StringComparison.Ordinal) && Interlocked.Increment(ref downloads) >= 8)
                {
                    prefetched.TrySetResult();
                }

                if (key == newKey)
                {
                    // Keep the first historical key retrieval in flight while the entire WAL prefetch window starts.
                    await prefetched.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }
            };
            await using var cold = await StartAsync(oci, time, coldDirectory);
            await Assert.That(File.Exists(DatabaseFile(coldDirectory, database))).IsFalse();
            using var owner = cold.As(user);
            using var restored = await owner.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime, name = "restored" });
            await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var restoredDatabase = await restored.Content.ReadFromJsonAsync<JsonElement>();
            using var reader = await cold.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await Assert.That(prefetched.Task.IsCompletedSuccessfully).IsTrue();
            foreach (var (name, text) in new[] { ("before", before), ("after", after) }.Concat(extraDocuments))
            {
                using var read = await reader.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/restored/collections/people/documents/{ManagementApi.DocumentId(name)}"));
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
            }

            foreach (var requests in new[] { oldRequests, newRequests })
            {
                await Assert.That(requests.Gets).IsEqualTo(1);
                await Assert.That(requests.Unwraps).IsEqualTo(1);
            }

            // A cold shipper taking over the restored file reuses the newest persisted project key.
            time.Advance(TimeSpan.FromSeconds(10));
            await cold.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(CancellationToken.None);
            var restoredPrefix = $"v1/{database.Organization}/{database.Project}/{restoredDatabase.GetProperty("id").GetString()}/snapshots/";
            await Assert.That(oci.Objects.Where(item => item.Key.StartsWith(restoredPrefix, StringComparison.Ordinal))
                .Select(item => KeyId(item.Value))).IsEquivalentTo([newKey[keys.Length..]]);
            await Assert.That(oci.GeneratedKeys).IsEqualTo(generated);
            foreach (var requests in new[] { oldRequests, newRequests })
            {
                await Assert.That(requests.Gets).IsEqualTo(1);
                await Assert.That(requests.Unwraps).IsEqualTo(1);
            }

            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(coldDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_live_node_refreshes_its_unexpired_current_key_after_a_peer_rotates_it()
    {
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var directory = Directory.CreateTempSubdirectory("terrabase-key-current-").FullName;
        var peerDirectory = Directory.CreateTempSubdirectory("terrabase-key-peer-").FullName;
        try
        {
            var (database, _) = await SeedAsync(oci, time, directory, new Faker().Lorem.Sentence(8), rotation: "");
            CopyCatalog(directory, peerDirectory);
            var peerFile = DatabaseFile(peerDirectory, database);
            Directory.CreateDirectory(Path.GetDirectoryName(peerFile)!);
            File.Copy(DatabaseFile(directory, database), peerFile);
            var keys = Keys(database);
            var oldKey = oci.Objects.Keys.Single(key => key.StartsWith(keys, StringComparison.Ordinal));
            var oldRequests = oci.ObserveKey(oldKey);
            oci.ObservedListPrefix = keys;
            await using var live = await StartAsync(oci, time, directory, rotation: "");
            await Assert.That(live.Server.Services.GetRequiredService<TerraBaseOptions>().Backup!.KeyRotationDays).IsEqualTo(90);
            var backup = live.Server.Services.GetRequiredService<IContinuousBackup>();
            await backup.RunOnceAsync(CancellationToken.None);
            var lists = oci.ListRequests;
            await Assert.That(lists).IsGreaterThan(0);
            await Assert.That(oci.LastListStartAfter).IsEqualTo("");
            await Assert.That(oldRequests.Gets).IsEqualTo(1);
            await Assert.That(oldRequests.Unwraps).IsEqualTo(1);

            // Keep this an active scope: an idle day would expire the cache and require a cold prefix scan.
            using var data = await live.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            time.Advance(TimeSpan.FromHours(23));
            using var warm = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "keep-warm")), new { text = new Faker().Lorem.Sentence(8) });
            warm.EnsureSuccessStatusCode();
            await backup.RunOnceAsync(CancellationToken.None);
            await Assert.That(oci.ListRequests).IsEqualTo(lists + 1);
            await Assert.That(oci.LastListStartAfter).IsEqualTo(oldKey);
            await Assert.That(oldRequests.Gets).IsEqualTo(1);
            await Assert.That(oldRequests.Unwraps).IsEqualTo(1);
            lists = oci.ListRequests;

            // The default 90-day key stays valid locally. The daily snapshot refreshes the warm selection before
            // the peer, configured for one day, publishes a newer key from its independent physical copy.
            time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
            await backup.RunOnceAsync(CancellationToken.None);
            await Assert.That(oci.Objects.Keys.Count(key => key.StartsWith(keys, StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(oci.ListRequests).IsEqualTo(lists + 1);
            await Assert.That(oci.LastListStartAfter).IsEqualTo(oldKey);
            await Assert.That(oldRequests.Gets).IsEqualTo(1);
            await Assert.That(oldRequests.Unwraps).IsEqualTo(1);
            var priorSegments = oci.Objects.Keys.Where(key => key.StartsWith(FilePrefix(database) + "/wal/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            await Assert.That(priorSegments.All(key => KeyId(oci.Objects[key]) == oldKey[keys.Length..])).IsTrue();
            // Advance only the live node during refresh assertions; an unrelated peer pass must not enter its LIST totals.
            var peerTime = new FakeTimeProvider(time.GetUtcNow());
            await using var peer = await StartAsync(oci, peerTime, peerDirectory);
            await peer.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(CancellationToken.None);
            var newest = oci.Objects.Keys.Single(key => key.StartsWith(keys, StringComparison.Ordinal) && key != oldKey);
            var newRequests = oci.ObserveKey(newest);
            var generated = oci.GeneratedKeys;
            lists = oci.ListRequests;

            time.Advance(TimeSpan.FromSeconds(61));
            using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "after-peer")), new { text = new Faker().Lorem.Sentence(8) });
            written.EnsureSuccessStatusCode();
            await backup.RunOnceAsync(CancellationToken.None);
            var status = await StatusAsync(live, database);
            var walPrefix = FilePrefix(database) + "/wal/" + status.GetProperty("generation").GetString() + "/";
            var segments = oci.Objects.Where(item => item.Key.StartsWith(walPrefix, StringComparison.Ordinal) && !priorSegments.Contains(item.Key)).ToArray();
            await Assert.That(segments.Length).IsGreaterThan(0);
            await Assert.That(segments.All(item => KeyId(item.Value) == newest[keys.Length..])).IsTrue();
            await Assert.That(oci.ListRequests).IsEqualTo(lists + 1);
            await Assert.That(oci.LastListStartAfter).IsEqualTo(oldKey);
            await Assert.That(newRequests.Gets).IsEqualTo(1);
            await Assert.That(newRequests.Unwraps).IsEqualTo(1);

            // A later warm refresh starts after the adopted peer key and keeps using its already unwrapped bytes.
            lists = oci.ListRequests;
            time.Advance(TimeSpan.FromSeconds(61));
            using var unchanged = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "after-refresh")), new { text = new Faker().Lorem.Sentence(8) });
            unchanged.EnsureSuccessStatusCode();
            await backup.RunOnceAsync(CancellationToken.None);
            var refreshed = oci.Objects.Where(item => item.Key.StartsWith(walPrefix, StringComparison.Ordinal) && !priorSegments.Contains(item.Key)).ToArray();
            await Assert.That(refreshed.Length).IsGreaterThan(segments.Length);
            await Assert.That(refreshed.All(item => KeyId(item.Value) == newest[keys.Length..])).IsTrue();
            await Assert.That(oci.ListRequests).IsEqualTo(lists + 1);
            await Assert.That(oci.LastListStartAfter).IsEqualTo(newest);
            await Assert.That(newRequests.Gets).IsEqualTo(1);
            await Assert.That(newRequests.Unwraps).IsEqualTo(1);
            await Assert.That(oldRequests.Gets).IsEqualTo(1);
            await Assert.That(oldRequests.Unwraps).IsEqualTo(1);
            await Assert.That(oci.GeneratedKeys).IsEqualTo(generated);
            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(peerDirectory, recursive: true);
        }
    }

    [Test]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("3651")]
    [Arguments("1.5")]
    public async Task Invalid_rotation_intervals_prevent_the_real_server_from_starting(string days)
    {
        using var oci = new FakeOci();
        await Assert.That(async () =>
        {
            await using var server = await TerraBaseTestServer.StartAsync(settings =>
            {
                oci.Configure(settings);
                settings["backup-key-rotation-days"] = days;
            }, oci.Route);
        }).Throws<InvalidOperationException>();
        await Assert.That(oci.Objects.Count).IsEqualTo(0);
        await Assert.That(oci.GeneratedKeys).IsEqualTo(0);
    }

    private static Task<ManagementApi> StartAsync(FakeOci oci, FakeTimeProvider time, string directory, string rotation = "1") =>
        ManagementApi.StartAsync(settings =>
        {
            oci.Configure(settings);
            settings["data-directory"] = directory;
            settings["backup-key-rotation-days"] = rotation;
        }, oci.Route, time);

    private static async Task<(ManagedDatabaseRef Database, string User)> SeedAsync(
        FakeOci oci, FakeTimeProvider time, string directory, string text, string rotation = "1")
    {
        await using var api = await StartAsync(oci, time, directory, rotation);
        return await SeedAsync(api, time, text);
    }

    private static async Task<(ManagedDatabaseRef Database, string User)> SeedAsync(ManagementApi api, FakeTimeProvider time, string text)
    {
        var database = await api.CreateManagedDatabaseAsync("main");
        using (var plan = await api.Client.PutAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/plan"), new { plan = "pro" }))
        {
            plan.EnsureSuccessStatusCode();
        }

        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "before")), new { text });
        written.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(CancellationToken.None);
        return (database, api.User);
    }

    private static async Task<JsonElement> StatusAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken cancellationToken = default)
    {
        using var response = await api.Server.Client.GetAsync(ManagementApi.Uri("/v1/backup"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return status.GetProperty("files").EnumerateArray().Single(file => file.GetProperty("file").GetString() == FilePrefix(database));
    }

    private static void CopyCatalog(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    private static string DatabaseFile(string directory, ManagedDatabaseRef database) =>
        Path.Combine(directory, database.Organization, database.Project, database.Id + ".db");

    private static string FilePrefix(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";

    private static string Keys(ManagedDatabaseRef database) => $"v1/keys/{database.Organization}/{database.Project}/";

    private static string KeyId(byte[] content) => Encoding.ASCII.GetString(content, 4, 26);
}
