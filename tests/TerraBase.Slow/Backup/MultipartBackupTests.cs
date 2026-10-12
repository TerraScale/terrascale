using System.Collections.Concurrent;
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

public sealed class MultipartBackupTests
{
    // Base64 of random bytes does not compress, so the sealed snapshot of these documents (about 150 MiB) takes two
    // 128 MiB parts.
    private const int Documents = 200;
    private const int RandomBytes = 576 * 1024;
    private const long PartSize = 128L << 20;

    [Test]
    public async Task A_snapshot_larger_than_a_part_ships_in_parts_retries_only_a_failed_part_and_restores_exactly()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(s3, time);
        var (database, hashes) = await FillAsync(api, cancellationToken);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var snapshots = File(database) + "/snapshots/";
        // Part 1 waits until part 2 is stored, so the parts complete out of order, and its first attempt fails.
        var order = new ConcurrentQueue<int>();
        var secondStored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = 0;
        s3.PartStored = (key, part) =>
        {
            if (key.StartsWith(snapshots, StringComparison.Ordinal))
            {
                order.Enqueue(part);
                if (part == 2)
                {
                    secondStored.TrySetResult();
                }
            }
        };
        s3.BeforePart = (key, part, token) => part == 1 && key.StartsWith(snapshots, StringComparison.Ordinal)
            ? secondStored.Task.WaitAsync(token) : Task.CompletedTask;
        s3.FailPart = (key, part) => part == 1 && key.StartsWith(snapshots, StringComparison.Ordinal) && Interlocked.Exchange(ref failed, 1) == 0;

        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(cancellationToken);
        var snapshotted = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(5));
        using (var after = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "after")), new { text = "after" }, cancellationToken))
        {
            after.EnsureSuccessStatusCode();
        }

        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(cancellationToken);
        var status = Status(backup, database);

        // Restore to a second between the snapshot and the later write: every snapshot document, and nothing after it.
        using var restored = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/point-in-time/restore"),
            new { pointInTime = snapshotted.AddSeconds(3), name = "restored" }, cancellationToken);
        var matching = 0;
        for (var i = 0; i < Documents; i++)
        {
            using var read = await data.GetAsync(ManagementApi.Uri(DocumentPath(database, "restored", $"doc-{i}")), cancellationToken);
            var text = (await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()!;
            matching += SHA256.HashData(Encoding.ASCII.GetBytes(text)).AsSpan().SequenceEqual(hashes[i]) ? 1 : 0;
        }

        using var missing = await data.GetAsync(ManagementApi.Uri(DocumentPath(database, "restored", "after")), cancellationToken);
        var stored = s3.Objects.Where(item => item.Key.StartsWith(snapshots, StringComparison.Ordinal)).ToList();

        await Assert.That(status.LastError).IsNull();
        await Assert.That(stored).Count().IsEqualTo(1);
        await Assert.That(stored[0].Value.LongLength).IsGreaterThan(PartSize);
        await Assert.That(s3.StartedUploads).IsEqualTo(1);
        await Assert.That(s3.CompletedUploads).IsEqualTo(1);
        await Assert.That(s3.AbortedUploads).IsEqualTo(0);
        await Assert.That(s3.PendingUploads).IsEqualTo(0);
        await Assert.That(s3.PartRequests.Count).IsEqualTo(2);
        await Assert.That(s3.PartRequests[1]).IsEqualTo(2);
        await Assert.That(s3.PartRequests[2]).IsEqualTo(1);
        await Assert.That(order.ToArray()).IsEquivalentTo([2, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(matching).IsEqualTo(Documents);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_multipart_upload_that_fails_or_is_cancelled_is_aborted_and_a_later_pass_ships_it()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(s3, time);
        var (database, _) = await FillAsync(api, cancellationToken);
        var snapshots = File(database) + "/snapshots/";
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();

        // The last part fails on every attempt: the upload is aborted after its bounded retries.
        s3.FailPart = (key, part) => part == 2 && key.StartsWith(snapshots, StringComparison.Ordinal);
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(cancellationToken);
        var failedError = Status(backup, database).LastError;
        var failedAttempts = s3.PartRequests[2];
        var abortedAfterFailure = s3.AbortedUploads;
        var pendingAfterFailure = s3.PendingUploads;
        var storedAfterFailure = s3.Objects.Keys.Count(key => key.StartsWith(snapshots, StringComparison.Ordinal));

        // The next pass is cancelled while its parts are in flight.
        s3.FailPart = (_, _) => false;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s3.BeforePart = async (key, _, token) =>
        {
            if (key.StartsWith(snapshots, StringComparison.Ordinal))
            {
                arrived.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        time.Advance(TimeSpan.FromMinutes(1));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancelled = backup.RunOnceAsync(stop.Token);
        await arrived.Task.WaitAsync(cancellationToken);
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        var abortedAfterCancellation = s3.AbortedUploads;
        var pendingAfterCancellation = s3.PendingUploads;
        var storedAfterCancellation = s3.Objects.Keys.Count(key => key.StartsWith(snapshots, StringComparison.Ordinal));

        s3.BeforePart = null;
        time.Advance(TimeSpan.FromMinutes(1));
        await backup.RunOnceAsync(cancellationToken);
        var stored = s3.Objects.Where(item => item.Key.StartsWith(snapshots, StringComparison.Ordinal)).ToList();

        await Assert.That(failedError).IsNotNull();
        await Assert.That(failedAttempts).IsGreaterThanOrEqualTo(3);
        await Assert.That(abortedAfterFailure).IsEqualTo(1);
        await Assert.That(pendingAfterFailure).IsEqualTo(0);
        await Assert.That(storedAfterFailure).IsEqualTo(0);
        await Assert.That(abortedAfterCancellation).IsEqualTo(2);
        await Assert.That(pendingAfterCancellation).IsEqualTo(0);
        await Assert.That(storedAfterCancellation).IsEqualTo(0);
        await Assert.That(Status(backup, database).Generation).IsNotNull();
        await Assert.That(s3.StartedUploads).IsEqualTo(3);
        await Assert.That(s3.CompletedUploads).IsEqualTo(1);
        await Assert.That(s3.PendingUploads).IsEqualTo(0);
        await Assert.That(stored).Count().IsEqualTo(1);
        await Assert.That(stored[0].Value.LongLength).IsGreaterThan(PartSize);
    }

    // A failed upload is aborted and never resumed: retrying the key with a newer body uploads every part again, so no part
    // of the older body reaches the object, and the older upload cannot complete later.
    [Test]
    public async Task A_stale_multipart_upload_cannot_replace_or_leak_into_a_newer_object_under_the_same_key()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var (older, newer) = (faker.Random.Byte(0, 127), faker.Random.Byte(128, 255));
        var s3 = new FakeS3();
        var settings = new Dictionary<string, string>();
        s3.Configure(settings);
        var services = new ServiceCollection();
        s3.Route(services);
        await using var provider = services.BuildServiceProvider();
        using var store = new S3ObjectStore(new Uri(settings["backup-endpoint"]), settings["backup-region"], FakeS3.Bucket,
            settings["backup-access-key-id"], settings["backup-secret-key"], provider.GetRequiredService<IHttpClientFactory>(),
            S3ObjectStore.HttpClientName, TimeProvider.System);
        var key = $"v1/{Ulid.NewUlid()}/snapshots/object";
        var failing = 1;
        s3.FailPart = (_, part) => part == 2 && Volatile.Read(ref failing) == 1;

        await using (var stale = await FileAsync(older, cancellationToken))
        {
            await Assert.ThrowsAsync<Amazon.S3.AmazonS3Exception>(() => store.PutAsync(key, stale, cancellationToken));
        }

        var storedAfterFailure = s3.Objects.ContainsKey(key);
        Volatile.Write(ref failing, 0);
        await using (var fresh = await FileAsync(newer, cancellationToken))
        {
            await store.PutAsync(key, fresh, cancellationToken);
        }

        var stored = s3.Objects[key];

        await Assert.That(storedAfterFailure).IsFalse();
        await Assert.That(stored.LongLength).IsEqualTo(PartSize + (1 << 20));
        await Assert.That(stored.AsSpan().IndexOfAnyExcept(newer)).IsEqualTo(-1);
        await Assert.That(s3.StartedUploads).IsEqualTo(2);
        await Assert.That(s3.AbortedUploads).IsEqualTo(1);
        await Assert.That(s3.CompletedUploads).IsEqualTo(1);
        await Assert.That(s3.PendingUploads).IsEqualTo(0);
    }

    // A scratch file of two parts (128 MiB and 1 MiB) filled with one byte, deleted when closed.
    private static async Task<FileStream> FileAsync(byte fill, CancellationToken cancellationToken)
    {
        var file = new FileStream(Path.Combine(Path.GetTempPath(), $"terrabase-multipart-{Ulid.NewUlid()}"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        var chunk = new byte[1 << 20];
        Array.Fill(chunk, fill);
        for (var written = 0L; written < PartSize + chunk.Length; written += chunk.Length)
        {
            await file.WriteAsync(chunk, cancellationToken);
        }

        file.Position = 0;
        return file;
    }

    private static async Task<ManagementApi> StartAsync(FakeS3 s3, FakeTimeProvider time)
    {
        var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        return api;
    }

    // Documents of incompressible text; only their hashes stay in memory.
    private static async Task<(ManagedDatabaseRef Database, byte[][] Hashes)> FillAsync(ManagementApi api, CancellationToken cancellationToken)
    {
        var faker = new Faker();
        var database = await api.CreateManagedDatabaseAsync("main");
        // Enterprise lifts the request budget, which refills on the fake clock.
        await api.SetPlanAsync(api.User, database.Organization, "enterprise", cancellationToken);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items", cancellationToken);
        var hashes = new byte[Documents][];
        for (var i = 0; i < Documents; i++)
        {
            var text = Convert.ToBase64String(faker.Random.Bytes(RandomBytes));
            hashes[i] = SHA256.HashData(Encoding.ASCII.GetBytes(text));
            using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", $"doc-{i}")), new { text }, cancellationToken);
            written.EnsureSuccessStatusCode();
        }

        return (database, hashes);
    }

    private static BackupFileStatus Status(IContinuousBackup backup, ManagedDatabaseRef database) =>
        backup.Status.Files.Single(file => file.File == File(database));

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";

    private static string DocumentPath(ManagedDatabaseRef database, string name, string document) =>
        $"/v1/projects/{database.Project}/databases/{name}/collections/items/documents/by-key?id={ManagementApi.DocumentId(document)}";
}
