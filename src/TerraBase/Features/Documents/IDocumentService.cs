using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>Addresses one document.</summary>
/// <param name="Database">Owning database.</param>
/// <param name="Collection">Collection name (same rules as database names).</param>
/// <param name="Id">Document id: a generated canonical uppercase ULID or a caller-supplied key.</param>
public sealed record DocumentKey(TenantContext Database, string Collection, string Id);

/// <summary>Write preconditions (see <c>ETags</c>).</summary>
/// <param name="IfMatch">Versions one of which must be current (strong comparison); <see langword="null"/> for none.</param>
/// <param name="IfMatchAny">Whether the document must be live (<c>If-Match: *</c>).</param>
/// <param name="IfNoneMatch">Whether the document must be absent or deleted (<c>If-None-Match: *</c>).</param>
public sealed record Precondition(IReadOnlyList<string>? IfMatch, bool IfMatchAny, bool IfNoneMatch)
{
    /// <summary>The document must be absent or deleted: a create.</summary>
    public static Precondition Absent { get; } = new(null, IfMatchAny: false, IfNoneMatch: true);
}

/// <summary>One operation of an atomic write.</summary>
/// <param name="Collection">Target collection.</param>
public abstract record DocumentOperation(string Collection);

/// <summary>Inserts or replaces a document.</summary>
/// <param name="Collection">Target collection.</param>
/// <param name="Id">Document id.</param>
/// <param name="Json">Validated JSON object.</param>
/// <param name="Precondition">Preconditions.</param>
public sealed record PutOperation(string Collection, string Id, ReadOnlyMemory<byte> Json, Precondition Precondition) : DocumentOperation(Collection);

/// <summary>Patches a live document.</summary>
/// <param name="Collection">Target collection.</param>
/// <param name="Id">Document id.</param>
/// <param name="Patch">The patch.</param>
/// <param name="Precondition">Preconditions.</param>
public sealed record PatchOperation(string Collection, string Id, DocumentPatch Patch, Precondition Precondition) : DocumentOperation(Collection);

/// <summary>Deletes a live document.</summary>
/// <param name="Collection">Target collection.</param>
/// <param name="Id">Document id.</param>
/// <param name="Precondition">Preconditions.</param>
public sealed record DeleteOperation(string Collection, string Id, Precondition Precondition) : DocumentOperation(Collection);

/// <summary>Asserts a document matches <paramref name="Filter"/>, or is absent when it is <see langword="null"/>.</summary>
/// <param name="Collection">Target collection.</param>
/// <param name="Id">Document id.</param>
/// <param name="Filter">Required filter, or <see langword="null"/> to require absence.</param>
public sealed record AssertOperation(string Collection, string Id, DocumentFilter? Filter) : DocumentOperation(Collection);

/// <summary>Asserts the number of documents matching <paramref name="Filter"/>.</summary>
/// <param name="Collection">Target collection.</param>
/// <param name="Filter">Counted filter.</param>
/// <param name="AtLeast">Least count.</param>
/// <param name="AtMost">Greatest count.</param>
public sealed record AssertCountOperation(string Collection, DocumentFilter? Filter, long? AtLeast, long? AtMost) : DocumentOperation(Collection);

/// <summary>Result of one operation.</summary>
/// <param name="Id">Written or asserted document id.</param>
/// <param name="Version">New version of a written document.</param>
/// <param name="Created">Whether a put created the document (it was absent or deleted).</param>
/// <param name="Count">Count of an <see cref="AssertCountOperation"/>.</param>
/// <param name="ETag">Public validator for the written state, including its JSON or deletion state.</param>
public sealed record OperationResult(string? Id, long? Version, bool Created, long? Count, string? ETag = null);

/// <summary>Result of an atomic write.</summary>
/// <param name="Offset">Database commit offset.</param>
/// <param name="Results">One result per operation.</param>
/// <param name="Scope">ETag scope of the database that committed this write.</param>
public sealed record WriteResult(long Offset, IReadOnlyList<OperationResult> Results, Guid Scope);

/// <summary>A refused document request: the failure, and the failing operation's index when the caller asked for it.</summary>
/// <param name="Error">The failure, with its Documents API v1 code.</param>
/// <param name="Op">Index of the failing operation of a transaction, or <see langword="null"/>.</param>
public sealed record DocumentRejected(Failure Error, int? Op = null);

/// <summary>A collection, or why the request about it failed: unknown database or collection, a taken name, capacity.</summary>
public readonly union CollectionResult(CollectionInfo, NotFound, Conflict, Unavailable);

/// <summary>A page of collections in name order.</summary>
/// <param name="Collections">The collections.</param>
/// <param name="HasMore">Whether more follow.</param>
public sealed record CollectionPage(IReadOnlyList<CollectionInfo> Collections, bool HasMore);

/// <summary>A document that was never written: no live document and no tombstone.</summary>
public sealed record NeverWritten;

/// <summary>
/// A document or its tombstone; that it was never written; an unknown database or collection; or an address that is
/// outside the caller-supplied key bounds.
/// </summary>
public readonly union DocumentRead(StoredDocument, NeverWritten, NotFound, Validation);

/// <summary>A query page and its count, when one was asked for.</summary>
/// <param name="Page">The page.</param>
/// <param name="Count">Matches among at most the count limit, and whether that count is exact.</param>
public sealed record QueryOutcome(QueryPage Page, (long Count, bool Complete)? Count);

/// <summary>A query's page, or why there is none: an unknown database or collection, or a query over the engine budget.</summary>
public readonly union QueryResult(QueryOutcome, NotFound, Validation);

/// <summary>An atomic write's result, or the operation that refused it.</summary>
public readonly union WriteOutcome(WriteResult, DocumentRejected);

/// <summary>One applied operation, or why it was refused.</summary>
public readonly union ApplyOutcome(OperationResult, DocumentRejected);

/// <summary>JSON document operations for the client API, over <see cref="IStorageEngine"/>, with Documents API v1 failures.</summary>
public interface IDocumentService
{
    /// <summary>Reads the declared collection schema; null schema means optional validation is disabled.</summary>
    ValueTask<CollectionSchemaResult> GetCollectionSchemaAsync(TenantContext database, string name, CancellationToken cancellationToken);

    /// <summary>Attaches or replaces a declared schema, or detaches it when null; affects future local writes.</summary>
    ValueTask<CollectionSchemaResult> SetCollectionSchemaAsync(TenantContext database, string name, JsonElement? schema, CancellationToken cancellationToken);

    /// <summary>Creates a collection; <c>name_taken</c> when it exists.</summary>
    ValueTask<CollectionResult> CreateCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken);

    /// <summary>Reads a collection.</summary>
    ValueTask<CollectionResult> GetCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken);

    /// <summary>Lists collections in name order after <paramref name="afterName"/>.</summary>
    ValueTask<Found<CollectionPage>> ListCollectionsAsync(TenantContext database, string? afterName, int limit, CancellationToken cancellationToken);

    /// <summary>Drops a collection and every document in it.</summary>
    ValueTask<CollectionResult> DropCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken);

    /// <summary>Reads a document or its tombstone.</summary>
    ValueTask<DocumentRead> GetAsync(DocumentKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a query and, when <paramref name="countLimit"/> is set, counts matches among at most that many
    /// documents (<c>Complete</c> is <see langword="false"/> when more exist).
    /// </summary>
    ValueTask<QueryResult> QueryAsync(TenantContext database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken);

    /// <summary>
    /// Applies <paramref name="operations"/> atomically: assertions first, against the state before
    /// any write, then writes in order. With <paramref name="indexErrors"/>, a refusal names its operation.
    /// </summary>
    ValueTask<WriteOutcome> WriteAsync(
        TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken);

    /// <summary>
    /// Applies one operation in a write transaction the caller owns and commits, with the refusals of
    /// <see cref="WriteAsync"/>. Collections are not checked: the caller's store decides which exist.
    /// </summary>
    ValueTask<ApplyOutcome> ApplyAsync(IStorageTransaction transaction, DocumentOperation operation, CancellationToken cancellationToken);

    /// <summary>Runs a query, and a count as in <see cref="QueryAsync(TenantContext, DocumentQuery, long?, CancellationToken)"/>, in a transaction the caller owns.</summary>
    ValueTask<QueryResult> QueryAsync(
        IStorageTransaction transaction, DocumentQuery query, long? countLimit, CancellationToken cancellationToken, QueryObservation? observation = null);
}
