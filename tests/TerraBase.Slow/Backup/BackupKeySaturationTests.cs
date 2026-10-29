using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class BackupKeySaturationTests
{
    [Test]
    [Timeout(300_000)]
    public async Task A_full_65536_entry_cache_reuses_a_cold_key_for_concurrent_empty_wal_segment_restores(CancellationToken cancellationToken)
    {
        const int cacheCapacity = 65536;
        const int primingKeys = cacheCapacity - 2;
        const int concurrentSegments = 8;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var directory = Directory.CreateTempSubdirectory("terrabase-key-saturation-").FullName;
        var coldDirectory = Directory.CreateTempSubdirectory("terrabase-key-saturation-cold-").FullName;
        var text = new Faker().Lorem.Sentence(8);
        ManagementApi? live = null;
        var liveCleanup = Task.CompletedTask;
        ManagementApi? cold = null;
        var coldCleanup = Task.CompletedTask;
        Exception? bodyFailure = null;
        SuiteDiagnostics.StorageObservation? observation = null;
        try
        {
            ManagedDatabaseRef database;
            string user;
            live = await StartAsync(directory, failedStartCleanup: task => liveCleanup = task);
            {
                database = await live.CreateManagedDatabaseAsync();
                user = live.User;
                using var data = await live.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(data, database, "items", cancellationToken);
                using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "before")), new { text }, cancellationToken);
                written.EnsureSuccessStatusCode();
                time.Advance(TimeSpan.FromSeconds(10));
                var backup = live.Server.Services.GetRequiredService<IContinuousBackup>();
                await backup.RunOnceAsync(cancellationToken);
                var status = backup.Status.Files.Single(status => status.File == $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local");
                await Assert.That(status.LastError).IsNull();
                await Assert.That(status.Generation).IsNotNull();
            }

            await live.DisposeAsync();
            live = null;

            // Start from a real shipped snapshot and stopped database. The cold node receives only the catalog and
            // layout marker; its project file and plaintext key caches do not come from the live node.
            foreach (var file in new[] { "_system.db", "_system.db.key", "_intents.db", "_intents.db.key", ".terrabase.lock" })
            {
                File.Copy(Path.Combine(directory, file), Path.Combine(coldDirectory, file));
            }

            var filePrefix = $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";
            var keys = $"v1/keys/{database.Organization}/{database.Project}/";
            var snapshot = s3.Objects.Single(item => item.Key.StartsWith(filePrefix + "/snapshots/", StringComparison.Ordinal));
            var snapshotName = snapshot.Key[(snapshot.Key.LastIndexOf('/') + 1)..].Split('-');
            await Assert.That(snapshotName[2]).IsEqualTo("0000000000000000");
            var snapshotAt = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(snapshotName[0], CultureInfo.InvariantCulture));
            var primingSegments = $"{filePrefix}/wal/{snapshotName[1]}/";
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(primingSegments, StringComparison.Ordinal))).IsFalse();
            var originalId = Encoding.ASCII.GetString(snapshot.Value, 4, 26);
            var originalWrappedKey = keys + originalId;
            var wrapped = Encoding.ASCII.GetString(s3.Objects[originalWrappedKey]);
            var originalDataKey = s3.Unwrap(wrapped);
            var sourcePath = Path.Combine(directory, database.Organization, database.Project, database.Id + ".db");
            await Assert.That(new FileInfo(sourcePath).Length).IsLessThan(1L << 20);
            using var snapshotBundle = new MemoryStream();
            await SnapshotBundle.WriteAsync(sourcePath, snapshotBundle, cancellationToken);
            var snapshotBytes = snapshotBundle.ToArray();

            // Stress fixtures are authenticated EMPTY WAL segments, not 65,534 real writes. Each distinct peer key
            // enters the real decrypt pipeline; zero frames keep every segment contiguous at position zero. The
            // reader owns at most eight scratch segments, and the test creates only three restored databases.
            for (var index = 0; index < primingKeys; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var at = snapshotAt.AddMilliseconds(index + 1);
                var id = Ulid.NewUlid(at).ToString();
                var dataKey = RandomNumberGenerator.GetBytes(32);
                s3.StoreObject(keys + id, Encoding.ASCII.GetBytes(s3.Wrap(dataKey)));
                var objectKey = string.Create(CultureInfo.InvariantCulture, $"{primingSegments}0000000000000000-{at.ToUnixTimeMilliseconds():D13}");
                s3.StoreObject(objectKey, SealFixture(objectKey, id, dataKey, []));
            }

            var primingAt = snapshotAt.AddMilliseconds(primingKeys);
            // A segment's time is when its read ended, so it cannot show that nothing committed by then is missing: each
            // restored generation's next snapshot, holding exactly the empty segments' log, closes its history.
            var primingClosedAt = primingAt.AddMilliseconds(1);
            var primingClosed = string.Create(CultureInfo.InvariantCulture, $"{filePrefix}/snapshots/{primingClosedAt.ToUnixTimeMilliseconds():D13}-{snapshotName[1]}-0000000000000000");
            s3.StoreObject(primingClosed, SealFixture(primingClosed, originalId, originalDataKey, snapshotBytes));
            var nextSnapshotAt = primingClosedAt.AddMilliseconds(1);
            var nextGeneration = Ulid.NewUlid(nextSnapshotAt).ToString();
            var nextSnapshot = string.Create(CultureInfo.InvariantCulture, $"{filePrefix}/snapshots/{nextSnapshotAt.ToUnixTimeMilliseconds():D13}-{nextGeneration}-0000000000000000");
            s3.StoreObject(nextSnapshot, SealFixture(nextSnapshot, originalId, originalDataKey, snapshotBytes));
            var targetSegments = $"{filePrefix}/wal/{nextGeneration}/";
            var targetId = Ulid.NewUlid(nextSnapshotAt).ToString();
            var targetDataKey = RandomNumberGenerator.GetBytes(32);
            var targetWrappedKey = keys + targetId;
            s3.StoreObject(targetWrappedKey, Encoding.ASCII.GetBytes(s3.Wrap(targetDataKey)));
            for (var index = 0; index < concurrentSegments; index++)
            {
                var at = nextSnapshotAt.AddMilliseconds(index + 1);
                var objectKey = string.Create(CultureInfo.InvariantCulture, $"{targetSegments}0000000000000000-{at.ToUnixTimeMilliseconds():D13}");
                s3.StoreObject(objectKey, SealFixture(objectKey, targetId, targetDataKey, []));
            }

            var targetAt = nextSnapshotAt.AddMilliseconds(concurrentSegments);
            var targetClosed = string.Create(CultureInfo.InvariantCulture, $"{filePrefix}/snapshots/{targetAt.AddMilliseconds(1).ToUnixTimeMilliseconds():D13}-{nextGeneration}-0000000000000000");
            s3.StoreObject(targetClosed, SealFixture(targetClosed, originalId, originalDataKey, snapshotBytes));
            // Seed everything and move time before cold startup. Frozen time prevents expiry, periodic discovery,
            // and shipping restored copies from selecting a current project key during the capacity regression.
            time.Advance(targetAt - time.GetUtcNow());
            var systemWrappedKey = s3.Objects.Keys.Single(key => key.StartsWith("v1/keys/_system/", StringComparison.Ordinal));
            var systemRequests = s3.ObserveKey(systemWrappedKey);
            var snapshotRequests = s3.ObserveKey(originalWrappedKey);
            var targetRequests = s3.ObserveKey(targetWrappedKey);
            var generated = s3.GeneratedKeys;
            var keyGets = 0;
            var primingObjectGets = 0;
            var targetObjectGets = 0;
            var lastGetTicks = Stopwatch.GetTimestamp();
            var pipeline = new BackupReadObservation();
            observation = SuiteDiagnostics.ObserveStorage("backup.key-saturation", progress: () => string.Create(CultureInfo.InvariantCulture,
                $"gets={Volatile.Read(ref keyGets)}/{Volatile.Read(ref primingObjectGets)}/{Volatile.Read(ref targetObjectGets)}/{targetRequests.Gets} idle_ms={(long)Stopwatch.GetElapsedTime(Volatile.Read(ref lastGetTicks)).TotalMilliseconds} {pipeline.Progress()}"));
            var allTargetObjectsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            s3.BeforeGet = async (key, token) =>
            {
                Interlocked.Exchange(ref lastGetTicks, Stopwatch.GetTimestamp());
                if (key.StartsWith(keys, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref keyGets);
                }

                if (key.StartsWith(primingSegments, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref primingObjectGets);
                }

                if (key.StartsWith(targetSegments, StringComparison.Ordinal)
                    && Interlocked.Increment(ref targetObjectGets) == concurrentSegments)
                {
                    allTargetObjectsStarted.TrySetResult();
                }

                if (key == targetWrappedKey && targetRequests.Gets == 1)
                {
                    // Hold the first cold-key retrieval until the whole real prefetch window has started.
                    await allTargetObjectsStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                }
            };

            observation.Phase("cold.start");
            cold = await StartAsync(coldDirectory, pipeline, task => coldCleanup = task);
            observation.ObserveServer("cold", cold.Server);
            observation.Phase("cold.ship-system");
            var coldBackup = cold.Server.Services.GetRequiredService<IContinuousBackup>();
            await coldBackup.RunOnceAsync(cancellationToken);
            var systemStatus = coldBackup.Status.Files.Single(status => status.File == "v1/_system/nodes/local");
            await Assert.That(systemStatus.LastError).IsNull();
            await Assert.That(systemStatus.Generation).IsNotNull();
            await Assert.That(systemStatus.Shipping).IsTrue();
            await Assert.That(coldBackup.Status.Files.Select(status => status.File)).IsEquivalentTo(["v1/_system/nodes/local"]);
            await Assert.That(s3.Objects.Keys.Where(key => key.StartsWith("v1/keys/_system/", StringComparison.Ordinal))).IsEquivalentTo([systemWrappedKey]);
            await Assert.That(systemRequests.Gets).IsEqualTo(1);
            await Assert.That(systemRequests.Unwraps).IsEqualTo(1);
            await Assert.That(Volatile.Read(ref keyGets)).IsEqualTo(0);
            await Assert.That(File.Exists(Path.Combine(coldDirectory, database.Organization, database.Project, database.Id + ".db"))).IsFalse();
            using var owner = cold.As(user);
            using var reader = await cold.CreateClientAsync(database.Organization, "documents:read");

            await RestoreAsync("primed", primingAt);
            // One system key + one snapshot key + 65,534 unique peer keys fill the actual 65,536-entry restore
            // cache. No clock advance or other project-key lookup intervenes before the cold target is requested.
            await Assert.That(Volatile.Read(ref keyGets)).IsEqualTo(primingKeys + 1);
            // Each WAL object is sized, then downloaded again for replay; key-cache lookups remain unchanged.
            await Assert.That(Volatile.Read(ref primingObjectGets)).IsEqualTo(2 * primingKeys);
            await Assert.That(snapshotRequests.Gets).IsEqualTo(1);
            await Assert.That(snapshotRequests.Unwraps).IsEqualTo(1);
            await Assert.That(targetRequests.Gets).IsEqualTo(0);
            await Assert.That(targetRequests.Unwraps).IsEqualTo(0);
            await Assert.That(s3.GeneratedKeys).IsEqualTo(generated);
            for (var round = 0; round < 2; round++)
            {
                // A newer generation's real-database fixture snapshot avoids replaying the priming chain.
                await RestoreAsync($"saturated-{round}", targetAt);
                await Assert.That(allTargetObjectsStarted.Task.IsCompletedSuccessfully).IsTrue();
                await Assert.That(Volatile.Read(ref targetObjectGets)).IsEqualTo(2 * concurrentSegments * (round + 1));
                await Assert.That(Volatile.Read(ref primingObjectGets)).IsEqualTo(2 * primingKeys);
                await Assert.That(targetRequests.Gets).IsEqualTo(1);
                await Assert.That(targetRequests.Unwraps).IsEqualTo(1);
                await Assert.That(s3.GeneratedKeys).IsEqualTo(generated);
            }

            await Assert.That(s3.BadSignatures).IsEqualTo(0);

            async Task RestoreAsync(string name, DateTimeOffset pointInTime)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    observation.Phase(name == "primed" ? "priming.restore" : "saturated.restore");
                    using var restored = await owner.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime, name }, cancellationToken);
                    await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    observation.Phase(name == "primed" ? "priming.read" : "saturated.read");
                    using var read = await reader.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{name}/collections/items/documents/by-key?id={ManagementApi.DocumentId("before")}"), cancellationToken);
                    read.EnsureSuccessStatusCode();
                    await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
                    observation.Phase(name == "primed" ? "priming.delete" : "saturated.delete");
                    using var deleted = await owner.DeleteAsync(restored.Headers.Location, cancellationToken);
                    deleted.EnsureSuccessStatusCode();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // TUnit replaces cancellation exceptions at its deadline; preserve progress in captured output.
                    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"Restore '{name}' cancelled after {Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s: keyGets={Volatile.Read(ref keyGets)}, primingObjectGets={Volatile.Read(ref primingObjectGets)}, targetObjectGets={Volatile.Read(ref targetObjectGets)}, targetKeyGets={targetRequests.Gets}, lastGetSecondsAgo={Stopwatch.GetElapsedTime(Volatile.Read(ref lastGetTicks)).TotalSeconds:F1}"));
                    throw;
                }
            }
        }
        catch (Exception error)
        {
            bodyFailure = error;
            TestContext.Current!.OutputWriter.WriteLine("Backup diagnostics: gets=key/priming/target/target-key; phases=active/starts/oldest-active-ms; Body overlaps its Read/Key/Decode/Output scopes.");
            observation?.BodyFailed(error);
        }
        finally
        {
            observation?.Phase("owners.dispose");
            try
            {
                await SuiteDiagnostics.CleanupAfterBodyAsync(bodyFailure, () =>
                {
                    Directory.Delete(directory, recursive: true);
                    Directory.Delete(coldDirectory, recursive: true);
                }, (cold, cold?.CleanupCompletion ?? coldCleanup), (live, live?.CleanupCompletion ?? liveCleanup));
            }
            finally
            {
                observation?.Dispose();
            }
        }

        async Task<ManagementApi> StartAsync(string dataDirectory, BackupReadObservation? pipeline = null, Action<Task>? failedStartCleanup = null)
        {
            var api = await ManagementApi.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = dataDirectory;
            }, services =>
            {
                s3.Route(services);
                ManualBackupHosting.Configure(services);
                if (pipeline is not null)
                {
                    services.AddSingleton(pipeline);
                }

                // Bound captured output from the 65,536-key workload; warnings and errors remain visible.
                services.AddLogging(logging => logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning));
            }, time, failedStartCleanup: failedStartCleanup);
            Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
            return api;
        }
    }

    // Format 1 fixture writer: one uncompressed last chunk, including the empty chunk. It independently follows
    // BackupObject's documented TBK1 header, HKDF label/path binding, zero chunk nonce, and authenticated words.
    private static byte[] SealFixture(string objectKey, string keyId, byte[] dataKey, ReadOnlySpan<byte> plain)
    {
        const int headerBytes = 62;
        const int tagBytes = 16;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plain.Length, 1 << 20, nameof(plain));
        var content = new byte[headerBytes + 8 + plain.Length + tagBytes];
        var header = content.AsSpan(0, headerBytes);
        "TBK1"u8.CopyTo(header);
        Encoding.ASCII.GetBytes(keyId, header.Slice(4, 26));
        var salt = header[30..];
        RandomNumberGenerator.Fill(salt);
        var words = content.AsSpan(headerBytes, 8);
        BinaryPrimitives.WriteUInt32BigEndian(words, (uint)plain.Length | (1u << 31));
        BinaryPrimitives.WriteUInt32BigEndian(words[4..], (uint)plain.Length);
        var associated = new byte[headerBytes + 16];
        header.CopyTo(associated);
        words.CopyTo(associated.AsSpan(headerBytes + 8));
        using var aes = new AesGcm(HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt.ToArray(),
            Encoding.UTF8.GetBytes("terrabase backup 1\0" + objectKey)), tagBytes);
        aes.Encrypt(new byte[12], plain, content.AsSpan(headerBytes + 8, plain.Length), content.AsSpan(headerBytes + 8 + plain.Length, tagBytes), associated);
        return content;
    }
}
