using System.Runtime.CompilerServices;
using System.Text.Json;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>
/// Wraps any <see cref="IStorageEngine"/> and moves <see cref="IWriteStamps"/> on every write that goes
/// through it: client writes, Management, and replication apply alike.
/// </summary>
/// <remarks>
/// ponytail: stamps live in fixed stripes, so unrelated documents or collections that share a stripe
/// invalidate each other (about 1 in 4096); more stripes if cache hit rates under writes suffer.
/// </remarks>
internal sealed class StampingStorageEngine(IStorageEngine inner, ISystemReplication system) : IStorageEngine, IWriteStamps, ISystemReplication
{
    private const int Stripes = 4096;

    private readonly long[] _stripes = new long[Stripes];

    // ponytail: one epoch for database and collection metadata changes; per-collection epochs if their
    // conservative cache invalidation becomes measurable.
    private long _epoch;

    public string Name => inner.Name;

    public StoragePressure StoragePressure => inner.StoragePressure;

    public long CommittedWrites => inner.CommittedWrites;

    public long? IoAdmittedCount => inner.IoAdmittedCount;

    public ValueTask RefreshStoragePressureAsync(CancellationToken cancellationToken) =>
        inner.RefreshStoragePressureAsync(cancellationToken);

    public bool SystemDatabaseExists => inner.SystemDatabaseExists;

    // Subscribers receive the inner store: notifications only lead to reads, which need no stamps.
    public event EventHandler<ChangeGroupCommitted>? ChangesCommitted
    {
        add => inner.ChangesCommitted += value;
        remove => inner.ChangesCommitted -= value;
    }

    public event EventHandler<IDatabaseStore>? DatabaseDeleted
    {
        add => inner.DatabaseDeleted += value;
        remove => inner.DatabaseDeleted -= value;
    }

    public IAsyncEnumerable<TenantContext> ListDatabasesAsync(CancellationToken cancellationToken) =>
        inner.ListDatabasesAsync(cancellationToken);

    public ValueTask<bool> CreateDatabaseWithIntentAsync(
        TenantContext database, TenantContext? source, Func<CancellationToken, ValueTask> recordIntent, CancellationToken cancellationToken) =>
        inner.CreateDatabaseWithIntentAsync(database, source, recordIntent, cancellationToken);

    public ValueTask CompleteDatabaseIntentAsync(TenantContext database, CancellationToken cancellationToken) =>
        inner.CompleteDatabaseIntentAsync(database, cancellationToken);

    public ValueTask<bool> RecordDatabaseDeleteIntentAsync(
        TenantContext database, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken) =>
        inner.RecordDatabaseDeleteIntentAsync(database, recordIntent, cancellationToken);

    public IEnumerable<StoredDatabaseFile> EnumerateDatabaseFiles(CancellationToken cancellationToken = default) =>
        inner.EnumerateDatabaseFiles(cancellationToken);

    public ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken) => inner.IsReadyAsync(cancellationToken);

    public long Document(TenantContext? database, string collection, string id) =>
        Volatile.Read(ref _epoch) + Volatile.Read(ref _stripes[DocumentStripe(database, collection, id)]);

    public long Collection(TenantContext? database, string collection) =>
        Volatile.Read(ref _epoch) + Volatile.Read(ref _stripes[CollectionStripe(database, collection)]);

    public ValueTask<bool> CreateDatabaseAsync(TenantContext database, CancellationToken cancellationToken) =>
        inner.CreateDatabaseAsync(database, cancellationToken);

    public ValueTask<long?> GetDatabaseSizeAsync(TenantContext database, CancellationToken cancellationToken) =>
        inner.GetDatabaseSizeAsync(database, cancellationToken);

    public async ValueTask<IDatabaseStore?> OpenDatabaseAsync(TenantContext database, CancellationToken cancellationToken) =>
        await inner.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is { } store ? new Store(this, database, store) : null;

    public async ValueTask<IDatabaseStore> OpenSystemDatabaseAsync(CancellationToken cancellationToken) =>
        new Store(this, null, await inner.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false));

    public ValueTask<bool> CopyDatabaseAsync(TenantContext source, TenantContext target, CancellationToken cancellationToken) =>
        inner.CopyDatabaseAsync(source, target, cancellationToken);

    public async ValueTask<bool> DeleteDatabaseAsync(TenantContext database, CancellationToken cancellationToken, bool retainIntentOwnership = false, Guid? incarnation = null)
    {
        Interlocked.Increment(ref _epoch);
        try
        {
            return await inner.DeleteDatabaseAsync(database, cancellationToken, retainIntentOwnership, incarnation).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Increment(ref _epoch);
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    public ValueTask<SystemDocumentPage> ReadDocumentsAsync(
        string? afterCollection, string? afterId, int maxDocuments, int maxBytes, CancellationToken cancellationToken) =>
        system.ReadDocumentsAsync(afterCollection, afterId, maxDocuments, maxBytes, cancellationToken);

    public ValueTask<SystemGroupPage> ReadGroupAsync(long sequence, int skip, int maxChanges, int maxBytes, CancellationToken cancellationToken) =>
        system.ReadGroupAsync(sequence, skip, maxChanges, maxBytes, cancellationToken);

    public ValueTask<JournalPosition> GetHeadAsync(CancellationToken cancellationToken) => system.GetHeadAsync(cancellationToken);

    public async ValueTask<ISystemReplicaTransaction> BeginReplicaAsync(ReadOnlyMemory<byte>? adopt, CancellationToken cancellationToken)
    {
        var reset = adopt is not null;
        if (reset)
        {
            // A reset removes documents no stamp names; the epoch covers them, before and after the commit.
            Interlocked.Increment(ref _epoch);
        }

        return new ReplicaTransaction(this, await system.BeginReplicaAsync(adopt, cancellationToken).ConfigureAwait(false), reset);
    }

    // Plans can change while a store remains open; only storage identity participates in invalidation.
    // Null is reserved for system storage and cannot be a valid tenant database.
    private static int DocumentStripe(TenantContext? database, string collection, string id) =>
        Stripe(HashCode.Combine(database?.OrganizationId, database?.ProjectId, database?.DatabaseId, collection, id));

    private static int CollectionStripe(TenantContext? database, string collection) =>
        Stripe(HashCode.Combine(database?.OrganizationId, database?.ProjectId, database?.DatabaseId, collection));

    private static int Stripe(int hash) => (int)((uint)hash % Stripes);

    private void Touch(int stripe) => Interlocked.Increment(ref _stripes[stripe]);

    private sealed class Store(StampingStorageEngine engine, TenantContext? database, IDatabaseStore inner) : IDatabaseStore
    {
        public Guid Incarnation => inner.Incarnation;

        public TenantContext? Tenant => inner.Tenant;

        public bool IsClosed => inner.IsClosed;

        public long CommitSequence => inner.CommitSequence;

        public Task WaitForCommitAsync(long sequence, CancellationToken cancellationToken) =>
            inner.WaitForCommitAsync(sequence, cancellationToken);

        public async ValueTask<IStorageTransaction> BeginTransactionAsync(bool write, CancellationToken cancellationToken) =>
            new Transaction(engine, database, await inner.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false));

        public async ValueTask<IStorageTransaction> BeginDurableTransactionAsync(CancellationToken cancellationToken) =>
            new Transaction(engine, database, await inner.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false));

        public async ValueTask<(T Result, long Offset)> WriteAsync<T>(
            Func<IStorageTransaction, CancellationToken, ValueTask<T>> work, Func<T, bool> keep, CancellationToken cancellationToken)
        {
            Transaction? stamped = null;
            try
            {
                return await inner.WriteAsync((transaction, token) => work(stamped = new Transaction(engine, database, transaction), token), keep, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // The shared transaction commits outside the wrapper: move its stamps again as CommitAsync would.
                stamped?.Committed();
            }
        }

        public ValueTask<bool> HasCollectionAsync(string name, CancellationToken cancellationToken) => inner.HasCollectionAsync(name, cancellationToken);
    }

    // Replicated system writes move stamps like local ones: a revoked API key is not served from the cache.
    private sealed class ReplicaTransaction(StampingStorageEngine engine, ISystemReplicaTransaction inner, bool reset) : ISystemReplicaTransaction
    {
        // One bit per stripe written, as in Transaction; the applier calls this transaction sequentially.
        private readonly ulong[] _written = new ulong[Stripes / 64];

        public ValueTask ReplaceAsync(DocumentChange change, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(change);
            foreach (var stripe in (ReadOnlySpan<int>)[DocumentStripe(null, change.Collection, change.Id), CollectionStripe(null, change.Collection)])
            {
                engine.Touch(stripe);
                _written[stripe / 64] |= 1UL << (stripe % 64);
            }

            return inner.ReplaceAsync(change, cancellationToken);
        }

        public async ValueTask CommitAsync(JournalPosition head, long offset, CancellationToken cancellationToken)
        {
            try
            {
                await inner.CommitAsync(head, offset, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (reset)
                {
                    Interlocked.Increment(ref engine._epoch);
                }

                for (var word = 0; word < _written.Length; word++)
                {
                    for (var bits = _written[word]; bits != 0; bits &= bits - 1)
                    {
                        engine.Touch((word * 64) + System.Numerics.BitOperations.TrailingZeroCount(bits));
                    }
                }
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // Stamps move before each write reaches storage, so nothing cached earlier stays valid, and again after the
    // commit, so nothing cached while the write was in flight stays valid either.
    private sealed class Transaction(StampingStorageEngine engine, TenantContext? database, IStorageTransaction inner) : IStorageTransaction
    {
        // Every operation joins the same gate before returning, so reads cannot overtake queued writes.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Lock _disposalGate = new();
        private Task? _disposal;
        private bool _streaming;
        private bool _writeFailed;

        // One bit per stripe the transaction wrote to: bounded (512 bytes) however many writes it makes,
        // allocated by the first write.
        private ulong[]? _written;
        private bool _collectionsWritten;

        public Guid Scope => inner.Scope;

        public ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetAsync(collection, id, cancellationToken), cancellationToken);

        public ValueTask<DocumentVersion?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetVersionAsync(collection, id, cancellationToken), cancellationToken);

        public ValueTask<long?> TombstoneAsync(string collection, string id, CancellationToken cancellationToken) =>
            WriteAsync(collection, id, () => inner.TombstoneAsync(collection, id, cancellationToken), cancellationToken);

        public ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken) =>
            WriteAsync(collection, id, () => inner.PutAsync(collection, id, json, cancellationToken), cancellationToken);

        public ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken) =>
            WriteAsync(collection, id, () => inner.DeleteAsync(collection, id, cancellationToken), cancellationToken);

        public ValueTask<DocumentPage> ListAsync(string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken) =>
            RunAsync(() => inner.ListAsync(collection, afterId, limit, maxBytes, cancellationToken), cancellationToken);

        public ValueTask<QueryPage> QueryAsync(DocumentQuery query, CancellationToken cancellationToken, QueryObservation? observation = null) =>
            RunAsync(() => inner.QueryAsync(query, cancellationToken, observation), cancellationToken);

        public ValueTask<SnapshotPage> SnapshotAsync(DocumentQuery query, long offset, CancellationToken cancellationToken) =>
            RunAsync(() => inner.SnapshotAsync(query, offset, cancellationToken), cancellationToken);

        public ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetOffsetAsync(cancellationToken), cancellationToken);

        public ValueTask<long> GetHistoryFloorAsync(CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetHistoryFloorAsync(cancellationToken), cancellationToken);

        public ValueTask<CollectionChangePage> ReadCollectionChangesAsync(
            string collection, long afterOffset, long afterSequence, int limit, long maxBytes, CancellationToken cancellationToken) =>
            RunAsync(() => inner.ReadCollectionChangesAsync(collection, afterOffset, afterSequence, limit, maxBytes, cancellationToken), cancellationToken);

        public async IAsyncEnumerable<StoredDocument> ChangesAsync(
            string collection, DocumentFilter? filter, long after, long through, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await EnterStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await foreach (var document in inner.ChangesAsync(collection, filter, after, through, cancellationToken).ConfigureAwait(false))
                {
                    yield return document;
                }
            }
            finally
            {
                lock (_disposalGate)
                {
                    _streaming = false;
                    _gate.Release();
                }
            }
        }

        public ValueTask<(long Count, bool Complete)> CountAsync(string collection, DocumentFilter? filter, long maxExamined, CancellationToken cancellationToken, QueryObservation? observation = null) =>
            RunAsync(() => inner.CountAsync(collection, filter, maxExamined, cancellationToken, observation), cancellationToken);

        public ValueTask<long> CountDocumentsAsync(string collection, CancellationToken cancellationToken) =>
            RunAsync(() => inner.CountDocumentsAsync(collection, cancellationToken), cancellationToken);

        public ValueTask<IReadOnlyList<CollectionStats>> ListCollectionStatsAsync(CancellationToken cancellationToken) =>
            RunAsync(() => inner.ListCollectionStatsAsync(cancellationToken), cancellationToken);

        public async IAsyncEnumerable<StoredDocument> QuerySqlAsync(SqlQuery query, [EnumeratorCancellation] CancellationToken cancellationToken, QueryObservation? observation = null)
        {
            await EnterStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await foreach (var document in inner.QuerySqlAsync(query, cancellationToken, observation).ConfigureAwait(false))
                {
                    yield return document;
                }
            }
            finally
            {
                lock (_disposalGate)
                {
                    _streaming = false;
                    _gate.Release();
                }
            }
        }

        public ValueTask<SqlQueryPlan> ExplainSqlAsync(SqlQuery query, CancellationToken cancellationToken) =>
            RunAsync(() => inner.ExplainSqlAsync(query, cancellationToken), cancellationToken);

        public ValueTask<long> DeleteCollectionsAsync(string prefix, CancellationToken cancellationToken) =>
            WriteCollectionAsync(() => inner.DeleteCollectionsAsync(prefix, cancellationToken), cancellationToken);

        public ValueTask<IReadOnlyList<IndexDefinition>> ListIndexesAsync(CancellationToken cancellationToken) =>
            RunAsync(() => inner.ListIndexesAsync(cancellationToken), cancellationToken);

        public ValueTask<bool> CreateIndexAsync(IndexDefinition index, CancellationToken cancellationToken) =>
            RunAsync(() => inner.CreateIndexAsync(index, cancellationToken), cancellationToken, mutation: true);

        public ValueTask<bool> DropIndexAsync(string name, CancellationToken cancellationToken) =>
            RunAsync(() => inner.DropIndexAsync(name, cancellationToken), cancellationToken, mutation: true);

        public ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetCollectionAsync(name, cancellationToken), cancellationToken);

        public ValueTask<JsonElement?> GetCollectionSchemaAsync(string name, CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetCollectionSchemaAsync(name, cancellationToken), cancellationToken);

        public ValueTask<bool> SetCollectionSchemaAsync(string name, JsonElement? schema, CancellationToken cancellationToken) =>
            WriteCollectionAsync(() => inner.SetCollectionSchemaAsync(name, schema, cancellationToken), cancellationToken);

        public ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(
            string? afterName, int limit, CancellationToken cancellationToken) =>
            RunAsync(() => inner.ListCollectionsAsync(afterName, limit, cancellationToken), cancellationToken);

        public ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken) =>
            WriteCollectionAsync(() => inner.CreateCollectionAsync(collection, cancellationToken), cancellationToken);

        public ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken) =>
            WriteCollectionAsync(() => inner.DropCollectionAsync(name, cancellationToken), cancellationToken);

        public ValueTask<long> CommitAsync(CancellationToken cancellationToken) => RunAsync(async () =>
        {
            if (_writeFailed)
            {
                throw new InvalidOperationException("a transaction with a rejected or failed write cannot commit");
            }
            try
            {
                return await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Also on failure: whether a failed commit reached the file is unknown.
                Committed();
            }
        }, cancellationToken);

        // Moves the stamps of everything this transaction wrote, once its commit returned or failed.
        public void Committed()
        {
            if (_collectionsWritten)
            {
                Interlocked.Increment(ref engine._epoch);
            }

            for (var word = 0; _written is not null && word < _written.Length; word++)
            {
                for (var bits = _written[word]; bits != 0; bits &= bits - 1)
                {
                    engine.Touch((word * 64) + System.Numerics.BitOperations.TrailingZeroCount(bits));
                }
            }
        }

        public ValueTask<ReadOnlyMemory<byte>> GetOriginAsync(CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetOriginAsync(cancellationToken), cancellationToken);

        public ValueTask<long> GetLastChangeSequenceAsync(CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetLastChangeSequenceAsync(cancellationToken), cancellationToken);

        public ValueTask<IReadOnlyList<DocumentChange>?> ReadChangesAsync(long sequence, CancellationToken cancellationToken) =>
            RunAsync(() => inner.ReadChangesAsync(sequence, cancellationToken), cancellationToken);

        public ValueTask<ReplicationStatePage> ReadReplicationStateAsync(string? afterCollection, string? afterId, CancellationToken cancellationToken) =>
            RunAsync(() => inner.ReadReplicationStateAsync(afterCollection, afterId, cancellationToken), cancellationToken);

        public ValueTask<ReadOnlyMemory<byte>?> GetChangeDigestAsync(long sequence, CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetChangeDigestAsync(sequence, cancellationToken), cancellationToken);

        public ValueTask<bool> ApplyAsync(DocumentChange change, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(change);
            if (change.Collection is DocumentChange.Collections or DocumentChange.Indexes)
            {
                return WriteCollectionAsync(() => inner.ApplyAsync(change, cancellationToken), cancellationToken);
            }

            // A replicated state may also create its collection; both stripes move like a local write.
            return WriteAsync(change.Collection, change.Id, () => inner.ApplyAsync(change, cancellationToken), cancellationToken);
        }

        public ValueTask<bool> RepairReplicationStateAsync(DocumentChange change, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(change);
            if (change.Collection is DocumentChange.Collections or DocumentChange.Indexes)
            {
                return WriteCollectionAsync(() => inner.RepairReplicationStateAsync(change, cancellationToken), cancellationToken);
            }

            return WriteAsync(change.Collection, change.Id, () => inner.RepairReplicationStateAsync(change, cancellationToken), cancellationToken);
        }

        public ValueTask<long> GetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetAppliedSequenceAsync(origin, cancellationToken), cancellationToken);

        public ValueTask<(long Sequence, ReadOnlyMemory<byte> Digest)> GetAppliedFrontierAsync(
            ReadOnlyMemory<byte> origin, CancellationToken cancellationToken) =>
            RunAsync(() => inner.GetAppliedFrontierAsync(origin, cancellationToken), cancellationToken);

        public async ValueTask SetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, long sequence, CancellationToken cancellationToken) =>
            await RunAsync(async () =>
            {
                await inner.SetAppliedSequenceAsync(origin, sequence, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken, mutation: true).ConfigureAwait(false);

        public async ValueTask SetAppliedFrontierAsync(
            ReadOnlyMemory<byte> origin, long sequence, ReadOnlyMemory<byte> digest, CancellationToken cancellationToken) =>
            await RunAsync(async () =>
            {
                await inner.SetAppliedFrontierAsync(origin, sequence, digest, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken, mutation: true).ConfigureAwait(false);

        public ValueTask DisposeAsync()
        {
            lock (_disposalGate)
            {
                if (_streaming)
                {
                    throw new InvalidOperationException("dispose the active changes stream before using the transaction");
                }
                // Closes submissions and queues cleanup after every already-admitted operation.
                return new(_disposal ??= DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private ValueTask<T> WriteCollectionAsync<T>(Func<ValueTask<T>> write, CancellationToken cancellationToken) => RunAsync(async () =>
        {
            // Collection and prefix deletion remove many documents; the bounded global epoch covers them.
            _collectionsWritten = true;
            Interlocked.Increment(ref engine._epoch);
            return await write().ConfigureAwait(false);
        }, cancellationToken, mutation: true);

        private ValueTask<T> WriteAsync<T>(string collection, string id, Func<ValueTask<T>> write, CancellationToken cancellationToken) => RunAsync(async () =>
        {
            // The document's and its collection's stamps move before the write reaches storage.
            _written ??= new ulong[Stripes / 64];
            foreach (var stripe in (ReadOnlySpan<int>)[DocumentStripe(database, collection, id), CollectionStripe(database, collection)])
            {
                engine.Touch(stripe);
                _written[stripe / 64] |= 1UL << (stripe % 64);
            }

            return await write().ConfigureAwait(false);
        }, cancellationToken, mutation: true);

        private Task EnterAsync(CancellationToken cancellationToken, bool stream = false, bool mutation = false)
        {
            lock (_disposalGate)
            {
                ObjectDisposedException.ThrowIf(_disposal is not null, this);
                if (_streaming)
                {
                    if (mutation)
                    {
                        // Mark refusal before stream cleanup can admit a concurrent commit.
                        _writeFailed = true;
                    }
                    throw new InvalidOperationException("dispose the active changes stream before using the transaction");
                }
                _streaming = stream;
                // Admission and waiting are atomic with respect to disposal; no pre-disposal call can
                // arrive at the operation gate after cleanup has already queued there.
                return _gate.WaitAsync(cancellationToken);
            }
        }

        private async Task EnterStreamAsync(CancellationToken cancellationToken)
        {
            var waiting = EnterAsync(cancellationToken, stream: true);
            try
            {
                await waiting.ConfigureAwait(false);
            }
            catch
            {
                lock (_disposalGate)
                {
                    _streaming = false;
                }
                throw;
            }
        }

        private ValueTask<T> RunAsync<T>(Func<ValueTask<T>> call, CancellationToken cancellationToken, bool mutation = false) =>
            // Forward cancelled mutations in submission order so the native transaction records failure.
            RunCoreAsync(EnterAsync(mutation ? CancellationToken.None : cancellationToken, mutation: mutation), call, mutation);

        private async ValueTask<T> RunCoreAsync<T>(Task waiting, Func<ValueTask<T>> call, bool mutation)
        {
            await waiting.ConfigureAwait(false);
            try
            {
                if (mutation && _writeFailed)
                {
                    throw new InvalidOperationException("a transaction with a rejected or failed write cannot write");
                }
                return await call().ConfigureAwait(false);
            }
            catch
            {
                if (mutation)
                {
                    _writeFailed = true;
                }

                throw;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
