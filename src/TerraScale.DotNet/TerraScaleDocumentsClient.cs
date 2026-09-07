using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>Client for the published database document endpoints.</summary>
public sealed class TerraScaleDocumentsClient
{
    private const int MaxDocumentBytes = 1 << 20;
    private const int MaxPageSize = 1000;
    private readonly TerraScaleClient client;

    internal TerraScaleDocumentsClient(TerraScaleClient client) => this.client = client;

    public Task<TerraScaleDocument> CreateAsync<TDocument>(string database, string collection, string documentId, TDocument document, CancellationToken cancellationToken = default) => UpsertAsync(database, collection, documentId, document, new TerraScaleDocumentMutationOptions(CreateOnly: true), cancellationToken);
    public Task<TerraScaleDocument> GetAsync(string database, string collection, string documentId, CancellationToken cancellationToken = default) => GetAsync(database, collection, documentId, options: null, cancellationToken);

    public Task<TerraScaleDocument> GetAsync(string database, string collection, string documentId, TerraScaleDocumentReadOptions? options, CancellationToken cancellationToken = default)
    {
        var route = DocumentRoute(database, collection, documentId).RequestUri;
        ValidateReadOptions(options);
        if (options?.At is { } at) route += "?at=" + at.ToString(CultureInfo.InvariantCulture);
        return client.SendAsync<JsonElement, TerraScaleDocument>(HttpMethod.Get, route, body: null, preferredRegion: null,
            request => { ConfigureRead(request, database, documentId, options); ApplyReadHeaders(request, database, documentId, options); },
            (value, response) => client.ObserveDocumentRead(ReadDocument(value, response, database, collection, documentId), response), cancellationToken);
    }

    public Task<TerraScaleDocument> UpsertAsync<TDocument>(string database, string collection, string documentId, TDocument document, CancellationToken cancellationToken = default) => UpsertAsync(database, collection, documentId, document, options: null, cancellationToken);
    public Task<TerraScaleDocument> UpsertAsync<TDocument>(string database, string collection, string documentId, TDocument document, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Put, database, collection, documentId, document, options, cancellationToken);
    public Task<TerraScaleDocument> PutAsync<TDocument>(string database, string collection, string documentId, TDocument document, TerraScaleDocumentMutationOptions? options = null, CancellationToken cancellationToken = default) => UpsertAsync(database, collection, documentId, document, options, cancellationToken);

    public Task<TerraScaleDocument> PatchAsync<TPatch>(string database, string collection, string documentId, TPatch patch, CancellationToken cancellationToken = default) => PatchAsync(database, collection, documentId, patch, options: null, cancellationToken);
    public Task<TerraScaleDocument> PatchAsync<TPatch>(string database, string collection, string documentId, TPatch patch, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Patch, database, collection, documentId, patch, options, cancellationToken);

    public Task<TerraScaleDocumentBatch> BatchAsync(string database, string collection, IReadOnlyList<TerraScaleDocumentBatchOperation> operations, CancellationToken cancellationToken = default) => BatchAsync(database, collection, operations, options: null, cancellationToken);
    public Task<TerraScaleDocumentBatch> BatchAsync(string database, string collection, IReadOnlyList<TerraScaleDocumentBatchOperation> operations, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ValidateMutationOptions(options);
        if (operations.Count is < 1 or > MaxPageSize) throw new ArgumentOutOfRangeException(nameof(operations), "A document batch must contain between 1 and 1000 operations.");
        return client.SendAsync<TerraScaleBatchResponse, TerraScaleDocumentBatch>(HttpMethod.Post, CollectionRoute(database, collection) + "/batch",
            new TerraScaleBatchMutation(operations.Select(static operation => operation.ToWire()).ToArray()), preferredRegion: null,
            request => ConfigureMutation(request, options),
            (value, response) => client.ObserveDocumentBatch(new TerraScaleDocumentBatch(database.Trim(), collection.Trim(), [], TerraScaleRoutingObservation.From(response), value.Committed, value.Count), response, database), cancellationToken,
            idempotencyKeyOverride: options?.IdempotencyKey?.Trim(), allowUnkeyedPutRetry: false);
    }

    public Task<TerraScaleDocumentPage> ListAsync(string database, string collection, int limit = 100, string? after = null, CancellationToken cancellationToken = default)
    {
        ValidatePage(limit, after);
        var route = CollectionRoute(database, collection) + "?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(after)) route += "&after=" + Uri.EscapeDataString(after.Trim());
        return client.SendAsync<TerraScaleDocumentsResponse, TerraScaleDocumentPage>(HttpMethod.Get, route, body: null, preferredRegion: null,
            configureRequest: null, (value, response) => client.ObserveDocumentPage(ToPage(value, response, database, collection), response), cancellationToken);
    }

    public Task DeleteAsync(string database, string collection, string documentId, CancellationToken cancellationToken = default) => DeleteAsync(database, collection, documentId, options: null, cancellationToken);
    public async Task DeleteAsync(string database, string collection, string documentId, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken = default)
    {
        ValidateMutationOptions(options);
        if (options is null) await client.SendNonRetryableNoContentAsync(HttpMethod.Delete, DocumentRoute(database, collection, documentId), cancellationToken).ConfigureAwait(false);
        else await client.SendNoContentAsync(HttpMethod.Delete, DocumentRoute(database, collection, documentId), request => ConfigureMutation(request, options), options.IdempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TerraScaleDocumentHistoryEntry>> HistoryAsync(string database, string collection, string documentId, int limit = 100, string? after = null, CancellationToken cancellationToken = default)
    {
        ValidatePage(limit, after);
        var route = DocumentRoute(database, collection, documentId).RequestUri + "/history?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(after)) route += "&after=" + Uri.EscapeDataString(after.Trim());
        return client.SendAsync<TerraScaleHistoryResponse, IReadOnlyList<TerraScaleDocumentHistoryEntry>>(HttpMethod.Get, route, body: null, preferredRegion: null, configureRequest: null, static (value, _) => value.History, cancellationToken);
    }

    private Task<TerraScaleDocument> WriteAsync<TDocument>(HttpMethod method, string database, string collection, string documentId, TDocument document, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken)
    {
        ValidateMutationOptions(options, method);
        var payload = JsonSerializer.SerializeToUtf8Bytes(document, TerraScaleDocumentJson.Options);
        if (payload.Length > MaxDocumentBytes) throw new ArgumentException("The document exceeds the 1 MiB limit.", nameof(document));
        return client.SendAsync<TerraScaleDocumentWriteResponse, TerraScaleDocument>(method, DocumentRoute(database, collection, documentId).RequestUri, JsonSerializer.SerializeToElement(document, TerraScaleDocumentJson.Options), preferredRegion: null,
            request => ConfigureMutation(request, options), (value, response) => client.ObserveDocumentWrite(ReadWriteResult(value, response, database, collection, documentId), response, database), cancellationToken,
            idempotencyKeyOverride: options?.IdempotencyKey?.Trim(), allowUnkeyedPutRetry: false);
    }

    private static TerraScaleBoundRoute DocumentRoute(string database, string collection, string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return new TerraScaleBoundRoute(CollectionRoute(database, collection) + "/" + Escape(documentId));
    }

    private static string CollectionRoute(string database, string collection)
    {
        ValidateIdentifier(database, nameof(database));
        ValidateIdentifier(collection, nameof(collection));
        return $"/api/v1/databases/{Escape(database)}/collections/{Escape(collection)}/documents";
    }

    private static void ConfigureRead(HttpRequestMessage request, string database, string documentId, TerraScaleDocumentReadOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.Session)) request.Headers.TryAddWithoutValidation("X-TerraScale-Session", options.Session.Trim());
        if (!string.IsNullOrWhiteSpace(options?.IfNoneMatch)) request.Headers.TryAddWithoutValidation("If-None-Match", options.IfNoneMatch.Trim());
        _ = database;
        // SessionConsistency=Automatic is handled by the shared client, which
        // retains opaque write tokens per database/document scope.
    }

    private void ApplyReadHeaders(HttpRequestMessage request, string database, string documentId, TerraScaleDocumentReadOptions? options) => client.ApplyDocumentReadHeaders(request, database, documentId, options);

    private static void ConfigureMutation(HttpRequestMessage request, TerraScaleDocumentMutationOptions? options)
    {
        if (options?.CreateOnly == true) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        if (!string.IsNullOrWhiteSpace(options?.ExpectedChangeToken)) request.Headers.TryAddWithoutValidation("If-Match", options.ExpectedChangeToken.Trim());
        if (options?.Durability is { } durability) request.Headers.TryAddWithoutValidation("X-TerraScale-Durability", durability switch { TerraScaleDurability.Regional => "regional", TerraScaleDurability.Global => "global", _ => throw new ArgumentOutOfRangeException(nameof(options)) });
    }

    private static TerraScaleDocument ReadDocument(JsonElement value, HttpResponseMessage response, string database, string collection, string documentId)
    {
        var etag = RequiredEtag(response);
        return new TerraScaleDocument(database.Trim(), collection.Trim(), documentId.Trim(), value.Clone(), null, Unquote(etag), ReadSession(response));
    }

    private static TerraScaleDocument ReadWriteResult(TerraScaleDocumentWriteResponse value, HttpResponseMessage response, string database, string collection, string documentId)
    {
        var etag = RequiredEtag(response);
        if (!string.Equals(value.Revision, Unquote(etag), StringComparison.Ordinal)) throw new JsonException("TerraScale document revision must match the strong ETag.");
        return new TerraScaleDocument(database.Trim(), collection.Trim(), documentId.Trim(), default, null, value.Revision, ReadSession(response));
    }

    private static TerraScaleDocumentPage ToPage(TerraScaleDocumentsResponse value, HttpResponseMessage response, string database, string collection) => new(database.Trim(), collection.Trim(), 0, value.Documents.Count, value.Documents.Count, value.Documents.Select(item => new TerraScaleDocument(database.Trim(), collection.Trim(), item.Id, item.Value.Clone(), null, item.Version.ToString(CultureInfo.InvariantCulture))).ToArray(), TerraScaleRoutingObservation.From(response));
    private static string RequiredEtag(HttpResponseMessage response) { var tag = response.Headers.ETag?.Tag; if (string.IsNullOrWhiteSpace(tag) || response.Headers.ETag!.IsWeak) throw new JsonException("TerraScale document responses require a strong ETag header."); return tag; }
    private static string? ReadSession(HttpResponseMessage response) => response.Headers.TryGetValues("X-TerraScale-Session", out var values) ? values.FirstOrDefault()?.Trim() : null;
    private static void ValidateMutationOptions(TerraScaleDocumentMutationOptions? options, HttpMethod? method = null) { if (options is null) return; if (options.CreateOnly && (method != HttpMethod.Put || options.ExpectedChangeToken is not null)) throw new ArgumentException("CreateOnly is supported only for PUT and cannot be combined with ExpectedChangeToken.", nameof(options)); ValidateBounded(options.ExpectedChangeToken, "ExpectedChangeToken", 4096); ValidateBounded(options.IdempotencyKey, "IdempotencyKey", 128); if (options.Durability is not (null or TerraScaleDurability.Regional or TerraScaleDurability.Global)) throw new ArgumentOutOfRangeException(nameof(options)); }
    private static void ValidateReadOptions(TerraScaleDocumentReadOptions? options) { ValidateBounded(options?.Session, "Session", 4096); ValidateBounded(options?.IfNoneMatch, "IfNoneMatch", 4096); if (options?.At is < 0) throw new ArgumentOutOfRangeException(nameof(options), "At must be non-negative."); }
    private static void ValidatePage(int limit, string? after) { if (limit is <= 0 or > MaxPageSize) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000."); ValidateBounded(after, "after", 255); }
    private static void ValidateIdentifier(string value, string name) { ArgumentException.ThrowIfNullOrWhiteSpace(value, name); if (Encoding.UTF8.GetByteCount(value.Trim()) > 255 || value.Any(char.IsControl)) throw new ArgumentException($"{name} must be printable and at most 255 UTF-8 bytes.", name); }
    private static void ValidateBounded(string? value, string name, int maxBytes) { if (value is not null && (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value.Trim()) > maxBytes)) throw new ArgumentException($"{name} must be at most {maxBytes} UTF-8 bytes.", name); }
    private static string Escape(string value) => Uri.EscapeDataString(value.Trim());
    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    private static Task<T> Gap<T>(string operation, CancellationToken cancellationToken) => cancellationToken.IsCancellationRequested ? Task.FromCanceled<T>(cancellationToken) : Task.FromException<T>(new TerraScaleContractGapException(operation));
}

internal sealed record TerraScaleDocumentWriteResponse([property: JsonRequired] string Revision);
internal sealed record TerraScaleDocumentsResponse([property: JsonRequired] IReadOnlyList<TerraScaleDocumentWire> Documents);
internal sealed record TerraScaleDocumentWire([property: JsonRequired] string Id, [property: JsonRequired] long Version, [property: JsonRequired] JsonElement Value);
internal sealed record TerraScaleBatchMutation([property: JsonPropertyName("operations")] IReadOnlyList<TerraScaleDocumentBatchOperationWire> Operations);
internal sealed record TerraScaleBatchResponse([property: JsonRequired] long Committed, [property: JsonRequired] int Count);
internal sealed record TerraScaleHistoryResponse([property: JsonRequired] IReadOnlyList<TerraScaleDocumentHistoryEntry> History);
internal sealed record TerraScaleDocumentBatchOperationWire([property: JsonPropertyName("op")] string Operation, [property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("value")] JsonElement? Value);
internal sealed record TerraScaleDocumentEmptyResponse;

public sealed record TerraScaleDocumentBatchOperation(string Operation, string DocumentId, JsonElement? Data)
{
    public static TerraScaleDocumentBatchOperation Create<TDocument>(string documentId, TDocument document) => new("put", RequiredId(documentId), JsonSerializer.SerializeToElement(document, TerraScaleDocumentJson.Options));
    public static TerraScaleDocumentBatchOperation Upsert<TDocument>(string documentId, TDocument document) => Create(documentId, document);
    public static TerraScaleDocumentBatchOperation Patch<TPatch>(string documentId, TPatch patch) => new("patch", RequiredId(documentId), JsonSerializer.SerializeToElement(patch, TerraScaleDocumentJson.Options));
    public static TerraScaleDocumentBatchOperation Delete(string documentId) => new("delete", RequiredId(documentId), null);
    internal TerraScaleDocumentBatchOperationWire ToWire() { if (Operation is not ("put" or "patch" or "delete")) throw new ArgumentException("Batch operation must be put, patch, or delete.", nameof(Operation)); if (string.IsNullOrWhiteSpace(DocumentId)) throw new ArgumentException("Batch operation id cannot be blank.", nameof(DocumentId)); if (Operation is not "delete" && Data is null) throw new ArgumentException("Put and patch operations require a value.", nameof(Data)); return new(Operation, DocumentId.Trim(), Data); }
    private static string RequiredId(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value.Trim(); }
}

public sealed record TerraScaleDocument(string DatabaseId, string Collection, string Id, JsonElement Data, DateTimeOffset? LastModified, string? ChangeToken, string? SessionConsistencyToken = null, TerraScaleRoutingObservation? Routing = null)
{
    public JsonElement Value => Data;
}
public sealed record TerraScaleDocumentPage(string DatabaseId, string Collection, int Skip, int Take, long Total, IReadOnlyList<TerraScaleDocument> Items, TerraScaleRoutingObservation? Routing = null);
public sealed record TerraScaleDeletedDocument(string DatabaseId, string Collection, string Id, bool Deleted, string? SessionConsistencyToken = null, TerraScaleRoutingObservation? Routing = null);
public sealed record TerraScaleDocumentBatch(string DatabaseId, string Collection, IReadOnlyList<TerraScaleDocumentBatchResult> Results, TerraScaleRoutingObservation? Routing = null, long Committed = 0, int Count = 0);
public sealed record TerraScaleDocumentBatchResult(int Index, string Operation, string Id, TerraScaleDocument? Document, TerraScaleDeletedDocument? Deleted);
public sealed record TerraScaleDocumentHistoryEntry([property: JsonRequired] string Revision, [property: JsonRequired] bool Deleted);
public sealed record TerraScaleDocumentReadOptions(bool AllowStale = false, string? IfNoneMatch = null, string? Session = null, long? At = null);
public sealed record TerraScaleDocumentMutationOptions(string? ExpectedChangeToken = null, string? IdempotencyKey = null, TerraScaleDurability? Durability = null, bool CreateOnly = false);
public enum TerraScaleDurability { Regional, Global }

public sealed record TerraScaleRoutingObservation(string? EdgeRegion, string? ServingRegion, bool IsDegraded, TerraScaleRouteMode? RouteMode = null, string? FailoverFromRegion = null)
{
    internal static TerraScaleRoutingObservation? From(HttpResponseMessage response) => Create(Read(response, "X-TerraScale-Edge-Region"), Read(response, "X-TerraScale-Serving-Region") ?? Read(response, "X-TerraScale-Region"), Read(response, "X-TerraScale-Route-Mode"), Read(response, "X-TerraScale-Failover-From-Region"));
    internal static TerraScaleRoutingObservation? Create(string? edgeRegion, string? servingRegion, string? routeMode, string? failoverFromRegion) { var mode = routeMode switch { "global" => TerraScaleRouteMode.Global, "preferred" => TerraScaleRouteMode.Preferred, "preferred-failover" => TerraScaleRouteMode.PreferredFailover, "regional-strict" => TerraScaleRouteMode.RegionalStrict, _ => (TerraScaleRouteMode?)null }; return edgeRegion is null && servingRegion is null && mode is null && failoverFromRegion is null ? null : new(edgeRegion, servingRegion, mode == TerraScaleRouteMode.PreferredFailover || mode is null && edgeRegion is not null && servingRegion is not null && !string.Equals(edgeRegion, servingRegion, StringComparison.OrdinalIgnoreCase), mode, failoverFromRegion); }
    private static string? Read(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim().ToUpperInvariant() : null;
}
public enum TerraScaleRouteMode { Global, Preferred, PreferredFailover, RegionalStrict }
internal static class TerraScaleDocumentJson { internal static readonly JsonSerializerOptions Options = TerraScaleTimestampJson.CreateOptions(); }

#pragma warning restore CS1591
