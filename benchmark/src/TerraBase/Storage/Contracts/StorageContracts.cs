using System.Text.Json;

namespace TerraBase;

/// <summary>The result of a document write or delete.</summary>
public enum WriteOutcome
{
    /// <summary>A new document was created.</summary>
    Created,

    /// <summary>An existing document was replaced.</summary>
    Updated,

    /// <summary>An existing document was deleted.</summary>
    Deleted,

    /// <summary>The expected version did not match.</summary>
    Conflict,

    /// <summary>The requested document did not exist.</summary>
    NotFound,
}

/// <summary>Controls the version precondition on one mutation.</summary>
public readonly record struct DocumentPrecondition
{
    private DocumentPrecondition(ulong? expectedVersion, bool mustExist)
    {
        ExpectedVersion = expectedVersion;
        MustExist = mustExist;
    }

    /// <summary>Accepts any current version, including an absent document.</summary>
    public static DocumentPrecondition Any { get; } = new(null, false);

    /// <summary>Requires that no document currently exists.</summary>
    public static DocumentPrecondition Missing { get; } = new(null, false) { MustBeMissing = true };

    /// <summary>Requires the supplied current version.</summary>
    /// <param name="version">The expected version.</param>
    /// <returns>A version precondition.</returns>
    public static DocumentPrecondition Version(ulong version) => new(version, true);

    /// <summary>The expected version, when this is a version precondition.</summary>
    public ulong? ExpectedVersion { get; }

    /// <summary>Whether an existing document is required.</summary>
    public bool MustExist { get; }

    /// <summary>Whether an absent document is required.</summary>
    public bool MustBeMissing { get; init; }

    internal bool Matches(Document? current) =>
        (MustBeMissing && current is null)
        || (!MustBeMissing && !MustExist)
        || (MustExist && current is not null && current.Version == ExpectedVersion);
}

/// <summary>An immutable document returned by a storage provider.</summary>
public sealed record Document
{
    /// <summary>Creates a document snapshot.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="body">The JSON body.</param>
    /// <param name="version">The monotonic document version.</param>
    /// <param name="updatedAt">The update timestamp.</param>
    public Document(DocumentKey key, JsonElement body, ulong version, DateTimeOffset updatedAt)
    {
        if (body.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A document body must be valid JSON.", nameof(body));
        }

        if (version == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Document versions start at one.");
        }

        Key = key;
        Body = body.Clone();
        Version = version;
        UpdatedAt = updatedAt;
    }

    /// <summary>The document key.</summary>
    public DocumentKey Key { get; }

    /// <summary>The immutable JSON body.</summary>
    public JsonElement Body { get; }

    /// <summary>The monotonic version assigned by the provider.</summary>
    public ulong Version { get; }

    /// <summary>The provider timestamp for the latest write.</summary>
    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>The typed result of one storage mutation.</summary>
public sealed record StorageWriteResult(WriteOutcome Outcome, Document? Document)
{
    /// <summary>Creates a conflict result.</summary>
    public static StorageWriteResult Conflict { get; } = new(WriteOutcome.Conflict, null);

    /// <summary>Creates a not-found result.</summary>
    public static StorageWriteResult NotFound { get; } = new(WriteOutcome.NotFound, null);
}

/// <summary>
/// The storage port used by the engine. Implementations can use SQLite,
/// ZoneTree, or another durable backend without changing document operations.
/// </summary>
public interface IStorageProvider : IAsyncDisposable
{
    /// <summary>Reads one document snapshot.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The document, or <see langword="null"/> when absent.</returns>
    ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces one document.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="body">The JSON body.</param>
    /// <param name="precondition">The version precondition.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The typed mutation result.</returns>
    ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one document.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="precondition">The version precondition.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The typed mutation result.</returns>
    ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition,
        CancellationToken cancellationToken = default);

    /// <summary>Scans one tenant collection in deterministic key order.</summary>
    /// <param name="tenant">The owning tenant.</param>
    /// <param name="collection">The collection to scan.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The matching document snapshots.</returns>
    IAsyncEnumerable<Document> ScanAsync(
        TenantId tenant,
        CollectionId collection,
        CancellationToken cancellationToken = default);
}

/// <summary>The document operation abstraction exposed by the engine.</summary>
public interface IDocumentStore
{
    /// <summary>Reads one document.</summary>
    ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces one document.</summary>
    ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one document.</summary>
    ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default);

    /// <summary>Scans one tenant collection.</summary>
    IAsyncEnumerable<Document> ScanAsync(
        TenantId tenant,
        CollectionId collection,
        CancellationToken cancellationToken = default);
}
