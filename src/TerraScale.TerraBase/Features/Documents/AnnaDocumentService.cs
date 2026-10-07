using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Engine;
using TerraBase.Engine.Anna;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Management.Usage;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>
/// <see cref="IDocumentService"/> under <c>core=anna</c> (TS-634 slice 2): point reads and writes run on the per-core
/// actors and live only in memory; collections stay on the SQLite engine; lists, queries and transactions answer
/// <c>503</c> until the disk tier ships. Answers match <see cref="DocumentService"/> otherwise, versions included.
/// </summary>
internal sealed class AnnaDocumentService(DocumentService sqlite, IDocumentActors actors, IUsageMeter usage) : IDocumentService
{
    /// <summary>What lists, queries, transactions and shapes answer until the disk tier ships.</summary>
    public static readonly Unavailable NeedsDiskTier = new("lists, queries, transactions and shapes need the Anna core's disk tier, which is not built yet");

    private static readonly NotFound DocumentMissing = new("the document does not exist", "not_found");
    private static readonly Validation NewIdRule = new("a new id must be a lowercase hyphenated UUID");

    public ValueTask<CollectionResult> CreateCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
        sqlite.CreateCollectionAsync(database, name, cancellationToken);

    public ValueTask<CollectionResult> GetCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
        sqlite.GetCollectionAsync(database, name, cancellationToken);

    public ValueTask<Found<CollectionPage>> ListCollectionsAsync(TenantContext database, string? afterName, int limit, CancellationToken cancellationToken) =>
        sqlite.ListCollectionsAsync(database, afterName, limit, cancellationToken);

    public async ValueTask<CollectionResult> DropCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var dropped = await sqlite.DropCollectionAsync(database, name, cancellationToken).ConfigureAwait(false);
        if (dropped is CollectionInfo)
        {
            // ponytail: a write racing the drop can outlive it; the disk tier (slice 3) drops collections in one place.
            await actors.PurgeAsync(database.VersionScope(), name, CancellationToken.None).ConfigureAwait(false);
        }

        return dropped;
    }

    public async ValueTask<DocumentRead> GetAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (await sqlite.GetCollectionAsync(key.Database, key.Collection, cancellationToken).ConfigureAwait(false) is NotFound missing)
        {
            return missing;
        }

        // Only the SQLite core holds schema-1 legacy ids.
        if (!DocumentRoute.IsUuid(key.Id))
        {
            return NewIdRule;
        }

        var scope = key.Database.VersionScope();
        var register = await actors.RunAsync(
            new DocumentAddress(scope, key.Collection, key.Id), 0, static (register, _) => new ActorStep<MvRegister<DocumentValue>>(null, register),
            cancellationToken).ConfigureAwait(false);
        usage.RecordRead(key.Database);
        return Current(register) is var (dot, value)
            ? new StoredDocument(key.Id, (long)dot.Counter, value.Json, scope, value.Deleted)
            : new NeverWritten();
    }

    public ValueTask<QueryResult> QueryAsync(TenantContext database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken) =>
        ValueTask.FromResult<QueryResult>(NeedsDiskTier);

    public async ValueTask<WriteOutcome> WriteAsync(
        TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (indexErrors || operations is not [PutOperation or PatchOperation or DeleteOperation])
        {
            return new DocumentRejected(NeedsDiskTier);
        }

        var operation = operations[0];
        if (await sqlite.GetCollectionAsync(database, operation.Collection, cancellationToken).ConfigureAwait(false) is NotFound missing)
        {
            return new DocumentRejected(missing);
        }

        var id = operation switch
        {
            PutOperation put => put.Id,
            PatchOperation patch => patch.Id,
            DeleteOperation delete => delete.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(operations), operation.GetType().Name, "not a point write"),
        };
        if (!DocumentRoute.IsUuid(id))
        {
            return new DocumentRejected(NewIdRule);
        }

        var scope = database.VersionScope();
        var applied = await actors.RunAsync(
            new DocumentAddress(scope, operation.Collection, id), operation is PutOperation { Json.Length: var length } ? length : 0,
            (register, replica) => Apply(scope, operation, id, register, replica), cancellationToken).ConfigureAwait(false);
        switch (applied)
        {
            case OperationResult result:
                usage.RecordWrites(database, 1);
                // Memory only: the actors keep no log, so there is no commit offset yet.
                return new WriteResult(0, [result], scope, Durability: "memory");
            case DocumentRejected rejected:
                return rejected;
            default:
                throw new InvalidOperationException("unknown write outcome");
        }
    }

    public ValueTask<ApplyOutcome> ApplyAsync(IStorageTransaction transaction, DocumentOperation operation, CancellationToken cancellationToken) =>
        sqlite.ApplyAsync(transaction, operation, cancellationToken);

    public ValueTask<QueryResult> QueryAsync(IStorageTransaction transaction, DocumentQuery query, long? countLimit, CancellationToken cancellationToken) =>
        sqlite.QueryAsync(transaction, query, countLimit, cancellationToken);

    // One point write on the owning actor's thread, with DocumentService's preconditions and refusals.
    private static ActorStep<ApplyOutcome> Apply(Guid scope, DocumentOperation operation, string id, MvRegister<DocumentValue> register, ulong replica)
    {
        var current = Current(register);
        var live = current is { Value.Deleted: false };
        var tag = current is var (dot, value) ? ETags.Version(scope, operation.Collection, id, (long)dot.Counter, value.Json, value.Deleted) : null;
        if (operation is PatchOperation or DeleteOperation && !live)
        {
            return Refuse(DocumentMissing);
        }

        var precondition = operation switch
        {
            PutOperation put => put.Precondition,
            PatchOperation patch => patch.Precondition,
            DeleteOperation delete => delete.Precondition,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation.GetType().Name, "not a point write"),
        };
        if (ETags.CheckVersion(precondition, live, tag) is PreconditionFailed failed)
        {
            return Refuse(failed);
        }

        ReadOnlyMemory<byte> json;
        switch (operation)
        {
            case PutOperation put:
                json = put.Json;
                break;
            case PatchOperation patch:
                var document = JsonNode.Parse(current!.Value.Value.Json.Span, documentOptions: RequestJson.StoredDocumentOptions)!.AsObject();
                switch (patch.Patch.Apply(document))
                {
                    case Validation error:
                        return Refuse(error);
                    case Conflict error:
                        return Refuse(error);
                }

                json = JsonSerializer.SerializeToUtf8Bytes(document, TerraBaseJsonContext.Default.JsonObject);
                break;
            default:
                json = ReadOnlyMemory<byte>.Empty;
                break;
        }

        var written = new DocumentValue(json, Deleted: operation is DeleteOperation);
        if (!written.Deleted)
        {
            // The same admission contract as the SQLite core: the strict client limits, also on a patched result.
            try
            {
                _ = DocumentJson.Read(json, requireObject: true);
            }
            catch (DocumentValidationException error)
            {
                return Refuse(error.TooLarge ? new PayloadTooLarge(error.Message) : new Validation(error.Message));
            }
        }

        var next = register.Join(register.WriteDelta(replica, written));
        var (newDot, _) = Current(next)!.Value;
        return new ActorStep<ApplyOutcome>(
            next,
            new OperationResult(id, (long)newDot.Counter, !live, null, ETags.Version(scope, operation.Collection, id, (long)newDot.Counter, written.Json, written.Deleted)));
    }

    // The document's version: one writer per document, so the register holds at most one value.
    private static (Dot Dot, DocumentValue Value)? Current(MvRegister<DocumentValue> register)
    {
        if (register.Values.Count == 0)
        {
            return null;
        }

        var (dot, value) = register.Values.MaxBy(pair => (pair.Key.Counter, pair.Key.Replica));
        return (dot, value);
    }

    private static ActorStep<ApplyOutcome> Refuse(Failure failure) => new(null, new DocumentRejected(failure));
}
