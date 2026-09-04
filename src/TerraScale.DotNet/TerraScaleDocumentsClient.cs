using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>Client for the published database document endpoints.</summary>
public sealed class TerraScaleDocumentsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleDocumentsClient(TerraScaleClient client) => this.client = client;

    /// <summary>Creates or replaces a document at the supplied identifier.</summary>
    public Task<TerraScaleDocument> CreateAsync<TDocument>(string database, string collection, string documentId, TDocument document, CancellationToken cancellationToken = default) =>
        UpsertAsync(database, collection, documentId, document, cancellationToken);

    /// <summary>Gets a document and its strong revision ETag.</summary>
    public Task<TerraScaleDocument> GetAsync(string database, string collection, string documentId, CancellationToken cancellationToken = default) =>
        GetAsync(database, collection, documentId, options: null, cancellationToken);

    /// <summary>Repository-shaped document routes were removed from the public contract.</summary>
    public Task<TerraScaleDocument> GetAsync(string repository, string documentId, CancellationToken cancellationToken = default) =>
        Task.FromException<TerraScaleDocument>(new TerraScaleContractGapException("documents.get.repository"));

    /// <summary>Gets a document with optional ETag and session preconditions.</summary>
    public Task<TerraScaleDocument> GetAsync(string database, string collection, string documentId, TerraScaleDocumentReadOptions? options, CancellationToken cancellationToken = default) =>
        client.SendAsync<JsonElement, TerraScaleDocument>(
            HttpMethod.Get,
            DocumentRoute(database, collection, documentId),
            body: null,
            preferredRegion: null,
            request => ConfigureRead(request, options),
            (value, response) => ReadDocument(value, response, database, collection, documentId),
            cancellationToken);

    /// <summary>Creates or replaces a document at the supplied identifier.</summary>
    public Task<TerraScaleDocument> UpsertAsync<TDocument>(string database, string collection, string documentId, TDocument document, CancellationToken cancellationToken = default) =>
        UpsertAsync(database, collection, documentId, document, options: null, cancellationToken);

    /// <summary>Repository-shaped document routes were removed from the public contract.</summary>
    public Task<TerraScaleDocument> UpsertAsync<TDocument>(string repository, string documentId, TDocument document, CancellationToken cancellationToken = default) =>
        Task.FromException<TerraScaleDocument>(new TerraScaleContractGapException("documents.upsert.repository"));

    /// <summary>Replaces a document, optionally requiring an exact ETag.</summary>
    public Task<TerraScaleDocument> UpsertAsync<TDocument>(string database, string collection, string documentId, TDocument document, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Put, database, collection, documentId, document, options, cancellationToken);

    /// <summary>Applies an RFC 7386 merge patch to a document.</summary>
    public Task<TerraScaleDocument> PatchAsync<TPatch>(string database, string collection, string documentId, TPatch patch, CancellationToken cancellationToken = default) =>
        PatchAsync(database, collection, documentId, patch, options: null, cancellationToken);

    /// <summary>Applies a merge patch, optionally requiring an exact ETag.</summary>
    public Task<TerraScaleDocument> PatchAsync<TPatch>(string database, string collection, string documentId, TPatch patch, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Patch, database, collection, documentId, patch, options, cancellationToken);

    /// <summary>Executes one atomic batch of put, patch, and delete operations.</summary>
    public Task<TerraScaleDocumentBatch> BatchAsync(string database, string collection, IReadOnlyList<TerraScaleDocumentBatchOperation> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(operations), "A document batch must contain between 1 and 1000 operations.");
        }

        return client.SendAsync<TerraScaleBatchResponse, TerraScaleDocumentBatch>(
            HttpMethod.Post,
            DocumentRoute(database, collection, null) with { RequestUri = DocumentRoute(database, collection, null).RequestUri + "/batch" },
            new TerraScaleBatchMutation(operations.Select(static operation => operation.ToWire()).ToArray()),
            preferredRegion: null,
            configureRequest: null,
            (value, response) => new TerraScaleDocumentBatch(database.Trim(), collection.Trim(), [], TerraScaleRoutingObservation.From(response), value.Committed, value.Count),
            cancellationToken);
    }

    /// <summary>Lists a collection in canonical identifier order.</summary>
    public Task<TerraScaleDocumentPage> ListAsync(string database, string collection, int limit = 100, string? after = null, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        }

        var route = DocumentRoute(database, collection, null).RequestUri + "?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(after))
        {
            route += "&after=" + Uri.EscapeDataString(after.Trim());
        }

        return client.SendAsync<TerraScaleDocumentsResponse, TerraScaleDocumentPage>(
            HttpMethod.Get,
            route,
            body: null,
            preferredRegion: null,
            configureRequest: null,
            (value, response) => ToPage(value, response, database, collection),
            cancellationToken);
    }

    /// <summary>Deletes a document. The published endpoint returns HTTP 204.</summary>
    public async Task DeleteAsync(string database, string collection, string documentId, CancellationToken cancellationToken = default)
    {
        await client.SendNonRetryableNoContentAsync(HttpMethod.Delete, DocumentRoute(database, collection, documentId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists retained revisions for a document, newest first.</summary>
    public Task<IReadOnlyList<TerraScaleDocumentHistoryEntry>> HistoryAsync(string database, string collection, string documentId, int limit = 100, string? after = null, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        }
        var route = DocumentRoute(database, collection, documentId).RequestUri + "/history?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(after))
        {
            route += "&after=" + Uri.EscapeDataString(after.Trim());
        }
        return client.SendAsync<TerraScaleHistoryResponse, IReadOnlyList<TerraScaleDocumentHistoryEntry>>(
            HttpMethod.Get,
            route,
            body: null,
            preferredRegion: null,
            configureRequest: null,
            static (value, _) => value.History,
            cancellationToken);
    }

    private Task<TerraScaleDocument> WriteAsync<TDocument>(HttpMethod method, string database, string collection, string documentId, TDocument document, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken) =>
        client.SendAsync<TerraScaleDocumentWriteResponse, TerraScaleDocument>(
            method,
            DocumentRoute(database, collection, documentId),
            document,
            preferredRegion: null,
            request => ConfigureMutation(request, options),
            (value, response) => ReadWriteResult(value, response, database, collection, documentId),
            cancellationToken);

    private static TerraScaleBoundRoute DocumentRoute(string database, string collection, string? documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        var route = $"/api/v1/databases/{Escape(database)}/collections/{Escape(collection)}/documents";
        if (documentId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
            route += "/" + Escape(documentId);
        }
        return new TerraScaleBoundRoute(route);
    }

    private static void ConfigureRead(HttpRequestMessage request, TerraScaleDocumentReadOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.IfNoneMatch))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", options.IfNoneMatch.Trim());
        }
        if (options?.Session is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Session must be non-negative.");
        }
        if (options?.Session is { } session)
        {
            request.Headers.TryAddWithoutValidation("X-TerraScale-Session", session.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void ConfigureMutation(HttpRequestMessage request, TerraScaleDocumentMutationOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.ExpectedChangeToken))
        {
            request.Headers.TryAddWithoutValidation("If-Match", options.ExpectedChangeToken.Trim());
        }
    }

    private static TerraScaleDocument ReadDocument(JsonElement value, HttpResponseMessage response, string database, string collection, string documentId)
    {
        var etag = RequiredEtag(response);
        return new TerraScaleDocument(database.Trim(), collection.Trim(), documentId.Trim(), value, null, Unquote(etag), ReadSession(response), TerraScaleRoutingObservation.From(response));
    }

    private static TerraScaleDocument ReadWriteResult(TerraScaleDocumentWriteResponse value, HttpResponseMessage response, string database, string collection, string documentId)
    {
        var etag = RequiredEtag(response);
        if (!string.Equals(value.Revision, Unquote(etag), StringComparison.Ordinal))
        {
            throw new JsonException("TerraScale document write revision must match the strong ETag.");
        }
        return new TerraScaleDocument(database.Trim(), collection.Trim(), documentId.Trim(), default, null, value.Revision, ReadSession(response), TerraScaleRoutingObservation.From(response));
    }

    private static TerraScaleDocumentPage ToPage(TerraScaleDocumentsResponse value, HttpResponseMessage response, string database, string collection) =>
        new(database.Trim(), collection.Trim(), 0, value.Documents.Count, value.Documents.Count,
            value.Documents.Select(item => new TerraScaleDocument(database.Trim(), collection.Trim(), item.Id, item.Value, null, item.Version.ToString(CultureInfo.InvariantCulture))).ToArray(),
            TerraScaleRoutingObservation.From(response));

    private static string RequiredEtag(HttpResponseMessage response)
    {
        var tag = response.Headers.ETag?.Tag;
        if (string.IsNullOrWhiteSpace(tag) || response.Headers.ETag!.IsWeak)
        {
            throw new JsonException("TerraScale document responses require a strong ETag header.");
        }
        return tag;
    }

    private static string? ReadSession(HttpResponseMessage response) => response.Headers.TryGetValues("X-TerraScale-Session", out var values) ? values.SingleOrDefault()?.Trim() : null;
    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    private static string Escape(string value) => Uri.EscapeDataString(value.Trim());
}

internal sealed record TerraScaleDocumentWriteResponse([property: JsonRequired] string Revision);
internal sealed record TerraScaleDocumentsResponse([property: JsonRequired] IReadOnlyList<TerraScaleDocumentWire> Documents) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Documents is null)
        {
            throw new JsonException("TerraScale document lists require a non-null documents field.");
        }
    }
}

internal sealed record TerraScaleDocumentWire([property: JsonRequired] string Id, [property: JsonRequired] long Version, [property: JsonRequired] JsonElement Value) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new JsonException("TerraScale document list entries require a nonblank id.");
        }
    }
}
internal sealed record TerraScaleBatchMutation(IReadOnlyList<TerraScaleDocumentBatchOperationWire> Operations);
internal sealed record TerraScaleBatchResponse([property: JsonRequired] long Committed, [property: JsonRequired] int Count);
internal sealed record TerraScaleHistoryResponse([property: JsonRequired] IReadOnlyList<TerraScaleDocumentHistoryEntry> History);
internal sealed record TerraScaleDocumentBatchOperationWire([property: JsonPropertyName("op")] string Operation, [property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("value")] JsonElement? Value);

/// <summary>A put, merge-patch, or delete batch operation.</summary>
public sealed record TerraScaleDocumentBatchOperation(string Operation, string DocumentId, JsonElement? Data)
{
    public static TerraScaleDocumentBatchOperation Create<TDocument>(string documentId, TDocument document) => new("put", RequiredId(documentId), JsonSerializer.SerializeToElement(document));
    public static TerraScaleDocumentBatchOperation Upsert<TDocument>(string documentId, TDocument document) => Create(documentId, document);
    public static TerraScaleDocumentBatchOperation Patch<TPatch>(string documentId, TPatch patch) => new("patch", RequiredId(documentId), JsonSerializer.SerializeToElement(patch));
    public static TerraScaleDocumentBatchOperation Delete(string documentId) => new("delete", RequiredId(documentId), null);
    internal TerraScaleDocumentBatchOperationWire ToWire() => new(Operation, DocumentId, Data);
    private static string RequiredId(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value.Trim(); }
}

/// <summary>Document returned by a database collection endpoint.</summary>
public sealed record TerraScaleDocument(string RepositoryId, string Repository, string Id, JsonElement Data, DateTimeOffset? LastModified, string? ChangeToken, string? SessionConsistencyToken = null, TerraScaleRoutingObservation? Routing = null);
public sealed record TerraScaleDocumentPage(string RepositoryId, string Repository, int Skip, int Take, long Total, IReadOnlyList<TerraScaleDocument> Items, TerraScaleRoutingObservation? Routing = null);
public sealed record TerraScaleDeletedDocument(string RepositoryId, string Repository, string Id, bool Deleted, string? SessionConsistencyToken = null, TerraScaleRoutingObservation? Routing = null);
public sealed record TerraScaleDocumentBatch(string RepositoryId, string Repository, IReadOnlyList<TerraScaleDocumentBatchResult> Results, TerraScaleRoutingObservation? Routing = null, long Committed = 0, int Count = 0);
public sealed record TerraScaleDocumentBatchResult(int Index, string Operation, string Id, TerraScaleDocument? Document, TerraScaleDeletedDocument? Deleted);
public sealed record TerraScaleDocumentHistoryEntry([property: JsonRequired] string Revision, [property: JsonRequired] bool Deleted);
public sealed record TerraScaleDocumentReadOptions(bool AllowStale = false, string? IfNoneMatch = null, long? Session = null);
public sealed record TerraScaleDocumentMutationOptions(string? ExpectedChangeToken = null);

/// <summary>Routing metadata observed on a TerraScale response.</summary>
public sealed record TerraScaleRoutingObservation(string? EdgeRegion, string? ServingRegion, bool IsDegraded, TerraScaleRouteMode? RouteMode = null, string? FailoverFromRegion = null)
{
    internal static TerraScaleRoutingObservation? From(HttpResponseMessage response) => Create(Read(response, "X-TerraScale-Edge-Region"), Read(response, "X-TerraScale-Serving-Region") ?? Read(response, "X-TerraScale-Region"), Read(response, "X-TerraScale-Route-Mode"), Read(response, "X-TerraScale-Failover-From-Region"));
    internal static TerraScaleRoutingObservation? Create(string? edgeRegion, string? servingRegion, string? routeMode, string? failoverFromRegion)
    {
        var mode = routeMode switch { "global" => TerraScaleRouteMode.Global, "preferred" => TerraScaleRouteMode.Preferred, "preferred-failover" => TerraScaleRouteMode.PreferredFailover, "regional-strict" => TerraScaleRouteMode.RegionalStrict, _ => (TerraScaleRouteMode?)null };
        return edgeRegion is null && servingRegion is null && mode is null && failoverFromRegion is null ? null : new(edgeRegion, servingRegion, mode == TerraScaleRouteMode.PreferredFailover || mode is null && edgeRegion is not null && servingRegion is not null && !string.Equals(edgeRegion, servingRegion, StringComparison.OrdinalIgnoreCase), mode, failoverFromRegion);
    }
    private static string? Read(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim().ToLowerInvariant() : null;
}

public enum TerraScaleRouteMode
{
    Global,
    Preferred,
    PreferredFailover,
    RegionalStrict
}

#pragma warning restore CS1591
