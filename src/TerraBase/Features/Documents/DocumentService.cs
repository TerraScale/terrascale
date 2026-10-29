using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Stats;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Documents;

internal sealed partial class DocumentService(IStorageEngine engine, TimeProvider time, DocumentCache cache, IUsageMeter usage,
    IQueryShapeTracker shapes, IMemoryBudget memory, IMeterFactory meters, IStorageQuota quota, IOperationAllowance allowance, IDatabaseStats stats,
    TerraBaseOptions options, ILogger<DocumentService> logger, IStorageCopyFaults faults)
    : IDocumentService, ISystemDocumentReader
{
    private const long MaxAssertCount = 10_000;

    private readonly Counter<long> _schemaRejections = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.schema.validation_failures", "{write}");

    private static readonly NotFound UnknownCollection = new("the collection does not exist", "unknown_collection");
    private static readonly NotFound DocumentMissing = new("the document does not exist", "not_found");
    private static readonly Conflict CollectionExists = new("the collection already exists", "name_taken");
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

        var collection = new CollectionInfo(name, time.GetUtcNow().ToUnixTimeMilliseconds(), Id: EntityIds.New(time).ToString());
        await using (transaction.ConfigureAwait(false))
        {
            if (!await transaction.CreateCollectionAsync(collection, cancellationToken).ConfigureAwait(false))
            {
                return CollectionExists;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return collection;
    }

    public ValueTask<CollectionResult> GetCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
        ReadCertifiedAsync<CollectionResult>(database, _ => ReadCollectionAsync(database, name, cancellationToken),
            static () => DocumentRoute.UnknownDatabase, cancellationToken);

    private async ValueTask<CollectionResult> ReadCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return DocumentRoute.UnknownDatabase;
        }

        return await store.FindCollectionAsync(name, cancellationToken).ConfigureAwait(false) is { } collection ? collection : UnknownCollection;
    }

    public ValueTask<Found<CollectionPage>> ListCollectionsAsync(
        TenantContext database, string? afterName, int limit, CancellationToken cancellationToken) =>
        ReadCertifiedAsync<Found<CollectionPage>>(database, _ => ReadCollectionsAsync(database, afterName, limit, cancellationToken),
            static () => DocumentRoute.UnknownDatabase, cancellationToken);

    private async ValueTask<Found<CollectionPage>> ReadCollectionsAsync(
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
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return DocumentRoute.UnknownDatabase;
        }

        CollectionInfo collection;
        var entered = await quota.EnterAsync(database, store, cancellationToken).ConfigureAwait(false);
        if (entered is not StorageQuotaLease admission)
        {
            return entered switch
            {
                Unavailable error => error,
                StorageQuotaLease => throw new UnreachableException(),
            };
        }

        using (admission)
        {
            var transaction = await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
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
        }

        return collection;
    }

    public ValueTask<DocumentRead> GetAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        var hit = new StrongBox<bool>();
        return ReadCertifiedAsync<DocumentRead>(key.Database, incarnation => CollectionRuleAccess.EndUser
            ? ReadAsync(key, cancellationToken) : ReadCachedDocumentAsync(key, incarnation, hit, cancellationToken),
            static () => DocumentRoute.UnknownDatabase, cancellationToken, hit);
    }

    private async ValueTask<DocumentRead> ReadCachedDocumentAsync(DocumentKey key, Ulid incarnation, StrongBox<bool> hit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentRead? missed = null;
        var fromStorage = false;
        var document = await cache.GetAsync(key, incarnation, async () =>
        {
            fromStorage = true;
            var read = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (read is StoredDocument stored)
            {
                return stored;
            }

            missed = read;
            return null;
        }).ConfigureAwait(false);
        hit.Value = !fromStorage;
        if (document is not null)
        {
            return document;
        }

        return missed ?? new NeverWritten();
    }

    public void AcceptRead(DocumentKey key, StoredDocument document)
    {
        usage.RecordRead(key.Database, OperationUnits.Of(document.Json.Length));
        stats.CollectionRead(key.Database, key.Collection);
    }

    public void AcceptRead(TenantContext database, string collection, QueryPage page)
    {
        usage.RecordRead(database, page.Rows.Sum(row => OperationUnits.Of(row.Document.Json.Length)));
        stats.CollectionRead(database, collection);
    }

    public ValueTask<StoredDocument?> GetSystemAsync(string collection, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return cache.GetSystemAsync(collection, id, () => ReadSystemAsync(collection, id, cancellationToken));
    }
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
        if (await engine.OpenDatabaseAsync(key.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return DocumentRoute.UnknownDatabase;
        }

        if (!await store.HasCollectionAsync(key.Collection, cancellationToken).ConfigureAwait(false))
        {
            return UnknownCollection;
        }

        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var access = ProjectAccess.Current;
            var rules = access is { IsEndUser: true } ? await transaction.GetCollectionRulesAsync(key.Collection, cancellationToken).ConfigureAwait(false) : null;
            if (access is { IsEndUser: true } && !CollectionRuleAccess.Granted(rules, access, write: false))
            {
                return new Forbidden("the collection rules deny this operation", "access_denied");
            }

            var stored = await transaction.GetAsync(key.Collection, key.Id, cancellationToken).ConfigureAwait(false);
            if (access is { IsEndUser: true } && (stored is null || !CollectionRuleAccess.CanRead(rules, stored.Json, access)))
            {
                return DocumentMissing;
            }
            if (stored is { } document)
            {
                return document;
            }

            return await transaction.GetVersionAsync(key.Collection, key.Id, cancellationToken).ConfigureAwait(false) is { Deleted: true } tombstone
                ? new StoredDocument(key.Id, tombstone.Version, ReadOnlyMemory<byte>.Empty, transaction.Scope, Deleted: true)
                : new NeverWritten();
        }
    }

    public ValueTask<QueryResult> QueryAsync(
        TenantContext database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken) =>
        ReadCertifiedAsync<QueryResult>(database, incarnation => CollectionRuleAccess.EndUser || query.Explain
            ? ReadQueryAsync(database, query, countLimit, new QueryObservation(), cancellationToken)
            : ReadCachedQueryAsync(database, incarnation, query, countLimit, cancellationToken),
            static () => DocumentRoute.UnknownDatabase, cancellationToken);

    private async ValueTask<QueryResult> ReadCachedQueryAsync(
        TenantContext database, Ulid incarnation, DocumentQuery query, long? countLimit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? shape;
        IMemoryReservation? retained = null;
        using (var scratch = memory.TryReserve(MemoryLane.Foreground, 64 * 1024))
        {
            shape = scratch is null ? null : QueryShape.Normalize(query);
            if (shape is not null)
            {
                retained = memory.TryReserve(MemoryLane.Foreground, 32L + 2L * shape.Length);
            }
        }

        using var reservation = retained;
        if (reservation is null)
        {
            shape = null;
        }
        var observation = new QueryObservation();
        var started = time.GetTimestamp();
        QueryResult? refused = null;
        try
        {
            var result = await cache.QueryAsync(database, incarnation, query, countLimit, async () =>
            {
                var read = await ReadQueryAsync(database, query, countLimit, observation, cancellationToken).ConfigureAwait(false);
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

            return new QueryOutcome(result.Page, result.Count);
        }
        finally
        {
            if (refused is not NotFound)
            {
                shapes.Record(database, query.Collection, shape ?? string.Empty, time.GetElapsedTime(started), observation.RowsScanned);
            }
        }
    }

    private async ValueTask<QueryResult> ReadQueryAsync(
        TenantContext database, DocumentQuery query, long? countLimit, QueryObservation observation, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return DocumentRoute.UnknownDatabase;
        }

        if (!await store.HasCollectionAsync(query.Collection, cancellationToken).ConfigureAwait(false))
        {
            return UnknownCollection;
        }

        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await QueryAsync(transaction, query, countLimit, cancellationToken, observation).ConfigureAwait(false);
        }
    }

    public async ValueTask<QueryResult> QueryAsync(
        IStorageTransaction transaction, DocumentQuery query, long? countLimit, CancellationToken cancellationToken, QueryObservation? observation = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (ProjectAccess.Current is { IsEndUser: true } access)
        {
            var rules = await transaction.GetCollectionRulesAsync(query.Collection, cancellationToken).ConfigureAwait(false);
            if (!CollectionRuleAccess.Granted(rules, access, write: false))
            {
                return new Forbidden("the collection rules deny this operation", "access_denied");
            }

            if (CollectionRuleAccess.Filter(rules!, access) is { } authorization)
            {
                query = query with { Filter = new AuthorizedFilter(authorization, query.Filter) };
            }
        }

        try
        {
            var page = await transaction.QueryAsync(query, cancellationToken, observation).ConfigureAwait(false);
            (long Count, bool Complete)? count = countLimit is { } limit
                ? await transaction.CountAsync(query.Collection, query.Filter, limit, cancellationToken, observation).ConfigureAwait(false)
                : null;
            return new QueryOutcome(page, count);
        }
        catch (QueryCapacityException error)
        {
            return error.SortBudget ? SortBudget : TooExpensive;
        }
        catch (QueryValidationException error)
        {
            return new Validation(error.Message, "invalid_query");
        }
    }

    public async IAsyncEnumerable<ExportRow> ExportAsync(
        TenantContext database, string? afterCollection, string? afterId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            yield break;
        }

        var incarnation = store.Incarnation;
        if (await faults.RefusedAsync(database, incarnation, cancellationToken).ConfigureAwait(false))
        {
            throw new StorageCopyFaultedException("this database copy requires storage recovery");
        }
        var units = 0L;
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            if (afterCollection is not null && await transaction.GetCollectionAsync(afterCollection, cancellationToken).ConfigureAwait(false) is not null)
            {
                await foreach (var row in ExportCollectionAsync(transaction, afterCollection, afterId, cancellationToken).ConfigureAwait(false))
                {
                    units += OperationUnits.Of(row.Document.Json.Length);
                    yield return row;
                }
            }

            var after = afterCollection;
            bool more;
            do
            {
                (var collections, more) = await transaction.ListCollectionsAsync(after, 200, cancellationToken).ConfigureAwait(false);
                foreach (var collection in collections)
                {
                    await foreach (var row in ExportCollectionAsync(transaction, collection.Name, null, cancellationToken).ConfigureAwait(false))
                    {
                        units += OperationUnits.Of(row.Document.Json.Length);
                        yield return row;
                    }

                    after = collection.Name;
                }
            }
            while (more);
        }

        usage.RecordRead(database, units);
        if (await faults.RefusedAsync(database, incarnation, cancellationToken).ConfigureAwait(false))
        {
            throw new StorageCopyFaultedException("this database copy changed while it was being read");
        }
    }

    private static async IAsyncEnumerable<ExportRow> ExportCollectionAsync(
        IStorageTransaction transaction, string collection, string? afterId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var position = afterId is null ? null : new QueryPosition([], afterId);
        while (true)
        {
            var page = await transaction.QueryAsync(
                new DocumentQuery(collection, null, [], position, QueryParser.MaxLimit, DocumentRoute.MaxPageBytes), cancellationToken).ConfigureAwait(false);
            foreach (var row in page.Rows)
            {
                yield return new ExportRow(collection, row.Document);
            }

            if (!page.HasMore)
            {
                yield break;
            }

            position = new QueryPosition(page.Rows[^1].Keys, page.Rows[^1].Document.Id);
        }
    }
    public ValueTask<ApplyOutcome> ApplyAsync(IStorageTransaction transaction, DocumentOperation operation, CancellationToken cancellationToken) =>
        ApplyWithAccessAsync(transaction, operation, ProjectAccess.Current, cancellationToken);

    private async ValueTask<ApplyOutcome> ApplyWithAccessAsync(
        IStorageTransaction transaction, DocumentOperation operation, ProjectAccess? access, CancellationToken cancellationToken)
    {
        try
        {
            if (await AuthorizeOperationAsync(transaction, operation, access, cancellationToken).ConfigureAwait(false) is { } denied)
            {
                return denied;
            }

            return await ApplyOperationAsync(transaction, operation, access, cancellationToken).ConfigureAwait(false);
        }
        catch (DocumentValidationException error)
        {
            return new DocumentRejected(error.TooLarge ? new PayloadTooLarge(error.Message) : new Validation(error.Message));
        }
        catch (CollectionSchemaException error)
        {
            _schemaRejections.Add(1);
            return new DocumentRejected(new Validation("the document does not satisfy the collection schema", "schema_validation_failed", error.Details));
        }
        catch (UniqueConstraintException error)
        {
            return new DocumentRejected(new Conflict(error.Message, "unique_violation"));
        }
        catch (StorageWriteLimitException)
        {
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
        TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken) =>
        (await CommitAsync(database, operations, indexErrors, cancellationToken).ConfigureAwait(false)).Outcome;

    public async ValueTask<WriteOutcome> WriteConfirmedAsync(
        TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken)
    {
        var mutations = operations.Any(operation => operation is PutOperation or PatchOperation or DeleteOperation);
        try
        {
            return await WriteAsync(database, operations, indexErrors, cancellationToken).ConfigureAwait(false) switch
            {
                WriteResult written when !mutations => written with { Durability = WriteResult.Unchanged },
                WriteResult written => written,
                DocumentRejected rejected => rejected,
            };
        }
        catch (Exception error) when (error is not StorageBusyException && !cancellationToken.IsCancellationRequested)
        {
            logger.ZLogError(error, $"A write to database {database.DatabaseId:@Database} failed at or after its commit; it is answered as outcome unknown");
            return Unknown("the write may or may not have committed; its outcome is unknown");
        }
    }

    private DocumentRejected Unknown(string message) => new(new OutcomeUnknown(message, options.Region, options.MachineId));
    private async ValueTask<(WriteOutcome Outcome, CommitMarker? Marker)> CommitAsync(
        TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return (new DocumentRejected(DocumentRoute.UnknownDatabase), null);
        }
        if (operations.Any(operation => operation is PutOperation or PatchOperation)
            && await allowance.CheckAsync(database, cancellationToken).ConfigureAwait(false) is var allowed && allowed is not Done)
        {
            return (allowed switch
            {
                QuotaExceeded error => new DocumentRejected(error),
                Unavailable error => new DocumentRejected(error),
                Done => throw new UnreachableException(),
            }, null);
        }

        var entered = await quota.EnterAsync(database, store, cancellationToken).ConfigureAwait(false);
        if (entered is not StorageQuotaLease admission)
        {
            return (entered switch
            {
                Unavailable error => new DocumentRejected(error),
                StorageQuotaLease => throw new UnreachableException(),
            }, null);
        }

        using var lease = admission;
        var delta = 0L;
        var units = 0L;
        var access = ProjectAccess.Current;
        var (outcome, commit) = await store.WriteAsync(
            async (transaction, token) =>
            {
                var (result, change, growthOp, written) = await ApplyAllAsync(store, transaction, operations, indexErrors, admission.RequiresAccounting, access, token).ConfigureAwait(false);
                (delta, units) = (change, written);
                if (result is not WriteResult)
                {
                    return result;
                }

                return await admission.CheckAsync(delta, token).ConfigureAwait(false) switch
                {
                    Done => result,
                    QuotaExceeded error => new DocumentRejected(error, indexErrors ? growthOp : null),
                    Unavailable error => new DocumentRejected(error, indexErrors ? growthOp : null),
                    null => throw new UnreachableException(),
                };
            }, outcome => outcome is WriteResult, cancellationToken)
            .ConfigureAwait(false);
        if (outcome is not WriteResult written)
        {
            return (outcome, null);
        }

        try
        {
            RecordCommitted(database, operations, admission, delta, units);
        }
        catch (Exception error)
        {
            logger.ZLogError(error, $"Accounting a committed write to database {database.DatabaseId:@Database} failed; the write is answered as committed");
        }

        return (written with { Offset = commit.Offset }, commit.Marker);
    }
    private void RecordCommitted(TenantContext database, IReadOnlyList<DocumentOperation> operations, StorageQuotaLease admission, long delta, long units)
    {
        var writes = operations.Count(operation => operation is PutOperation or PatchOperation or DeleteOperation);
        if (writes > 0)
        {
            admission.Committed(delta);
            usage.RecordWrites(database, writes, units);
            foreach (var operation in operations)
            {
                if (operation is PutOperation or PatchOperation or DeleteOperation)
                {
                    stats.CollectionWrote(database, operation.Collection);
                }
            }

            foreach (var group in operations.OfType<DocumentOperation>().GroupBy(operation => operation switch
            {
                PutOperation => "put",
                PatchOperation => "patch",
                DeleteOperation => "delete",
                _ => "",
            }).Where(group => group.Key.Length > 0))
            {
                usage.RecordOperation(database, group.Key, group.Count());
            }
        }
    }
    private async ValueTask<(WriteOutcome Outcome, long Delta, int? GrowthOp, long Units)> ApplyAllAsync(
        IDatabaseStore store, IStorageTransaction transaction, IReadOnlyList<DocumentOperation> operations, bool indexErrors, bool accountStorage,
        ProjectAccess? access, CancellationToken cancellationToken)
    {
        var collections = new HashSet<string>(StringComparer.Ordinal);
        var results = new OperationResult[operations.Count];
        var delta = 0L;
        var units = 0L;
        int? growthOp = null;
        foreach (var assertions in new[] { true, false })
        {
            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];
                if ((operation is AssertOperation or AssertCountOperation) != assertions)
                {
                    continue;
                }

                var id = operation switch
                {
                    PutOperation put => put.Id,
                    PatchOperation patch => patch.Id,
                    DeleteOperation delete => delete.Id,
                    _ => null,
                };
                var before = accountStorage && id is not null
                    ? await transaction.GetDocumentBytesAsync(operation.Collection, id, cancellationToken).ConfigureAwait(false) : 0;
                switch (await ApplyCheckedAsync(store, transaction, operation, collections, access, cancellationToken).ConfigureAwait(false))
                {
                    case OperationResult result:
                        results[i] = result;
                        var after = operation switch
                        {
                            PutOperation put => await transaction.GetDocumentBytesAsync(put.Collection, put.Id, cancellationToken).ConfigureAwait(false),
                            PatchOperation patch => await transaction.GetDocumentBytesAsync(patch.Collection, patch.Id, cancellationToken).ConfigureAwait(false),
                            _ => 0,
                        };
                        units += operation is DeleteOperation ? 1 : OperationUnits.Of(after);
                        if (accountStorage)
                        {
                            delta += after - before;
                            if (after > before)
                            {
                                growthOp = i;
                            }
                        }

                        break;
                    case DocumentRejected rejected:
                        return (rejected with { Op = indexErrors ? i : null }, 0, null, 0);
                }
            }
        }

        return (new WriteResult(0, results, transaction.Scope), delta, growthOp, units);
    }
    private async ValueTask<ApplyOutcome> ApplyCheckedAsync(
        IDatabaseStore store, IStorageTransaction transaction, DocumentOperation operation, HashSet<string> collections, ProjectAccess? access, CancellationToken cancellationToken)
    {
        if (collections.Add(operation.Collection) && !await store.HasCollectionAsync(operation.Collection, cancellationToken).ConfigureAwait(false))
        {
            return new DocumentRejected(UnknownCollection);
        }

        return await ApplyWithAccessAsync(transaction, operation, access, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ApplyOutcome> ApplyOperationAsync(
        IStorageTransaction transaction, DocumentOperation operation, ProjectAccess? access, CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case PutOperation put:
                {
                    var current = await transaction.GetVersionAsync(put.Collection, put.Id, cancellationToken).ConfigureAwait(false);
                    if (await CheckAsync(transaction, put.Precondition, put.Collection, put.Id, current, cancellationToken).ConfigureAwait(false) is { } failed)
                    {
                        return new DocumentRejected(failed);
                    }
                    _ = DocumentJson.Read(put.Json, requireObject: true);
                    var json = EntityPayload.WithoutId(put.Json, put.Id);
                    var (version, stored) = await transaction.PutAdmittedAsync(put.Collection, put.Id, json, cancellationToken).ConfigureAwait(false);
                    return new OperationResult(put.Id, version, current is not { Deleted: false }, null,
                        ETags.Version(transaction.Scope, put.Collection, put.Id, version, stored));
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
                    var hadId = document.ContainsKey("id");
                    var operations = new List<FieldOperation>();
                    switch (patch.Patch.Apply(document, operations))
                    {
                        case Validation error:
                            return new DocumentRejected(error);
                        case Conflict error:
                            return new DocumentRejected(error);
                    }
                    var json = JsonSerializer.SerializeToUtf8Bytes(document, TerraBaseJsonContext.Default.JsonObject);
                    _ = DocumentJson.Read(json, requireObject: true);
                    if (document["id"] is JsonValue identity && identity.GetValueKind() == JsonValueKind.String && identity.GetValue<string>() == patch.Id)
                    {
                        document.Remove("id");
                        operations.RemoveAll(operation => operation.Field == "id");
                        if (hadId)
                        {
                            operations.Add(new FieldOperation(FieldOperationKind.Unset, "id"));
                        }

                        json = JsonSerializer.SerializeToUtf8Bytes(document, TerraBaseJsonContext.Default.JsonObject);
                    }

                    if (access is { IsEndUser: true }
                        && await transaction.GetCollectionRulesAsync(patch.Collection, cancellationToken).ConfigureAwait(false) is { Owner: not null } rules
                        && rules.Subject(json) != rules.Subject(current.Json))
                    {
                        return AccessDenied;
                    }

                    var version = await transaction.PatchAsync(patch.Collection, patch.Id, json, operations, cancellationToken).ConfigureAwait(false);
                    var stored = await transaction.GetAsync(patch.Collection, patch.Id, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("a patched document has no body in the same transaction");
                    ReadOnlyMemory<byte>? response = stored.Json;
                    if (access is { IsEndUser: true })
                    {
                        var storedRules = await transaction.GetCollectionRulesAsync(patch.Collection, cancellationToken).ConfigureAwait(false);
                        if (storedRules is { Owner: not null } && storedRules.Subject(stored.Json) != storedRules.Subject(current.Json))
                        {
                            return AccessDenied;
                        }
                        if (!CollectionRuleAccess.CanRead(storedRules, stored.Json, access))
                        {
                            response = null;
                        }
                    }

                    return new OperationResult(patch.Id, version, false, null,
                        ETags.Version(transaction.Scope, patch.Collection, patch.Id, version, stored.Json), response);
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

            case AssertOperation { Filter: null } absent when access is not { IsEndUser: true }:
                {
                    return await transaction.GetVersionAsync(absent.Collection, absent.Id, cancellationToken).ConfigureAwait(false) is not { Deleted: false }
                        ? new OperationResult(absent.Id, null, false, null)
                        : new DocumentRejected(Failed("the document exists"));
                }

            case AssertOperation assert:
                {
                    DocumentFilter filter = new CompareFilter(DocumentPath.Id, Comparison.Equal, new ScalarValue(ScalarKind.Text, assert.Id));
                    if (assert.Filter is not null)
                    {
                        filter = new AndFilter([assert.Filter, filter]);
                    }

                    if (access is { IsEndUser: true }
                        && await transaction.GetCollectionRulesAsync(assert.Collection, cancellationToken).ConfigureAwait(false) is { } rules
                        && CollectionRuleAccess.Filter(rules, access) is { } authorization)
                    {
                        filter = new AuthorizedFilter(authorization, filter);
                    }

                    try
                    {
                        var page = await transaction.QueryAsync(new DocumentQuery(assert.Collection, filter, [], null, 1, long.MaxValue), cancellationToken)
                            .ConfigureAwait(false);
                        if (page.Rows.Count == (assert.Filter is null ? 0 : 1))
                        {
                            return new OperationResult(assert.Id, null, false, null);
                        }

                        return new DocumentRejected(Failed(assert.Filter is null ? "the document exists" : "the document does not match"));
                    }
                    catch (QueryCapacityException error)
                    {
                        return new DocumentRejected(error.SortBudget ? SortBudget : TooExpensive);
                    }
                    catch (QueryValidationException error)
                    {
                        return new DocumentRejected(new Validation(error.Message, "invalid_query"));
                    }
                }

            case AssertCountOperation assertCount:
                {
                    long count;
                    bool complete;
                    try
                    {
                        var filter = assertCount.Filter;
                        if (access is { IsEndUser: true }
                            && await transaction.GetCollectionRulesAsync(assertCount.Collection, cancellationToken).ConfigureAwait(false) is { } rules)
                        {
                            if (CollectionRuleAccess.Filter(rules, access) is { } authorization)
                            {
                                filter = new AuthorizedFilter(authorization, filter);
                            }
                        }

                        (count, complete) = await transaction.CountAsync(assertCount.Collection, filter, MaxAssertCount, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (QueryCapacityException)
                    {
                        return new DocumentRejected(TooExpensive);
                    }
                    catch (QueryValidationException error)
                    {
                        return new DocumentRejected(new Validation(error.Message, "invalid_query"));
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
    private static async ValueTask<PreconditionFailed?> CheckAsync(
        IStorageTransaction transaction, Precondition precondition, string collection, string id, DocumentVersion? current,
        CancellationToken cancellationToken, ReadOnlyMemory<byte>? json = null)
    {
        string? version = null;
        if ((precondition.IfMatch is not null || precondition.IfNeverWritten) && current is { } state)
        {
            var body = state.Deleted ? ReadOnlyMemory<byte>.Empty
                : json ?? (await transaction.GetAsync(collection, id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("a live document version has no body in the same transaction")).Json;
            version = ETags.Version(transaction.Scope, collection, id, state.Version, body, state.Deleted);
        }

        return ETags.CheckVersion(precondition, current is { Deleted: false }, version) is PreconditionFailed failed ? failed : null;
    }

    private static PreconditionFailed Failed(string message) => new(message, "precondition_failed");
    private async ValueTask<T> ReadCertifiedAsync<T>(
        TenantContext database, Func<Ulid, ValueTask<T>> read, Func<T> missing, CancellationToken cancellationToken, StrongBox<bool>? hit = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return missing();
        }

        var incarnation = store.Incarnation;
        if (await faults.RefusedAsync(database, incarnation, cancellationToken).ConfigureAwait(false))
        {
            throw new StorageCopyFaultedException("this database copy requires storage recovery");
        }

        var result = await read(incarnation).ConfigureAwait(false);
        if (hit?.Value != true && await faults.RefusedAsync(database, incarnation, cancellationToken).ConfigureAwait(false))
        {
            throw new StorageCopyFaultedException("this database copy changed while it was being read");
        }

        return result;
    }
    private async ValueTask<IStorageTransaction?> BeginAsync(TenantContext database, bool write, CancellationToken cancellationToken) =>
        await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is { } store
            ? await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false)
            : null;
}
public interface ISystemDocumentReader
{
    ValueTask<StoredDocument?> GetSystemAsync(string collection, string id, CancellationToken cancellationToken);
}
