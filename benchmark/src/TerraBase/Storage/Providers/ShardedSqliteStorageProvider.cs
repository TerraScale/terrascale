using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;

namespace TerraBase;

/// <summary>Routes documents to independent SQLite files using a stable hash.</summary>
public sealed class ShardedSqliteStorageProvider : IStorageProvider, ITransactionalStorageProvider
{
    private readonly SqliteStorageProvider[] shards;
    private bool disposed;

    public ShardedSqliteStorageProvider(string basePath, int shardCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        if (shardCount is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(shardCount));
        var full = Path.GetFullPath(basePath);
        shards = Enumerable.Range(0, shardCount)
            .Select(index => new SqliteStorageProvider($"{full}.shard-{index:D3}.db"))
            .ToArray();
    }

    public int ShardCount => shards.Length;

    public ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default) => Shard(key).ReadAsync(key, cancellationToken);

    public ValueTask<StorageWriteResult> UpsertAsync(DocumentKey key, JsonElement body, DocumentPrecondition precondition, CancellationToken cancellationToken = default) =>
        Shard(key).UpsertAsync(key, body, precondition, cancellationToken);

    public ValueTask<StorageWriteResult> DeleteAsync(DocumentKey key, DocumentPrecondition precondition, CancellationToken cancellationToken = default) =>
        Shard(key).DeleteAsync(key, precondition, cancellationToken);

    public async IAsyncEnumerable<Document> ScanAsync(TenantId tenant, CollectionId collection, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var all = new List<Document>();
        foreach (var shard in shards)
        {
            await foreach (var document in shard.ScanAsync(tenant, collection, cancellationToken).ConfigureAwait(false)) all.Add(document);
        }
        foreach (var document in all.OrderBy(static value => value.Key.DocumentId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return document;
        }
    }

    /// <summary>Applies a batch atomically when all mutations map to one shard.</summary>
    public ValueTask<StorageBatchResult> ApplyBatchAsync(IReadOnlyList<StorageMutation> mutations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0) throw new ArgumentException("A storage batch must contain at least one mutation.", nameof(mutations));
        var target = ShardIndex(mutations[0].Key);
        if (mutations.Any(mutation => ShardIndex(mutation.Key) != target))
            throw new NotSupportedException("Cross-shard batches require a transaction coordinator and are rejected to preserve atomicity.");
        return shards[target].ApplyBatchAsync(mutations, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        foreach (var shard in shards) await shard.DisposeAsync().ConfigureAwait(false);
    }

    private SqliteStorageProvider Shard(DocumentKey key) => shards[ShardIndex(key)];

    private int ShardIndex(DocumentKey key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{key.Tenant.Value}\0{key.Collection.Value}\0{key.DocumentId}"));
        return (int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes) % (uint)shards.Length);
    }
}
