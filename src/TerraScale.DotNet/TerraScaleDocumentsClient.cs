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

/// <summary>
/// Document returned by TerraScale repository endpoints.
/// </summary>
public sealed record TerraScaleDocument(
    string Repository,
    string Id,
    JsonElement Data,
    DateTimeOffset? LastModified,
    string? ChangeVector);

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
