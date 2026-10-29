using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Management;

// Decorates the real engine. Only the catalog commits that put a matching document (usage, unless told otherwise) or one
// explicitly armed write start are intercepted.
internal sealed class UsageCommitHook(Func<string, string, ReadOnlyMemory<byte>, bool>? intercepts = null, Func<PlanTier, long>? storageLimit = null)
{
    private readonly Func<string, string, ReadOnlyMemory<byte>, bool> _intercepts = intercepts ?? IsUsage;
    private readonly ConcurrentQueue<Gate> _commits = new();
    private readonly ConcurrentQueue<Gate> _afterCommits = new();
    private Gate? _write;

    public Func<TenantContext, CancellationToken, ValueTask>? BeforeCopy { get; set; }

    // Lifecycle prechecks complete before quota takes the organization gate.
    public Func<CancellationToken, ValueTask>? BeforeQuota { get; set; }

    public Func<TenantContext, CancellationToken, ValueTask>? BeforeDocumentQuota { get; set; }

    public Func<TenantContext, CancellationToken, ValueTask>? AfterQuotaBytes { get; set; }

    public Func<TenantContext, CancellationToken, ValueTask>? BeforeDelete { get; set; }

    // Runs when a transaction starts on an opened user database, before the store hands out a session.
    public Func<TenantContext, CancellationToken, ValueTask>? BeforeBegin { get; set; }

    // Runs before opening a catalog transaction, so mutations finish before its snapshot or write lock is acquired.
    public Func<bool, CancellationToken, ValueTask>? BeforeCatalogBegin { get; set; }

    // Runs before a user database's size is read.
    public Func<TenantContext, CancellationToken, ValueTask>? BeforeSize { get; set; }

    // Runs before a page of a catalog collection is listed; while it is set, catalog reads go through the hook too.
    public Action<string>? BeforeList { get; set; }

    // Runs after a catalog row is captured, so tests can change its mapping before the next lookup.
    public Func<string, string, CancellationToken, ValueTask>? AfterGet { get; set; }

    // Observes detached catalog rows from real reads, including pages, before lookup hooks run.
    public Action<StoredDocument>? AfterRead { get; set; }

    // Lists databases in reverse engine order while set, as an unordered listing may.
    public bool ReverseDatabases
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    // Observes catalog materialization; used to prove memory refusal precedes tenant resolution.
    public Action<string>? BeforeGet { get; set; }

    // A user database for which this returns true opens as missing storage, while the catalog still lists it.
    public Func<TenantContext, bool>? Hidden { get; set; }

    public Gate PauseCommit(bool fail = false)
    {
        var gate = new Gate(fail);
        _commits.Enqueue(gate);
        return gate;
    }

    public Gate PauseAfterCommit(bool fail = false)
    {
        var gate = new Gate(fail);
        _afterCommits.Enqueue(gate);
        return gate;
    }

    public Gate PauseWrite(bool fail = false)
    {
        var gate = new Gate(fail);
        if (Interlocked.CompareExchange(ref _write, gate, null) is not null)
        {
            throw new InvalidOperationException("a write gate is already armed");
        }

        return gate;
    }

    public void Install(IServiceCollection services)
    {
        var registration = services.Single(service => service.ServiceType == typeof(IStorageEngine));
        services.Remove(registration);
        services.AddSingleton<IStorageEngine>(provider => new Engine((IStorageEngine)registration.ImplementationFactory!(provider), this));
        InstallQuota(services);
    }

    public void InstallQuota(IServiceCollection services)
    {
        services.AddSingleton<IStorageQuota>(provider => new Quota(new StorageQuota(provider.GetRequiredService<IStorageEngine>(),
            provider.GetRequiredService<ITenantDirectory>(), storageLimit ?? (plan => QosPolicy.For(plan).StorageBytes)), this));
    }

    private sealed class Quota(StorageQuota inner, UsageCommitHook hook) : IStorageQuota, IDisposable
    {
        public async ValueTask<StorageQuotaEntry> EnterAsync(TenantContext database, IDatabaseStore store, CancellationToken cancellationToken)
        {
            if (hook.BeforeDocumentQuota is { } before)
            {
                await before(database, cancellationToken);
            }

            return await inner.EnterAsync(database, store, cancellationToken);
        }

        public async ValueTask<StorageQuotaGroupEntry> EnterOrganizationsAsync(IReadOnlyList<string> organizationIds, CancellationToken cancellationToken)
        {
            if (hook.BeforeQuota is { } before)
            {
                await before(cancellationToken);
            }

            return await inner.EnterOrganizationsAsync(organizationIds, cancellationToken);
        }

        public async ValueTask<long> ReadBytesAsync(TenantContext database, CancellationToken cancellationToken)
        {
            var bytes = await inner.ReadBytesAsync(database, cancellationToken);
            if (hook.AfterQuotaBytes is { } after)
            {
                await after(database, cancellationToken);
            }

            return bytes;
        }

        public void Dispose() => inner.Dispose();
    }

    private static bool IsUsage(string collection, string id, ReadOnlyMemory<byte> json) =>
        collection.StartsWith("databases/", StringComparison.Ordinal) && collection.EndsWith("/usage", StringComparison.Ordinal) && id.StartsWith("ops~", StringComparison.Ordinal);

    internal sealed class Gate(bool fail) : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Released.Task.WaitAsync(cancellationToken);
            if (fail)
            {
                throw new IOException("injected commit failure");
            }
        }

        public void Dispose() => Released.TrySetResult();
    }

    private sealed class Engine(IStorageEngine inner, UsageCommitHook hook) : IStorageEngine
    {
        public string Name => inner.Name;
        public StoragePressure StoragePressure => inner.StoragePressure;
        public long CommittedWrites => inner.CommittedWrites;
        public long? IoAdmittedCount => inner.IoAdmittedCount;
        public ValueTask RefreshStoragePressureAsync(CancellationToken cancellationToken) => inner.RefreshStoragePressureAsync(cancellationToken);
        public ValueTask<bool> CreateDatabaseAsync(TenantContext database, CancellationToken cancellationToken) => inner.CreateDatabaseAsync(database, cancellationToken);
        public async ValueTask<IDatabaseStore?> OpenDatabaseAsync(TenantContext database, CancellationToken cancellationToken) =>
            hook.Hidden?.Invoke(database) != true && await inner.OpenDatabaseAsync(database, cancellationToken) is { } store
                ? new Opened(store, database, hook)
                : null;
        public async ValueTask<bool> CopyDatabaseAsync(TenantContext source, TenantContext target, CancellationToken cancellationToken)
        {
            if (hook.BeforeCopy is { } beforeCopy)
            {
                await beforeCopy(source, cancellationToken);
            }

            return await inner.CopyDatabaseAsync(source, target, cancellationToken);
        }

        // Restores and branches copy their source here.
        public async ValueTask<bool> CreateDatabaseWithIntentAsync(
            TenantContext database, TenantContext? source, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken)
        {
            if (source is not null && hook.BeforeCopy is { } beforeCopy)
            {
                await beforeCopy(source, cancellationToken);
            }

            return await inner.CreateDatabaseWithIntentAsync(database, source, recordIntent, cancellationToken);
        }

        public ValueTask<bool> VerifyDatabaseErasureAsync(TenantContext database, CancellationToken cancellationToken) =>
            inner.VerifyDatabaseErasureAsync(database, cancellationToken);

        public async ValueTask<bool> DeleteDatabaseAsync(TenantContext database, CancellationToken cancellationToken, bool retainIntentOwnership = false, Ulid? incarnation = null)
        {
            if (hook.BeforeDelete is { } beforeDelete)
            {
                await beforeDelete(database, cancellationToken);
            }

            return await inner.DeleteDatabaseAsync(database, cancellationToken, retainIntentOwnership, incarnation);
        }

        public bool SystemDatabaseExists => inner.SystemDatabaseExists;
        public ValueTask<bool> MayHaveSystemCollectionStateAsync(string collection, CancellationToken cancellationToken) =>
            inner.MayHaveSystemCollectionStateAsync(collection, cancellationToken);

        public ValueTask CompleteDatabaseIntentAsync(TenantContext database, CancellationToken cancellationToken) => inner.CompleteDatabaseIntentAsync(database, cancellationToken);
        public ValueTask<bool> RecordDatabaseDeleteIntentAsync(
            TenantContext database, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken) =>
            inner.RecordDatabaseDeleteIntentAsync(database, recordIntent, cancellationToken);
        public ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken) => inner.IsReadyAsync(cancellationToken);
        public IEnumerable<StoredDatabaseFile> EnumerateDatabaseFiles(CancellationToken cancellationToken = default) => inner.EnumerateDatabaseFiles(cancellationToken);
        public async ValueTask<long?> GetDatabaseSizeAsync(TenantContext database, CancellationToken cancellationToken)
        {
            if (hook.BeforeSize is { } before)
            {
                await before(database, cancellationToken);
            }

            return await inner.GetDatabaseSizeAsync(database, cancellationToken);
        }

        public async IAsyncEnumerable<TenantContext> ListDatabasesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!hook.ReverseDatabases)
            {
                await foreach (var database in inner.ListDatabasesAsync(cancellationToken))
                {
                    yield return database;
                }

                yield break;
            }

            var listed = new List<TenantContext>();
            await foreach (var database in inner.ListDatabasesAsync(cancellationToken))
            {
                listed.Add(database);
            }

            for (var i = listed.Count - 1; i >= 0; i--)
            {
                yield return listed[i];
            }
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public event EventHandler<IDatabaseStore>? DatabaseDeleted
        {
            add => inner.DatabaseDeleted += value;
            remove => inner.DatabaseDeleted -= value;
        }

        // Only the system database, where the catalog lives, is intercepted.
        public async ValueTask<IDatabaseStore> OpenSystemDatabaseAsync(CancellationToken cancellationToken) =>
            new Store(await inner.OpenSystemDatabaseAsync(cancellationToken), hook);
    }

    private sealed class Store(IDatabaseStore inner, UsageCommitHook hook) : IDatabaseStore
    {
        public Ulid Incarnation => inner.Incarnation;
        public TenantContext? Tenant => inner.Tenant;
        public bool IsClosed => inner.IsClosed;
        public long CommitSequence => inner.CommitSequence;

        public Task WaitForCommitAsync(long sequence, CancellationToken cancellationToken) => inner.WaitForCommitAsync(sequence, cancellationToken);
        public async ValueTask<IStorageTransaction> BeginDurableTransactionAsync(
            CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (hook.BeforeCatalogBegin is { } beforeCatalogBegin)
            {
                await beforeCatalogBegin(true, cancellationToken);
            }

            if (Interlocked.Exchange(ref hook._write, null) is { } gate)
            {
                await gate.WaitAsync(cancellationToken);
            }

            return new Transaction(await inner.BeginDurableTransactionAsync(cancellationToken, file, line), hook);
        }

        public async ValueTask<IStorageTransaction> BeginTransactionAsync(
            bool write, CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (hook.BeforeCatalogBegin is { } beforeCatalogBegin)
            {
                await beforeCatalogBegin(write, cancellationToken);
            }

            if (write && Interlocked.Exchange(ref hook._write, null) is { } gate)
            {
                await gate.WaitAsync(cancellationToken);
            }

            var transaction = await inner.BeginTransactionAsync(write, cancellationToken, file, line);
            return write || hook.BeforeList is not null || hook.AfterGet is not null || hook.BeforeGet is not null || hook.AfterRead is not null
                ? new Transaction(transaction, hook) : transaction;
        }
    }

    private sealed class Opened(IDatabaseStore inner, TenantContext database, UsageCommitHook hook) : IDatabaseStore
    {
        public Ulid Incarnation => inner.Incarnation;
        public TenantContext? Tenant => inner.Tenant;
        public bool IsClosed => inner.IsClosed;
        public long CommitSequence => inner.CommitSequence;

        public Task WaitForCommitAsync(long sequence, CancellationToken cancellationToken) => inner.WaitForCommitAsync(sequence, cancellationToken);
        public ValueTask<IStorageTransaction> BeginDurableTransactionAsync(
            CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
            inner.BeginDurableTransactionAsync(cancellationToken, file, line);

        public async ValueTask<IStorageTransaction> BeginTransactionAsync(
            bool write, CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (hook.BeforeBegin is { } beforeBegin)
            {
                await beforeBegin(database, cancellationToken);
            }

            return await inner.BeginTransactionAsync(write, cancellationToken, file, line);
        }
    }

    private sealed class Transaction(IStorageTransaction inner, UsageCommitHook hook) : IStorageTransaction
    {
        public long Work => inner.Work;

        public ValueTask<long> GetDocumentBytesAsync(string collection, string id, CancellationToken cancellationToken) =>
            inner.GetDocumentBytesAsync(collection, id, cancellationToken);

        public ValueTask<long> GetStoredDocumentBytesAsync(CancellationToken cancellationToken) =>
            inner.GetStoredDocumentBytesAsync(cancellationToken);

        private bool _intercepted;

        public ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken) => inner.GetOffsetAsync(cancellationToken);

        public Guid Scope => inner.Scope;

        public ValueTask<Ulid> GetIncarnationAsync(CancellationToken cancellationToken) => inner.GetIncarnationAsync(cancellationToken);

        public async ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
        {
            hook.BeforeGet?.Invoke(collection);
            var document = await inner.GetAsync(collection, id, cancellationToken);
            if (document is not null)
            {
                hook.AfterRead?.Invoke(document);
            }
            if (hook.AfterGet is { } afterGet)
            {
                await afterGet(collection, id, cancellationToken);
            }

            return document;
        }

        public ValueTask<DocumentVersion?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken) => inner.GetVersionAsync(collection, id, cancellationToken);
        public ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken) => inner.DeleteAsync(collection, id, cancellationToken);
        public ValueTask<long?> TombstoneAsync(string collection, string id, CancellationToken cancellationToken) => inner.TombstoneAsync(collection, id, cancellationToken);

        public ValueTask<bool> JoinBaselineAsync(string collection, string id, DocumentState state, EntryVersion at, CancellationToken cancellationToken) =>
            inner.JoinBaselineAsync(collection, id, state, at, cancellationToken);

        public ValueTask<bool> JoinAsync(string collection, string id, DocumentState delta, EntryVersion at, CancellationToken cancellationToken) =>
            inner.JoinAsync(collection, id, delta, at, cancellationToken);

        public ValueTask<bool> WouldJoinAsync(string collection, string id, DocumentState delta, CancellationToken cancellationToken) =>
            inner.WouldJoinAsync(collection, id, delta, cancellationToken);

        public ValueTask RecordAppliedAsync(ReplicaId origin, Hlc applied, CancellationToken cancellationToken) =>
            inner.RecordAppliedAsync(origin, applied, cancellationToken);
        public ValueTask RequireAppliedAsync(ReplicaId origin, Hlc expected, CancellationToken cancellationToken) =>
            inner.RequireAppliedAsync(origin, expected, cancellationToken);
        public ValueTask<FeedPage> ReadFeedAsync(
            FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken) =>
            inner.ReadFeedAsync(cursor, collections, maxEvents, maxBytes, cancellationToken);
        public ValueTask<FeedPage> ReadFeedMetadataAsync(
            FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken) =>
            inner.ReadFeedMetadataAsync(cursor, collections, maxEvents, maxBytes, cancellationToken);
        public ValueTask<IReadOnlyDictionary<ReplicaId, Hlc>> GetFeedHeadAsync(CancellationToken cancellationToken) => inner.GetFeedHeadAsync(cancellationToken);

        public ValueTask<IReadOnlyDictionary<ReplicaId, Hlc>> GetAppliedAsync(CancellationToken cancellationToken) => inner.GetAppliedAsync(cancellationToken);

        public ValueTask<bool> HoldsHistoryAtOrBelowAsync(Hlc cut, CancellationToken cancellationToken) => inner.HoldsHistoryAtOrBelowAsync(cut, cancellationToken);
        public ValueTask<int> TrimFeedAsync(Hlc before, int limit, CancellationToken cancellationToken) => inner.TrimFeedAsync(before, limit, cancellationToken);
        public async ValueTask<DocumentPage> ListAsync(string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken)
        {
            hook.BeforeList?.Invoke(collection);
            var page = await inner.ListAsync(collection, afterId, limit, maxBytes, cancellationToken);
            foreach (var document in page.Documents)
            {
                hook.AfterRead?.Invoke(document);
            }
            return page;
        }

        public ValueTask<QueryPage> QueryAsync(DocumentQuery query, CancellationToken cancellationToken, QueryObservation? observation = null) => inner.QueryAsync(query, cancellationToken, observation);
        public ValueTask<(long Count, bool Complete)> CountAsync(string collection, DocumentFilter? filter, long maxExamined, CancellationToken cancellationToken, QueryObservation? observation = null) => inner.CountAsync(collection, filter, maxExamined, cancellationToken, observation);
        public ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken) => inner.CreateCollectionAsync(collection, cancellationToken);
        public ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken) => inner.GetCollectionAsync(name, cancellationToken);
        public ValueTask<CollectionAccessRules?> GetCollectionRulesAsync(string name, CancellationToken cancellationToken) =>
            inner.GetCollectionRulesAsync(name, cancellationToken);
        public ValueTask<bool> SetCollectionRulesAsync(string name, CollectionAccessRules rules, CancellationToken cancellationToken) =>
            inner.SetCollectionRulesAsync(name, rules, cancellationToken);
        public ValueTask<DocumentOwnerProof?> GetDocumentOwnerProofAsync(string collection, string id, CancellationToken cancellationToken) =>
            inner.GetDocumentOwnerProofAsync(collection, id, cancellationToken);

        public ValueTask<JsonElement?> GetCollectionSchemaAsync(string name, CancellationToken cancellationToken) => inner.GetCollectionSchemaAsync(name, cancellationToken);
        public ValueTask<bool> SetCollectionSchemaAsync(string name, JsonElement? schema, CancellationToken cancellationToken) => inner.SetCollectionSchemaAsync(name, schema, cancellationToken);
        public ValueTask<string?> GetCollectionTtlAsync(string name, CancellationToken cancellationToken) => inner.GetCollectionTtlAsync(name, cancellationToken);
        public ValueTask<bool> SetCollectionTtlAsync(string name, string? path, CancellationToken cancellationToken) => inner.SetCollectionTtlAsync(name, path, cancellationToken);

        public ValueTask<bool> HasCollectionStateAsync(string name, bool prefix, CancellationToken cancellationToken) => inner.HasCollectionStateAsync(name, prefix, cancellationToken);
        public ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(string? afterName, int limit, CancellationToken cancellationToken) => inner.ListCollectionsAsync(afterName, limit, cancellationToken);
        public ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken) => inner.DropCollectionAsync(name, cancellationToken);
        public ValueTask<long> CountDocumentsAsync(string collection, CancellationToken cancellationToken) => inner.CountDocumentsAsync(collection, cancellationToken);
        public ValueTask<IReadOnlyList<CollectionStats>> ListCollectionStatsAsync(CancellationToken cancellationToken) => inner.ListCollectionStatsAsync(cancellationToken);
        public IAsyncEnumerable<StoredDocument> QuerySqlAsync(SqlQuery query, CancellationToken cancellationToken, QueryObservation? observation = null) => inner.QuerySqlAsync(query, cancellationToken, observation);
        public ValueTask<SqlQueryPlan> ExplainSqlAsync(SqlQuery query, CancellationToken cancellationToken) => inner.ExplainSqlAsync(query, cancellationToken);
        ValueTask<SqlStatementResult> IStorageTransaction.ExecuteStatementAsync(SqlStatement statement, CancellationToken cancellationToken) => inner.ExecuteStatementAsync(statement, cancellationToken);
        ValueTask<SqlStatementShape> IStorageTransaction.DescribeStatementAsync(string sql, CancellationToken cancellationToken) => inner.DescribeStatementAsync(sql, cancellationToken);
        void IStorageTransaction.InterruptStatement() => inner.InterruptStatement();
        public ValueTask<long> DeleteCollectionsAsync(string prefix, CancellationToken cancellationToken) => inner.DeleteCollectionsAsync(prefix, cancellationToken);
        public ValueTask<IReadOnlyList<IndexDefinition>> ListIndexesAsync(CancellationToken cancellationToken) => inner.ListIndexesAsync(cancellationToken);
        public ValueTask<bool> CreateIndexAsync(IndexDefinition index, CancellationToken cancellationToken) => inner.CreateIndexAsync(index, cancellationToken);

        public ValueTask<bool> BuildVectorIndexAsync(CancellationToken cancellationToken) => inner.BuildVectorIndexAsync(cancellationToken);
        public ValueTask<bool> DropIndexAsync(string name, CancellationToken cancellationToken) => inner.DropIndexAsync(name, cancellationToken);
        public ValueTask<IReadOnlyList<UniqueConstraint>> GetUniqueConstraintsAsync(string collection, CancellationToken cancellationToken) => inner.GetUniqueConstraintsAsync(collection, cancellationToken);
        public ValueTask SetUniqueConstraintsAsync(string collection, IReadOnlyList<UniqueConstraint> constraints, bool reindex, CancellationToken cancellationToken) => inner.SetUniqueConstraintsAsync(collection, constraints, reindex, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public ValueTask<long> PatchAsync(
            string collection, string id, ReadOnlyMemory<byte> json, IReadOnlyList<FieldOperation> operations, CancellationToken cancellationToken)
        {
            _intercepted |= hook._intercepts(collection, id, json);
            return inner.PatchAsync(collection, id, json, operations, cancellationToken);
        }

        public ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
        {
            _intercepted |= hook._intercepts(collection, id, json);
            return inner.PutAsync(collection, id, json, cancellationToken);
        }

        public ValueTask<(long Version, ReadOnlyMemory<byte> Stored)> PutAdmittedAsync(
            string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
        {
            _intercepted |= hook._intercepts(collection, id, json);
            return inner.PutAdmittedAsync(collection, id, json, cancellationToken);
        }

        public async ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken)
        {
            if (_intercepted && hook._commits.TryDequeue(out var gate))
            {
                await gate.WaitAsync(cancellationToken);
            }

            var sequence = await inner.CommitAsync(cancellationToken);
            if (_intercepted && hook._afterCommits.TryDequeue(out var afterCommit))
            {
                await afterCommit.WaitAsync(cancellationToken);
            }

            return sequence;
        }
    }
}
