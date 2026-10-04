using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

internal sealed class DocumentService(IStorageEngine engine, TimeProvider time) : IDocumentService
{
    // assert_count examines at most this many documents.
    private const long MaxAssertCount = 10_000;

    public async ValueTask<CollectionInfo> CreateCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, write: true, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var collection = new CollectionInfo(name, time.GetUtcNow().ToUnixTimeMilliseconds());
            if (!await transaction.CreateCollectionAsync(collection, cancellationToken).ConfigureAwait(false))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "name_taken", "the collection already exists");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return collection;
        }
    }

    public async ValueTask<CollectionInfo> GetCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await CollectionAsync(transaction, name, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(
        TenantContext database, string? afterName, int limit, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.ListCollectionsAsync(afterName, limit, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<CollectionInfo> DropCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, write: true, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var collection = await CollectionAsync(transaction, name, cancellationToken).ConfigureAwait(false);
            await transaction.DropCollectionAsync(name, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return collection;
        }
    }

    public async ValueTask<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(key.Database, write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await CollectionAsync(transaction, key.Collection, cancellationToken).ConfigureAwait(false);
            await RequireAddressAsync(transaction, key.Collection, key.Id, cancellationToken).ConfigureAwait(false);
            // A deleted document comes back as its tombstone, without a body, so its 404 can carry its version.
            return await transaction.GetAsync(key.Collection, key.Id, cancellationToken).ConfigureAwait(false)
                ?? (await transaction.GetVersionAsync(key.Collection, key.Id, cancellationToken).ConfigureAwait(false) is { Deleted: true } tombstone
                    ? new StoredDocument(key.Id, tombstone.Version, ReadOnlyMemory<byte>.Empty, transaction.Incarnation, Deleted: true)
                    : null);
        }
    }

    public async ValueTask<(QueryPage Page, (long Count, bool Complete)? Count)> QueryAsync(
        TenantContext database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await CollectionAsync(transaction, query.Collection, cancellationToken).ConfigureAwait(false);
            var page = await QueryPageAsync(transaction, query, cancellationToken).ConfigureAwait(false);
            (long Count, bool Complete)? count = countLimit is { } limit
                ? await CountAsync(transaction, query.Collection, query.Filter, limit, cancellationToken).ConfigureAwait(false)
                : null;
            return (page, count);
        }
    }

    public async ValueTask<WriteResult> WriteAsync(
        TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, write: true, cancellationToken).ConfigureAwait(false);
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

                    try
                    {
                        if (collections.Add(operation.Collection))
                        {
                            await CollectionAsync(transaction, operation.Collection, cancellationToken).ConfigureAwait(false);
                        }

                        results[i] = await ApplyAsync(transaction, operation, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OverflowException)
                    {
                        throw new ApiException(StatusCodes.Status503ServiceUnavailable, "capacity", "the commit offset is exhausted")
                        {
                            Op = indexErrors ? i : null,
                        };
                    }
                    catch (ApiException error) when (indexErrors)
                    {
                        error.Op = i;
                        throw;
                    }
                }
            }

            var offset = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WriteResult(offset, results, transaction.Incarnation);
        }
    }

    private static async ValueTask<OperationResult> ApplyAsync(
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
        if (id is not null)
        {
            await RequireAddressAsync(transaction, operation.Collection, id, cancellationToken).ConfigureAwait(false);
        }

        switch (operation)
        {
            case PutOperation put:
                {
                    // Preconditions need only the version, so the body being replaced is never read.
                    var current = await transaction.GetVersionAsync(put.Collection, put.Id, cancellationToken).ConfigureAwait(false);
                    Check(transaction.Incarnation, put.Precondition, put.Collection, put.Id, current);
                    var version = await transaction.PutAsync(put.Collection, put.Id, put.Json, cancellationToken).ConfigureAwait(false);
                    return new OperationResult(put.Id, version, current is not { Deleted: false }, null);
                }

            case PatchOperation patch:
                {
                    var current = await LiveAsync(transaction, patch.Collection, patch.Id, cancellationToken).ConfigureAwait(false);
                    Check(transaction.Incarnation, patch.Precondition, patch.Collection, patch.Id, new DocumentVersion(current.Version, Deleted: false));
                    var document = JsonNode.Parse(current.Json.Span)!.AsObject();
                    patch.Patch.Apply(document);
                    // Patch inputs passed RequestJson's limits, so the tree is shallow enough to serialize; the
                    // document limits then apply to the result.
                    var json = JsonSerializer.SerializeToUtf8Bytes(document, TerraBaseJsonContext.Default.JsonObject);
                    RequestJson.ValidateDocument(json);
                    var version = await transaction.PutAsync(patch.Collection, patch.Id, json, cancellationToken).ConfigureAwait(false);
                    return new OperationResult(patch.Id, version, false, null);
                }

            case DeleteOperation delete:
                {
                    var current = await transaction.GetVersionAsync(delete.Collection, delete.Id, cancellationToken).ConfigureAwait(false);
                    if (current is not { Deleted: false })
                    {
                        throw NotFound();
                    }

                    Check(transaction.Incarnation, delete.Precondition, delete.Collection, delete.Id, current);
                    var version = await transaction.TombstoneAsync(delete.Collection, delete.Id, cancellationToken).ConfigureAwait(false);
                    return new OperationResult(delete.Id, version, false, null);
                }

            case AssertOperation { Filter: null } absent:
                {
                    return await transaction.GetVersionAsync(absent.Collection, absent.Id, cancellationToken).ConfigureAwait(false) is not { Deleted: false }
                        ? new OperationResult(absent.Id, null, false, null)
                        : throw Failed("the document exists");
                }

            case AssertOperation assert:
                {
                    var filter = new AndFilter([assert.Filter, new CompareFilter(DocumentPath.Id, Comparison.Equal, new ScalarValue(ScalarKind.Text, assert.Id))]);
                    var page = await QueryPageAsync(transaction, new DocumentQuery(assert.Collection, filter, [], null, 1, long.MaxValue), cancellationToken)
                        .ConfigureAwait(false);
                    return page.Rows.Count == 1 ? new OperationResult(assert.Id, null, false, null) : throw Failed("the document does not match");
                }

            case AssertCountOperation assertCount:
                {
                    var (count, complete) = await CountAsync(transaction, assertCount.Collection, assertCount.Filter, MaxAssertCount, cancellationToken)
                        .ConfigureAwait(false);
                    if (!complete)
                    {
                        throw new ApiException(
                            StatusCodes.Status400BadRequest, "assert_requires_index", "assert_count examines at most 10000 documents");
                    }

                    return count < assertCount.AtLeast || count > assertCount.AtMost
                        ? throw Failed("the count is out of bounds")
                        : new OperationResult(null, null, false, count);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.GetType().Name, "unknown operation");
        }
    }

    private static async ValueTask<StoredDocument> LiveAsync(
        IStorageTransaction transaction, string collection, string id, CancellationToken cancellationToken) =>
        await transaction.GetAsync(collection, id, cancellationToken).ConfigureAwait(false)
            ?? throw NotFound();

    private static async ValueTask<(long Count, bool Complete)> CountAsync(
        IStorageTransaction transaction, string collection, DocumentFilter? filter, long limit, CancellationToken cancellationToken)
    {
        try
        {
            return await transaction.CountAsync(collection, filter, limit, cancellationToken).ConfigureAwait(false);
        }
        catch (QueryCapacityException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_query", "query too expensive");
        }
    }

    private static async ValueTask<QueryPage> QueryPageAsync(
        IStorageTransaction transaction, DocumentQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await transaction.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (QueryCapacityException error)
        {
            throw error.SortBudget
                ? new ApiException(StatusCodes.Status400BadRequest, "sort_requires_index", "the query exceeds the sort budget")
                : new ApiException(StatusCodes.Status400BadRequest, "invalid_query", "query too expensive");
        }
    }

    // Preserve schema-1 identities, including tombstones recreated by version, without admitting new legacy ids.
    private static async ValueTask RequireAddressAsync(
        IStorageTransaction transaction, string collection, string id, CancellationToken cancellationToken)
    {
        if (!DocumentRoute.IsUuid(id)
            && await transaction.GetVersionAsync(collection, id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new ApiException("a new id must be a lowercase hyphenated UUID");
        }
    }

    private static void Check(Guid incarnation, Precondition precondition, string collection, string id, DocumentVersion? current) =>
        ETags.Check(precondition, current is { Deleted: false }, current is { } state ? ETags.Version(incarnation, collection, id, state.Version) : null);

    private static ApiException NotFound() => new(StatusCodes.Status404NotFound, "not_found", "the document does not exist");

    private static ApiException Failed(string message) => new(StatusCodes.Status412PreconditionFailed, "precondition_failed", message);

    private static async ValueTask<CollectionInfo> CollectionAsync(
        IStorageTransaction transaction, string name, CancellationToken cancellationToken) =>
        await transaction.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "unknown_collection", "the collection does not exist");

    private async ValueTask<IStorageTransaction> BeginAsync(TenantContext database, bool write, CancellationToken cancellationToken)
    {
        var store = await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false)
            ?? throw DocumentRoute.UnknownDatabase();
        return await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
    }
}
