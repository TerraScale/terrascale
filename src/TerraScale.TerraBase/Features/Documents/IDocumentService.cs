using TerraScale.TerraBase.Engine;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>Addresses one document.</summary>
/// <param name="Database">Owning database.</param>
/// <param name="Collection">Collection name (same rules as database names).</param>
/// <param name="Id">Document id: a lowercase hyphenated UUID, or an existing schema-1 legacy id.</param>
public sealed record DocumentKey(DatabaseName Database, string Collection, string Id);

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
public sealed record OperationResult(string? Id, long? Version, bool Created, long? Count);

/// <summary>Result of an atomic write.</summary>
/// <param name="Offset">Database commit offset.</param>
/// <param name="Results">One result per operation.</param>
/// <param name="Incarnation">Persisted identity of the database that committed this write.</param>
public sealed record WriteResult(long Offset, IReadOnlyList<OperationResult> Results, Guid Incarnation);

/// <summary>
/// JSON document operations for the client API, over <see cref="IStorageEngine"/>. Failures are
/// <see cref="Hosting.ApiException"/>s with Documents API v1 codes.
/// </summary>
public interface IDocumentService
{
    /// <summary>Creates a database; <see langword="false"/> when it already existed.</summary>
    ValueTask<bool> CreateDatabaseAsync(DatabaseName database, CancellationToken cancellationToken);

    /// <summary>Creates a collection; fails with <c>name_taken</c> when it exists.</summary>
    ValueTask<CollectionInfo> CreateCollectionAsync(DatabaseName database, string name, CancellationToken cancellationToken);

    /// <summary>Reads a collection.</summary>
    ValueTask<CollectionInfo> GetCollectionAsync(DatabaseName database, string name, CancellationToken cancellationToken);

    /// <summary>Lists collections in name order after <paramref name="afterName"/>.</summary>
    ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(
        DatabaseName database, string? afterName, int limit, CancellationToken cancellationToken);

    /// <summary>Drops a collection and every document in it.</summary>
    ValueTask<CollectionInfo> DropCollectionAsync(DatabaseName database, string name, CancellationToken cancellationToken);

    /// <summary>Reads a document or its tombstone; <see langword="null"/> when it was never written.</summary>
    ValueTask<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a query and, when <paramref name="countLimit"/> is set, counts matches among at most that many
    /// documents (<c>Complete</c> is <see langword="false"/> when more exist).
    /// </summary>
    ValueTask<(QueryPage Page, (long Count, bool Complete)? Count)> QueryAsync(
        DatabaseName database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken);

    /// <summary>
    /// Applies <paramref name="operations"/> atomically: assertions first, against the state before
    /// any write, then writes in order. With <paramref name="indexErrors"/>, a failure names its operation.
    /// </summary>
    ValueTask<WriteResult> WriteAsync(
        DatabaseName database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken);
}
