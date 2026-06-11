using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// Client for repository document endpoints.
/// </summary>
public sealed class TerraScaleDocumentsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleDocumentsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Creates a document in a repository.
    /// </summary>
    public Task<TerraScaleDocument> CreateAsync<TDocument>(
        string repository,
        TDocument document,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDocument>(
            HttpMethod.Post,
            $"/api/v1/repositories/{Escape(repository)}/documents",
            new TerraScaleDocumentMutation<TDocument>(document),
            cancellationToken);

    /// <summary>
    /// Gets a document by id.
    /// </summary>
    public Task<TerraScaleDocument> GetAsync(
        string repository,
        string documentId,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDocument>(
            HttpMethod.Get,
            $"/api/v1/repositories/{Escape(repository)}/documents/{EscapeDocumentId(documentId)}",
            body: null,
            cancellationToken);

    /// <summary>
    /// Upserts a document by id.
    /// </summary>
    public Task<TerraScaleDocument> UpsertAsync<TDocument>(
        string repository,
        string documentId,
        TDocument document,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDocument>(
            HttpMethod.Put,
            $"/api/v1/repositories/{Escape(repository)}/documents/{EscapeDocumentId(documentId)}",
            new TerraScaleDocumentMutation<TDocument>(document),
            cancellationToken);

    /// <summary>
    /// Applies a partial top-level JSON object patch to a document.
    /// </summary>
    public Task<TerraScaleDocument> PatchAsync<TPatch>(
        string repository,
        string documentId,
        TPatch patch,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDocument>(
            HttpMethod.Patch,
            $"/api/v1/repositories/{Escape(repository)}/documents/{EscapeDocumentId(documentId)}",
            new TerraScaleDocumentMutation<TPatch>(patch),
            cancellationToken);

    /// <summary>
    /// Runs create, upsert, patch, and delete document operations as one batch.
    /// </summary>
    public Task<TerraScaleDocumentBatch> BatchAsync(
        string repository,
        IReadOnlyList<TerraScaleDocumentBatchOperation> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return client.SendAsync<TerraScaleDocumentBatch>(
            HttpMethod.Post,
            $"/api/v1/repositories/{Escape(repository)}/documents/batch",
            new TerraScaleDocumentBatchMutation(operations),
            cancellationToken);
    }

    /// <summary>
    /// Lists documents in a repository.
    /// </summary>
    public Task<TerraScaleDocumentPage> ListAsync(
        string repository,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDocumentPage>(
            HttpMethod.Get,
            $"/api/v1/repositories/{Escape(repository)}/documents?skip={skip}&take={take}",
            body: null,
            cancellationToken);

    /// <summary>
    /// Deletes a document by id.
    /// </summary>
    public Task<TerraScaleDeletedDocument> DeleteAsync(
        string repository,
        string documentId,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDeletedDocument>(
            HttpMethod.Delete,
            $"/api/v1/repositories/{Escape(repository)}/documents/{EscapeDocumentId(documentId)}",
            body: null,
            cancellationToken);

    private static string Escape(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value.Trim());
    }

    private static string EscapeDocumentId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return string.Join(
            '/',
            value
                .Trim()
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));
    }
}

internal sealed record TerraScaleDocumentMutation<TDocument>(TDocument Data);

internal sealed record TerraScaleDocumentBatchMutation(
    IReadOnlyList<TerraScaleDocumentBatchOperation> Operations);

/// <summary>
/// A create, upsert, patch, or delete operation in a document batch.
/// </summary>
public sealed record TerraScaleDocumentBatchOperation(
    string Operation,
    string? DocumentId,
    object? Data)
{
    /// <summary>
    /// Creates a new document with a server-generated ULID.
    /// </summary>
    public static TerraScaleDocumentBatchOperation Create<TDocument>(TDocument document) =>
        new("create", DocumentId: null, document);

    /// <summary>
    /// Upserts a document at a caller-provided ULID.
    /// </summary>
    public static TerraScaleDocumentBatchOperation Upsert<TDocument>(
        string documentId,
        TDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return new TerraScaleDocumentBatchOperation("upsert", documentId.Trim(), document);
    }

    /// <summary>
    /// Applies a partial top-level JSON object patch to an existing document.
    /// </summary>
    public static TerraScaleDocumentBatchOperation Patch<TPatch>(
        string documentId,
        TPatch patch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return new TerraScaleDocumentBatchOperation("patch", documentId.Trim(), patch);
    }

    /// <summary>
    /// Deletes an existing document.
    /// </summary>
    public static TerraScaleDocumentBatchOperation Delete(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return new TerraScaleDocumentBatchOperation("delete", documentId.Trim(), Data: null);
    }
}

/// <summary>
/// Document returned by TerraScale repository endpoints.
/// </summary>
public sealed record TerraScaleDocument(
    string Repository,
    string Id,
    JsonElement Data,
    DateTimeOffset? LastModified,
    string? ChangeToken);

/// <summary>
/// Page of TerraScale repository documents.
/// </summary>
public sealed record TerraScaleDocumentPage(
    string Repository,
    int Skip,
    int Take,
    long Total,
    IReadOnlyList<TerraScaleDocument> Items);

/// <summary>
/// Result of deleting a document.
/// </summary>
public sealed record TerraScaleDeletedDocument(
    string Repository,
    string Id,
    bool Deleted);

/// <summary>
/// Result of a TerraScale document batch operation.
/// </summary>
public sealed record TerraScaleDocumentBatch(
    string RepositoryId,
    string Repository,
    IReadOnlyList<TerraScaleDocumentBatchResult> Results);

/// <summary>
/// Result item for a single batch operation.
/// </summary>
public sealed record TerraScaleDocumentBatchResult(
    int Index,
    string Operation,
    string Id,
    TerraScaleDocument? Document,
    TerraScaleDeletedDocument? Deleted);
