using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Errors;
using TerraScale.TerraBase.Features.Management.Usage;
using TerraScale.TerraBase.Features.Replication;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Primitives;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <remarks>
/// A committed client write answers only after <see cref="IReplicationWait"/>, outside its transaction: the database's
/// writer lock must not wait on the network, and a peer applying this member's groups needs it.
/// </remarks>
internal sealed class DocumentService(IStorageEngine engine, TimeProvider time, DocumentCache cache, IUsageMeter usage, IReplicationWait replication)
    : IDocumentService, ISystemDocumentReader
{
    // assert_count examines at most this many documents.
    private const long MaxAssertCount = 10_000;

    private static readonly NotFound UnknownCollection = new("the collection does not exist", "unknown_collection");
    private static readonly NotFound DocumentMissing = new("the document does not exist", "not_found");
    private static readonly Conflict CollectionExists = new("the collection already exists", "name_taken");
    private static readonly Validation NewIdRule = new("a new id must be a lowercase hyphenated UUID");
    private static readonly Validation TooExpensive = new("query too expensive", "invalid_query");
    private static readonly Validation SortBudget = new("the query exceeds the sort budget", "sort_requires_index");
    private static readonly Validation AssertRequiresIndex = new("assert_count examines at most 10000 documents", "assert_requires_index");
    private static readonly PayloadTooLarge WriteLimits = new("a transaction exceeds the storage write limits");
    private static readonly Unavailable OffsetExhausted = new("the commit offset is exhausted", "capacity");
    private static readonly Unavailable ChangeGroupCapacity = new("a write transaction holds at most 1024 changes and 2 MiB of change content", "capacity");

    public async ValueTask<CollectionResult> CreateCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, write: true, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        var collection = new CollectionInfo(name, time.GetUtcNow().ToUnixTimeMilliseconds());
        await using (transaction.ConfigureAwait(false))
        {
            if (!await transaction.CreateCollectionAsync(collection, cancellationToken).ConfigureAwait(false))
            {
                return CollectionExists;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await replication.WaitAsync(database, cancellationToken).ConfigureAwait(false);
        return collection;
    }

    public async ValueTask<CollectionResult> GetCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false) is { } collection ? collection : UnknownCollection;
        }
    }

    public async ValueTask<Found<CollectionPage>> ListCollectionsAsync(
        TenantContext database, string? afterName, int limit, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            var (collections, hasMore) = await transaction.ListCollectionsAsync(afterName, limit, cancellationToken).ConfigureAwait(false);
            return new CollectionPage(collections, hasMore);
        }
    }

    public async ValueTask<CollectionResult> DropCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, write: true, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        CollectionInfo collection;
        await using (transaction.ConfigureAwait(false))
        {
            if (await transaction.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false) is not { } existing)
            {
                return UnknownCollection;
            }

            collection = existing;
            try
            {
                await transaction.DropCollectionAsync(name, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChangeGroupCapacityException)
            {
                return ChangeGroupCapacity;
            }
        }

        await replication.WaitAsync(database, cancellationToken).ConfigureAwait(false);
        return collection;
    }

    public async ValueTask<DocumentRead> GetAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The cache keeps documents only: an absence or a refusal comes back as null, which it never stores.
        DocumentRead? missed = null;
        var document = await cache.GetAsync(key, async () =>
        {
            var read = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (read is StoredDocument stored)
            {
                return stored;
            }

            missed = read;
            return null;
        }).ConfigureAwait(false);
        if (document is not null)
        {
            usage.RecordRead(key.Database);
            return document;
        }

        var result = missed ?? new NeverWritten();
        if (result is NeverWritten)
        {
            usage.RecordRead(key.Database);
        }

        return result;
    }

    public ValueTask<StoredDocument?> GetSystemAsync(string collection, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return cache.GetSystemAsync(collection, id, () => ReadSystemAsync(collection, id, cancellationToken));
    }

    // Internal system documents use opaque ids and do not require client collection metadata.
    private async ValueTask<StoredDocument?> ReadSystemAsync(string collection, string id, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);

        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.GetAsync(collection, id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<DocumentRead> ReadAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        if (await BeginAsync(key.Database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            if (await transaction.GetCollectionAsync(key.Collection, cancellationToken).ConfigureAwait(false) is null)
            {
                return UnknownCollection;
            }

            if (await AddressAsync(transaction, key.Collection, key.Id, cancellationToken).ConfigureAwait(false) is { } invalid)
            {
                return invalid;
            }

            // A deleted document comes back as its tombstone, without a body, so its 404 can carry its version.
            if (await transaction.GetAsync(key.Collection, key.Id, cancellationToken).ConfigureAwait(false) is { } document)
            {
                return document;
            }

            return await transaction.GetVersionAsync(key.Collection, key.Id, cancellationToken).ConfigureAwait(false) is { Deleted: true } tombstone
                ? new StoredDocument(key.Id, tombstone.Version, ReadOnlyMemory<byte>.Empty, transaction.Scope, Deleted: true)
                : new NeverWritten();
        }
    }

    public async ValueTask<QueryResult> QueryAsync(
        TenantContext database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // As in GetAsync: the cache keeps pages only, never a refusal.
        QueryResult? refused = null;
        var result = await cache.QueryAsync(database, query, countLimit, async () =>
        {
            var read = await ReadQueryAsync(database, query, countLimit, cancellationToken).ConfigureAwait(false);
            if (read is QueryOutcome outcome)
            {
                return new CachedQuery(outcome.Page, outcome.Count);
            }

            refused = read;
            return null!;
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (refused is { } failure)
        {
            return failure;
        }

        usage.RecordRead(database);
        return new QueryOutcome(result.Page, result.Count);
    }

    private async ValueTask<QueryResult> ReadQueryAsync(
        TenantContext database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.GetCollectionAsync(query.Collection, cancellationToken).ConfigureAwait(false) is null
                ? UnknownCollection
                : await QueryAsync(transaction, query, countLimit, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<QueryResult> QueryAsync(
        IStorageTransaction transaction, DocumentQuery query, long? countLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var page = await transaction.QueryAsync(query, cancellationToken).ConfigureAwait(false);
            (long Count, bool Complete)? count = countLimit is { } limit
                ? await transaction.CountAsync(query.Collection, query.Filter, limit, cancellationToken).ConfigureAwait(false)
                : null;
            return new QueryOutcome(page, count);
        }
        catch (QueryCapacityException error)
        {
            return error.SortBudget ? SortBudget : TooExpensive;
        }
    }

    // One operation, with the engine's limits read where they are hit.
    public async ValueTask<ApplyOutcome> ApplyAsync(IStorageTransaction transaction, DocumentOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            return await ApplyOperationAsync(transaction, operation, cancellationToken).ConfigureAwait(false);
        }
        catch (DocumentValidationException error)
        {
            return new DocumentRejected(error.TooLarge ? new PayloadTooLarge(error.Message) : new Validation(error.Message));
        }
        catch (StorageWriteLimitException)
        {
            // One commit must fit one shape log chunk (docs/shape-reads.md).
            return new DocumentRejected(WriteLimits);
        }
        catch (ChangeGroupCapacityException)
        {
            return new DocumentRejected(ChangeGroupCapacity);
        }
        catch (OverflowException)
        {
            return new DocumentRejected(OffsetExhausted);
        }
    }

    public async ValueTask<WriteOutcome> WriteAsync(
        TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken)
    {
        var written = await CommitAsync(database, operations, indexErrors, cancellationToken).ConfigureAwait(false);
        if (written is WriteResult)
        {
            await replication.WaitAsync(database, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    private async ValueTask<WriteOutcome> CommitAsync(
        TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, write: true, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return new DocumentRejected(DocumentRoute.UnknownDatabase);
        }

        await using (transaction.ConfigureAwait(false))
        {
            var collections = new HashSet<string>(StringComparer.Ordinal);
            var results = new OperationResult[operations.Count];
            // Assertions see the state before this transaction's writes.
            foreach (var assertions in new[] { true, false })
            {
                for (var i = 0; i < operations.Count; i++)
                {
                    var operation = operations[i];
                    if ((operation is AssertOperation or AssertCountOperation) != assertions)
                    {
                        continue;
                    }

                    switch (await ApplyCheckedAsync(transaction, operation, collections, cancellationToken).ConfigureAwait(false))
                    {
                        case OperationResult result:
                            results[i] = result;
                            break;
                        case DocumentRejected rejected:
                            return rejected with { Op = indexErrors ? i : null };
                    }
                }
            }

            var offset = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var writes = operations.Count(operation => operation is PutOperation or PatchOperation or DeleteOperation);
            if (writes > 0)
            {
                usage.RecordWrites(database, writes);
            }

            return new WriteResult(offset, results, transaction.Scope);
        }
    }

    // One operation of a client transaction: its collection must exist.
    private async ValueTask<ApplyOutcome> ApplyCheckedAsync(
        IStorageTransaction transaction, DocumentOperation operation, HashSet<string> collections, CancellationToken cancellationToken)
    {
        if (collections.Add(operation.Collection)
            && await transaction.GetCollectionAsync(operation.Collection, cancellationToken).ConfigureAwait(false) is null)
        {
            return new DocumentRejected(UnknownCollection);
        }

        return await ApplyAsync(transaction, operation, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ApplyOutcome> ApplyOperationAsync(
        IStorageTransaction transaction, DocumentOperation operation, CancellationToken cancellationToken)
    {
        var id = operation switch
        {
            PutOperation put => put.Id,
            PatchOperation patch => patch.Id,
            DeleteOperation delete => delete.Id,
            AssertOperation assert => assert.Id,
            _ => null,
        };
        if (id is not null && await AddressAsync(transaction, operation.Collection, id, cancellationToken).ConfigureAwait(false) is { } invalid)
        {
            return new DocumentRejected(invalid);
        }

        switch (operation)
        {
            case PutOperation put:
                {
                    var current = await transaction.GetVersionAsync(put.Collection, put.Id, cancellationToken).ConfigureAwait(false);
                    if (await CheckAsync(transaction, put.Precondition, put.Collection, put.Id, current, cancellationToken).ConfigureAwait(false) is { } failed)
                    {
                        return new DocumentRejected(failed);
                    }

                    var version = await transaction.PutAsync(put.Collection, put.Id, put.Json, cancellationToken).ConfigureAwait(false);
                    return new OperationResult(put.Id, version, current is not { Deleted: false }, null,
                        ETags.Version(transaction.Scope, put.Collection, put.Id, version, put.Json));
                }

            case PatchOperation patch:
                {
                    if (await transaction.GetAsync(patch.Collection, patch.Id, cancellationToken).ConfigureAwait(false) is not { } current)
                    {
                        return new DocumentRejected(DocumentMissing);
                    }

                    if (await CheckAsync(transaction, patch.Precondition, patch.Collection, patch.Id,
                        new DocumentVersion(current.Version, Deleted: false), cancellationToken, current.Json).ConfigureAwait(false) is { } failed)
                    {
                        return new DocumentRejected(failed);
                    }

                    var document = JsonNode.Parse(current.Json.Span, documentOptions: RequestJson.StoredDocumentOptions)!.AsObject();
                    switch (patch.Patch.Apply(document))
                    {
                        case Validation error:
                            return new DocumentRejected(error);
                        case Conflict error:
                            return new DocumentRejected(error);
                    }

                    // Existing legacy documents use the stored depth bound; the strict client limits still
                    // apply to the patched result.
                    var json = JsonSerializer.SerializeToUtf8Bytes(document, TerraBaseJsonContext.Default.JsonObject);
                    var version = await transaction.PutAsync(patch.Collection, patch.Id, json, cancellationToken).ConfigureAwait(false);
                    return new OperationResult(patch.Id, version, false, null,
                        ETags.Version(transaction.Scope, patch.Collection, patch.Id, version, json));
                }

            case DeleteOperation delete:
                {
                    var current = await transaction.GetVersionAsync(delete.Collection, delete.Id, cancellationToken).ConfigureAwait(false);
                    if (current is not { Deleted: false })
                    {
                        return new DocumentRejected(DocumentMissing);
                    }

                    if (await CheckAsync(transaction, delete.Precondition, delete.Collection, delete.Id, current, cancellationToken).ConfigureAwait(false) is { } failed)
                    {
                        return new DocumentRejected(failed);
                    }

                    var version = await transaction.TombstoneAsync(delete.Collection, delete.Id, cancellationToken).ConfigureAwait(false);
                    return new OperationResult(delete.Id, version, false, null,
                        ETags.Version(transaction.Scope, delete.Collection, delete.Id, version!.Value, ReadOnlyMemory<byte>.Empty, deleted: true));
                }

            case AssertOperation { Filter: null } absent:
                {
                    return await transaction.GetVersionAsync(absent.Collection, absent.Id, cancellationToken).ConfigureAwait(false) is not { Deleted: false }
                        ? new OperationResult(absent.Id, null, false, null)
                        : new DocumentRejected(Failed("the document exists"));
                }

            case AssertOperation assert:
                {
                    var filter = new AndFilter([assert.Filter, new CompareFilter(DocumentPath.Id, Comparison.Equal, new ScalarValue(ScalarKind.Text, assert.Id))]);
                    try
                    {
                        var page = await transaction.QueryAsync(new DocumentQuery(assert.Collection, filter, [], null, 1, long.MaxValue), cancellationToken)
                            .ConfigureAwait(false);
                        return page.Rows.Count == 1 ? new OperationResult(assert.Id, null, false, null) : new DocumentRejected(Failed("the document does not match"));
                    }
                    catch (QueryCapacityException error)
                    {
                        return new DocumentRejected(error.SortBudget ? SortBudget : TooExpensive);
                    }
                }

            case AssertCountOperation assertCount:
                {
                    long count;
                    bool complete;
                    try
                    {
                        (count, complete) = await transaction.CountAsync(assertCount.Collection, assertCount.Filter, MaxAssertCount, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (QueryCapacityException)
                    {
                        return new DocumentRejected(TooExpensive);
                    }

                    if (!complete)
                    {
                        return new DocumentRejected(AssertRequiresIndex);
                    }

                    return count < assertCount.AtLeast || count > assertCount.AtMost
                        ? new DocumentRejected(Failed("the count is out of bounds"))
                        : new OperationResult(null, null, false, count);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.GetType().Name, "unknown operation");
        }
    }

    // Preserve schema-1 identities, including tombstones recreated by version, without admitting new legacy ids.
    private static async ValueTask<Validation?> AddressAsync(
        IStorageTransaction transaction, string collection, string id, CancellationToken cancellationToken) =>
        !DocumentRoute.IsUuid(id) && await transaction.GetVersionAsync(collection, id, cancellationToken).ConfigureAwait(false) is null
            ? NewIdRule
            : null;

    // The precondition failure, or null when it holds.
    private static async ValueTask<PreconditionFailed?> CheckAsync(
        IStorageTransaction transaction, Precondition precondition, string collection, string id, DocumentVersion? current,
        CancellationToken cancellationToken, ReadOnlyMemory<byte>? json = null)
    {
        string? version = null;
        if (precondition.IfMatch is not null && current is { } state)
        {
            // Wildcards and unconditional writes need only existence. Exact tags also identify equal-version body winners.
            var body = state.Deleted ? ReadOnlyMemory<byte>.Empty
                : json ?? (await transaction.GetAsync(collection, id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("a live document version has no body in the same transaction")).Json;
            version = ETags.Version(transaction.Scope, collection, id, state.Version, body, state.Deleted);
        }

        return ETags.CheckVersion(precondition, current is { Deleted: false }, version) is PreconditionFailed failed ? failed : null;
    }

    private static PreconditionFailed Failed(string message) => new(message, "precondition_failed");

    // Null when the database's storage does not exist.
    private async ValueTask<IStorageTransaction?> BeginAsync(TenantContext database, bool write, CancellationToken cancellationToken) =>
        await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is { } store
            ? await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false)
            : null;
}

/// <summary>Cached reads of TerraBase-owned documents; tenant document routes never address system storage.</summary>
public interface ISystemDocumentReader
{
    /// <summary>Reads an opaque document id from system storage without requiring client collection metadata.</summary>
    ValueTask<StoredDocument?> GetSystemAsync(string collection, string id, CancellationToken cancellationToken);
}
