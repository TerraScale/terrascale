using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Replication;
using TerraBase.Storage.Backup;
using TerraBase.Sync;
using TerraBase.Storage.Compaction;

namespace TerraBase.Api.Tests;

public sealed class ProductionPrimitivesTests
{
    [Test]
    public async Task BackupSnapshotRestoresAndRejectsTampering()
    {
        var root = NewDirectory();
        var database = Path.Combine(root, "data.db");
        var backups = Path.Combine(root, "backups");
        var restored = Path.Combine(root, "restored.db");
        try
        {
            await using (var provider = new SqliteStorageProvider(database))
            {
                using var body = JsonDocument.Parse("{\"value\":42}");
                await provider.UpsertAsync(new DocumentKey(new TenantId("tenant"), new CollectionId("items"), "one"), body.RootElement, DocumentPrecondition.Missing);
            }
            await using var manager = new SqliteBackupManager(database, new SqliteBackupOptions { DirectoryPath = backups, RetainedSnapshots = 1 });
            var snapshot = await manager.CreateSnapshotAsync();
            await SqliteBackupManager.RestoreAsync(snapshot.SnapshotPath, restored);
            await using var reopened = new SqliteStorageProvider(restored);
            var read = await reopened.ReadAsync(new DocumentKey(new TenantId("tenant"), new CollectionId("items"), "one"));
            await Assert.That(read?.Body.GetProperty("value").GetInt32()).IsEqualTo(42);

            await File.AppendAllTextAsync(snapshot.SnapshotPath, "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() => SqliteBackupManager.RestoreAsync(snapshot.SnapshotPath, Path.Combine(root, "bad.db")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task LeaderlessReplicationDefersDependenciesAndConvergesWithoutParsingCleanBytes()
    {
        var key = new DocumentKey(new TenantId("tenant"), new CollectionId("items"), "one");
        var bytesA = Encoding.UTF8.GetBytes("{\"a\":1}");
        // A causally-following write carries the complete canonical document,
        // so the receiver can apply it without reparsing the predecessor.
        var bytesB = Encoding.UTF8.GetBytes("{\"a\":1,\"b\":2}");
        var a = new LeaderlessReplicationEngine();
        var b = new LeaderlessReplicationEngine();
        var first = ReplicationRecord.FromCanonicalBytes(key, new("a", 1), new Dictionary<string, ulong>(), 1, false, bytesA);
        var dependent = ReplicationRecord.FromCanonicalBytes(key, new("b", 1), new Dictionary<string, ulong> { ["a"] = 1 }, 2, false, bytesB);
        await Assert.That(b.Apply(dependent).Outcome).IsEqualTo(ReplicationApplyOutcome.Deferred);
        var applied = a.Apply(first);
        await Assert.That(applied.Body).IsNull();
        await Assert.That(applied.BodyBytes).IsNotNull();
        b.Apply(first);
        b.Apply(dependent);
        a.Apply(dependent);
        var left = a.Read(key);
        var right = b.Read(key);
        await Assert.That(left?.GetProperty("a").GetInt32()).IsEqualTo(1);
        await Assert.That(left?.GetProperty("b").GetInt32()).IsEqualTo(2);
        await Assert.That(right?.GetRawText()).IsEqualTo(left?.GetRawText());
    }

    [Test]
    public async Task SyncTransportRequiresTls()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => new SyncTransportOptions(new Uri("http://localhost"))));
        await using var transport = new SyncTransport(new SyncTransportOptions(new Uri("https://localhost")), new HttpClient(new StubHandler()));
        await Assert.That(await transport.GetSnapshotAsync("/snapshot")).IsNotNull();
    }

    [Test]
    public async Task ShardedProviderRejectsCrossShardAtomicBatches()
    {
        var root = NewDirectory();
        try
        {
            await using var provider = new ShardedSqliteStorageProvider(Path.Combine(root, "sharded"), 4);
            using var mutationBody = JsonDocument.Parse("{\"value\":1}");
            var mutations = Enumerable.Range(0, 32).Select(index => StorageMutation.Upsert(
                new DocumentKey(new TenantId("tenant"), new CollectionId("items"), $"key-{index}"),
                mutationBody.RootElement, DocumentPrecondition.Missing)).ToArray();
            await Assert.ThrowsAsync<NotSupportedException>(() => provider.ApplyBatchAsync(mutations).AsTask());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task SqliteCompactorMaintainsReadableData()
    {
        var root = NewDirectory();
        var path = Path.Combine(root, "data.db");
        try
        {
            await using (var provider = new SqliteStorageProvider(path))
            {
                using var body = JsonDocument.Parse("{\"value\":7}");
                await provider.UpsertAsync(new DocumentKey(new TenantId("tenant"), new CollectionId("items"), "one"), body.RootElement, DocumentPrecondition.Missing);
            }
            var result = await SqliteCompactor.CompactAsync(path);
            await Assert.That(result.PageCount).IsGreaterThan(0);
            await using var reopened = new SqliteStorageProvider(path);
            await Assert.That(await reopened.ReadAsync(new DocumentKey(new TenantId("tenant"), new CollectionId("items"), "one"))).IsNotNull();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "terrabase-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }
}
