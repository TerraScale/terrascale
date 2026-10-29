using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;
public sealed record DocumentKey(TenantContext Database, string Collection, string Id);
public sealed record Precondition(IReadOnlyList<string>? IfMatch, bool IfMatchAny, bool IfNoneMatch, bool IfNeverWritten = false)
{
    public static Precondition Absent { get; } = new(null, IfMatchAny: false, IfNoneMatch: true);
}
public abstract record DocumentOperation(string Collection);
public sealed record PutOperation(string Collection, string Id, ReadOnlyMemory<byte> Json, Precondition Precondition) : DocumentOperation(Collection);
public sealed record PatchOperation(string Collection, string Id, DocumentPatch Patch, Precondition Precondition) : DocumentOperation(Collection);
public sealed record DeleteOperation(string Collection, string Id, Precondition Precondition) : DocumentOperation(Collection);
public sealed record AssertOperation(string Collection, string Id, DocumentFilter? Filter) : DocumentOperation(Collection);
public sealed record AssertCountOperation(string Collection, DocumentFilter? Filter, long? AtLeast, long? AtMost) : DocumentOperation(Collection);
public sealed record OperationResult(string? Id, long? Version, bool Created, long? Count, string? ETag = null, ReadOnlyMemory<byte>? Document = null);
public sealed record WriteResult(long Offset, IReadOnlyList<OperationResult> Results, Guid Scope, string Durability = WriteResult.LocalDurable)
{
    public const string LocalDurable = "local_durable";
    public const string Unchanged = "unchanged";
}
public sealed record DocumentRejected(Failure Error, int? Op = null);
public readonly union CollectionResult(CollectionInfo, NotFound, Conflict, Unavailable);
public sealed record CollectionPage(IReadOnlyList<CollectionInfo> Collections, bool HasMore);
public sealed record NeverWritten;
public readonly union DocumentRead(StoredDocument, NeverWritten, NotFound, Validation, Forbidden);
public sealed record QueryOutcome(QueryPage Page, (long Count, bool Complete)? Count);
public readonly union QueryResult(QueryOutcome, NotFound, Validation, Forbidden);
public readonly union WriteOutcome(WriteResult, DocumentRejected);
public readonly union ApplyOutcome(OperationResult, DocumentRejected);
public sealed record ExportRow(string Collection, StoredDocument Document);
public interface IDocumentService
{
    ValueTask<CollectionSchemaPreviewResult> PreviewCollectionSchemaAsync(TenantContext database, string name, JsonElement schema, string? cursor, CancellationToken cancellationToken);
    ValueTask<CollectionRulesResult> GetCollectionRulesAsync(TenantContext database, string name, CancellationToken cancellationToken);
    ValueTask<CollectionRulesResult> SetCollectionRulesAsync(TenantContext database, string name, CollectionAccessRules rules, CancellationToken cancellationToken);
    ValueTask<CollectionSchemaResult> GetCollectionSchemaAsync(TenantContext database, string name, CancellationToken cancellationToken);
    ValueTask<CollectionSchemaResult> SetCollectionSchemaAsync(TenantContext database, string name, JsonElement? schema, CancellationToken cancellationToken);
    ValueTask<CollectionTtlResult> GetCollectionTtlAsync(TenantContext database, string name, CancellationToken cancellationToken);
    ValueTask<CollectionTtlResult> SetCollectionTtlAsync(TenantContext database, string name, string? path, CancellationToken cancellationToken);
    ValueTask<CollectionResult> CreateCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken);
    ValueTask<CollectionResult> GetCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken);
    ValueTask<Found<CollectionPage>> ListCollectionsAsync(TenantContext database, string? afterName, int limit, CancellationToken cancellationToken);
    ValueTask<CollectionResult> DropCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken);
    ValueTask<DocumentRead> GetAsync(DocumentKey key, CancellationToken cancellationToken);
    void AcceptRead(DocumentKey key, StoredDocument document);
    void AcceptRead(TenantContext database, string collection, QueryPage page);
    ValueTask<QueryResult> QueryAsync(TenantContext database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken);
    IAsyncEnumerable<ExportRow> ExportAsync(TenantContext database, string? afterCollection, string? afterId, CancellationToken cancellationToken);
    ValueTask<WriteOutcome> WriteAsync(
        TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken);
    ValueTask<WriteOutcome> WriteConfirmedAsync(
        TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken);
    ValueTask<ApplyOutcome> ApplyAsync(IStorageTransaction transaction, DocumentOperation operation, CancellationToken cancellationToken);
    ValueTask<QueryResult> QueryAsync(
        IStorageTransaction transaction, DocumentQuery query, long? countLimit, CancellationToken cancellationToken, QueryObservation? observation = null);
}
