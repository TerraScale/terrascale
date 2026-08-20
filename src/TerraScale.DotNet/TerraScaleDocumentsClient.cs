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
        client.SendAsync<TerraScaleDocument, TerraScaleDocument>(
            HttpMethod.Post,
            $"/api/v1/repositories/{Escape(repository)}/documents",
            new TerraScaleDocumentMutation<TDocument>(document),
            preferredRegion: null,
            configureRequest: null,
            (document, response) => client.ObserveDocumentWrite(document, response, repository),
            cancellationToken);

    /// <summary>
    /// Gets a document by id.
    /// </summary>
    public Task<TerraScaleDocument> GetAsync(
        string repository,
        string documentId,
        CancellationToken cancellationToken = default) =>
        GetAsync(repository, documentId, options: null, cancellationToken);

    /// <summary>
    /// Gets a document with explicit stale-read behavior.
    /// </summary>
    public Task<TerraScaleDocument> GetAsync(
        string repository,
        string documentId,
        TerraScaleDocumentReadOptions? options,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDocument, TerraScaleDocument>(
            HttpMethod.Get,
            $"/api/v1/repositories/{Escape(repository)}/documents/{EscapeDocumentId(documentId)}",
            body: null,
            preferredRegion: null,
            request => client.ApplyDocumentReadHeaders(request, repository, documentId, options),
            client.ObserveDocumentRead,
            cancellationToken);

    /// <summary>
    /// Upserts a document by id.
    /// </summary>
    public Task<TerraScaleDocument> UpsertAsync<TDocument>(
        string repository,
        string documentId,
        TDocument document,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(repository, documentId, document, options: null, cancellationToken);

    /// <summary>
    /// Upserts a document and optionally requires the current change token.
    /// </summary>
    public Task<TerraScaleDocument> UpsertAsync<TDocument>(
        string repository,
        string documentId,
        TDocument document,
        TerraScaleDocumentMutationOptions? options,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDocument, TerraScaleDocument>(
            HttpMethod.Put,
            $"/api/v1/repositories/{Escape(repository)}/documents/{EscapeDocumentId(documentId)}",
            new TerraScaleDocumentMutation<TDocument>(document),
            preferredRegion: null,
            request => client.ApplyDocumentMutationHeaders(request, options),
            (response, message) => client.ObserveDocumentWrite(response, message, repository),
            cancellationToken);

    /// <summary>
    /// Applies a partial top-level JSON object patch to a document.
    /// </summary>
    public Task<TerraScaleDocument> PatchAsync<TPatch>(
        string repository,
        string documentId,
        TPatch patch,
        CancellationToken cancellationToken = default) =>
        PatchAsync(repository, documentId, patch, options: null, cancellationToken);

    /// <summary>
    /// Patches a document and optionally requires the current change token.
    /// </summary>
    public Task<TerraScaleDocument> PatchAsync<TPatch>(
        string repository,
        string documentId,
        TPatch patch,
        TerraScaleDocumentMutationOptions? options,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDocument, TerraScaleDocument>(
            HttpMethod.Patch,
            $"/api/v1/repositories/{Escape(repository)}/documents/{EscapeDocumentId(documentId)}",
            new TerraScaleDocumentMutation<TPatch>(patch),
            preferredRegion: null,
            request => client.ApplyDocumentMutationHeaders(request, options),
            (response, message) => client.ObserveDocumentWrite(response, message, repository),
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
        return client.SendAsync<TerraScaleDocumentBatch, TerraScaleDocumentBatch>(
            HttpMethod.Post,
            $"/api/v1/repositories/{Escape(repository)}/documents/batch",
            new TerraScaleDocumentBatchMutation(operations),
            preferredRegion: null,
            configureRequest: null,
            (batch, response) => client.ObserveDocumentBatch(batch, response, repository),
            cancellationToken);
    }

    /// <summary>
    /// Lists documents in a repository.
    /// </summary>
    public Task<TerraScaleDocumentPage> ListAsync(
        string repository,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<TerraScaleDocumentPage, TerraScaleDocumentPage>(
            HttpMethod.Get,
            $"/api/v1/repositories/{Escape(repository)}/documents?skip={skip}&take={take}",
            body: null,
            preferredRegion: null,
            configureRequest: null,
            client.ObserveDocumentPage,
            cancellationToken);
    }

    /// <summary>
    /// Deletes a document by id.
    /// </summary>
    public Task<TerraScaleDeletedDocument> DeleteAsync(
        string repository,
        string documentId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(repository, documentId, options: null, cancellationToken);

    /// <summary>
    /// Deletes a document and optionally requires the current change token.
    /// </summary>
    public Task<TerraScaleDeletedDocument> DeleteAsync(
        string repository,
        string documentId,
        TerraScaleDocumentMutationOptions? options,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDeletedDocument, TerraScaleDeletedDocument>(
            HttpMethod.Delete,
            $"/api/v1/repositories/{Escape(repository)}/documents/{EscapeDocumentId(documentId)}",
            body: null,
            preferredRegion: null,
            request => client.ApplyDocumentMutationHeaders(request, options),
            (response, message) => client.ObserveDocumentDelete(response, message, repository),
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
    string RepositoryId,
    string Repository,
    string Id,
    JsonElement Data,
    DateTimeOffset? LastModified,
    string? ChangeToken,
    string? SessionConsistencyToken = null,
    TerraScaleRoutingObservation? Routing = null);

/// <summary>
/// Page of TerraScale repository documents.
/// </summary>
public sealed record TerraScaleDocumentPage(
    string RepositoryId,
    string Repository,
    int Skip,
    int Take,
    long Total,
    IReadOnlyList<TerraScaleDocument> Items,
    TerraScaleRoutingObservation? Routing = null);

/// <summary>
/// Result of deleting a document.
/// </summary>
public sealed record TerraScaleDeletedDocument(
    string RepositoryId,
    string Repository,
    string Id,
    bool Deleted,
    string? SessionConsistencyToken = null,
    TerraScaleRoutingObservation? Routing = null);

/// <summary>
/// Result of a TerraScale document batch operation.
/// </summary>
public sealed record TerraScaleDocumentBatch(
    string RepositoryId,
    string Repository,
    IReadOnlyList<TerraScaleDocumentBatchResult> Results,
    TerraScaleRoutingObservation? Routing = null);

/// <summary>
/// Result item for a single batch operation.
/// </summary>
public sealed record TerraScaleDocumentBatchResult(
    int Index,
    string Operation,
    string Id,
    TerraScaleDocument? Document,
    TerraScaleDeletedDocument? Deleted);

/// <summary>
/// Options for a document read.
/// </summary>
public sealed record TerraScaleDocumentReadOptions(bool AllowStale = false);

/// <summary>
/// Options for a conditional document mutation.
/// </summary>
public sealed record TerraScaleDocumentMutationOptions(string? ExpectedChangeToken = null);

/// <summary>
/// Public regional routing metadata returned with a document operation.
/// </summary>
public sealed record TerraScaleRoutingObservation(
    string? EdgeRegion,
    string? ServingRegion,
    bool IsDegraded,
    TerraScaleRouteMode? RouteMode = null,
    string? FailoverFromRegion = null)
{
    internal static TerraScaleRoutingObservation? From(HttpResponseMessage response)
    {
        var edge = ReadHeader(response, "X-TerraScale-Edge-Region");
        var serving = ReadHeader(response, "X-TerraScale-Serving-Region");
        var routeMode = ReadHeader(response, "X-TerraScale-Route-Mode");
        var failoverFrom = ReadHeader(response, "X-TerraScale-Failover-From-Region");
        return Create(edge, serving, routeMode, failoverFrom);
    }

    internal static TerraScaleRoutingObservation? Create(
        string? edgeRegion,
        string? servingRegion,
        string? routeMode,
        string? failoverFromRegion)
    {
        var typedRouteMode = ParseRouteMode(routeMode);
        return edgeRegion is null &&
               servingRegion is null &&
               typedRouteMode is null &&
               failoverFromRegion is null
            ? null
            : new TerraScaleRoutingObservation(
                edgeRegion,
                servingRegion,
                typedRouteMode == TerraScaleRouteMode.PreferredFailover ||
                typedRouteMode is null &&
                edgeRegion is not null &&
                servingRegion is not null &&
                !string.Equals(edgeRegion, servingRegion, StringComparison.OrdinalIgnoreCase),
                typedRouteMode,
                failoverFromRegion);
    }

    private static string? ReadHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()?.Trim().ToLowerInvariant()
            : null;

    private static TerraScaleRouteMode? ParseRouteMode(string? value) => value switch
    {
        "global" => TerraScaleRouteMode.Global,
        "preferred" => TerraScaleRouteMode.Preferred,
        "preferred-failover" => TerraScaleRouteMode.PreferredFailover,
        "regional-strict" => TerraScaleRouteMode.RegionalStrict,
        _ => null
    };
}

/// <summary>
/// Public routing mode reported by the TerraScale API.
/// </summary>
public enum TerraScaleRouteMode
{
    /// <summary>The global endpoint selected a healthy region.</summary>
    Global,

    /// <summary>The preferred selected region served the request.</summary>
    Preferred,

    /// <summary>The preferred selected region was unavailable and another selected region served it.</summary>
    PreferredFailover,

    /// <summary>A strict regional endpoint served the request without failover.</summary>
    RegionalStrict
}
