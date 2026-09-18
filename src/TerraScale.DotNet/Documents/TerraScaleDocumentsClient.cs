using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet.Generated;

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
        ValidateReadOptions(options);
        var route = DocumentRoute(database, collection, documentId).RequestUri;
        if (options?.At is { } at) route += "?at=" + at.ToString(CultureInfo.InvariantCulture);
        return client.SendAsync<JsonElement, TerraScaleDocument>(HttpMethod.Get, route, body: null, preferredRegion: null,
            configureRequest: request => ConfigureRead(request, options, database, documentId),
            (value, response) => client.ObserveDocumentRead(ReadDocument(value, response, database, collection, documentId), response), cancellationToken);
    }

    public Task<TerraScaleDocument> UpsertAsync<TDocument>(string database, string collection, string documentId, TDocument document, CancellationToken cancellationToken = default) => UpsertAsync(database, collection, documentId, document, options: null, cancellationToken);
    public Task<TerraScaleDocument> UpsertAsync<TDocument>(string database, string collection, string documentId, TDocument document, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Put, database, collection, documentId, document, options, cancellationToken);
    public Task<TerraScaleDocument> PutAsync<TDocument>(string database, string collection, string documentId, TDocument document, TerraScaleDocumentMutationOptions? options = null, CancellationToken cancellationToken = default) => UpsertAsync(database, collection, documentId, document, options, cancellationToken);

    public Task<TerraScaleDocument> PatchAsync<TPatch>(string database, string collection, string documentId, TPatch patch, CancellationToken cancellationToken = default) => PatchAsync(database, collection, documentId, patch, options: null, cancellationToken);
    public Task<TerraScaleDocument> PatchAsync<TPatch>(string database, string collection, string documentId, TPatch patch, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Patch, database, collection, documentId, patch, options, cancellationToken);

    public Task<TerraScaleDocumentBatch> BatchAsync(string database, string collection, IReadOnlyList<TerraScaleDocumentBatchOperation> operations, CancellationToken cancellationToken = default) => BatchAsync(database, collection, operations, options: null, cancellationToken);

    /// <summary>
    /// Commits one atomic batch of document mutations in a single engine
    /// transaction: every operation commits together or the typed refusal
    /// names the offending operation and nothing lands.
    /// </summary>
    public Task<TerraScaleDocumentBatch> BatchAsync(string database, string collection, IReadOnlyList<TerraScaleDocumentBatchOperation> operations, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(operations), "A batch must contain between 1 and 1000 operations.");
        ValidateBatchOptions(options);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new TerraScaleBatchMutation([.. operations.Select(operation => operation.ToWire())]), TerraScaleDocumentJson.Options);
        return client.SendPreparedJsonAsync<TerraScaleBatchResponse, TerraScaleDocumentBatch>(
            HttpMethod.Post,
            CollectionRoute(database, collection) + "/batch",
            payload,
            request => ConfigureBatchMutation(request, options),
            (value, response) => new TerraScaleDocumentBatch(database.Trim(), collection.Trim(), value.Committed, value.Count, TerraScaleRoutingObservation.From(response)),
            cancellationToken,
            idempotencyKeyOverride: options?.IdempotencyKey?.Trim(),
            allowUnkeyedPutRetry: false);
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
        else await client.SendNoContentAsync(HttpMethod.Delete, DocumentRoute(database, collection, documentId), request => ConfigureMutation(request, options), options.IdempotencyKey?.Trim(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns one document's retained revision history, newest first. History
    /// is journal retention; conflict explanations ride the replication lane.
    /// </summary>
    public async Task<IReadOnlyList<TerraScaleDocumentHistoryEntry>> HistoryAsync(string database, string collection, string documentId, int limit = 100, string? after = null, CancellationToken cancellationToken = default)
    {
        ValidatePage(limit, after);
        var route = DocumentRoute(database, collection, documentId).RequestUri + "/history?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(after)) route += "&after=" + Uri.EscapeDataString(after.Trim());
        var response = await client.SendJsonAsync(HttpMethod.Get, route, TerraScaleContractJsonContext.Default.HistoryResponse, cancellationToken).ConfigureAwait(false);
        return [.. response.History.Select(entry => new TerraScaleDocumentHistoryEntry(entry.Revision, entry.Deleted, entry.CommitUlid, entry.Hlc, entry.Origin, entry.Policy, entry.Explanation))];
    }

    public Task<TerraScaleDocumentQueryResult> QueryAsync(string database, string collection, TerraScaleDocumentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var payload = JsonSerializer.SerializeToUtf8Bytes(query.ToWire(), TerraScaleDocumentJson.Options);
        return client.SendPreparedJsonAsync<TerraScaleDocumentQueryWireResponse, TerraScaleDocumentQueryResult>(
            HttpMethod.Post,
            CollectionRoute(database, collection) + "/query",
            payload,
            configureRequest: null,
            (value, _) => new TerraScaleDocumentQueryResult(
                database.Trim(),
                collection.Trim(),
                [.. value.Documents.Select(row => new TerraScaleDocumentQueryRow(row.Id, row.Version, row.Value.Clone()))],
                value.ReturnedCount,
                value.HasMore,
                value.NextAfter,
                value.SourceCommitSequence,
                value.Plan,
                value.Work),
            cancellationToken,
            idempotencyKeyOverride: null,
            allowUnkeyedPutRetry: true);
    }

    /// <summary>
    /// Explains how the server would execute one bounded query right now: the
    /// selected candidate with its access method, freshness, and cost, plus
    /// every rejected candidate and its rejection reason. No documents are read.
    /// </summary>
    public Task<TerraScaleDocumentExplainResult> ExplainAsync(string database, string collection, TerraScaleDocumentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var payload = JsonSerializer.SerializeToUtf8Bytes(query.ToWire(explain: true), TerraScaleDocumentJson.Options);
        return client.SendPreparedJsonAsync<TerraScaleDocumentExplainWireResponse, TerraScaleDocumentExplainResult>(
            HttpMethod.Post,
            CollectionRoute(database, collection) + "/query",
            payload,
            configureRequest: null,
            (value, _) => new TerraScaleDocumentExplainResult(
                database.Trim(),
                collection.Trim(),
                value.Explain),
            cancellationToken,
            idempotencyKeyOverride: null,
            allowUnkeyedPutRetry: true);
    }

    private Task<TerraScaleDocument> WriteAsync<TDocument>(HttpMethod method, string database, string collection, string documentId, TDocument document, TerraScaleDocumentMutationOptions? options, CancellationToken cancellationToken)
    {
        ValidateMutationOptions(options, method);
        var payload = JsonSerializer.SerializeToUtf8Bytes(document, TerraScaleDocumentJson.Options);
        if (payload.Length > MaxDocumentBytes) throw new ArgumentException("The document exceeds the 1 MiB limit.", nameof(document));
        return client.SendPreparedJsonAsync<TerraScaleDocumentWriteResponse, TerraScaleDocument>(
            method,
            DocumentRoute(database, collection, documentId).RequestUri,
            payload,
            request => ConfigureMutation(request, options),
            (value, response) => client.ObserveDocumentWrite(ReadWriteResult(value, response, database, collection, documentId), response, database),
            cancellationToken,
            idempotencyKeyOverride: options?.IdempotencyKey?.Trim(),
            allowUnkeyedPutRetry: false);
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

    private static void ConfigureMutation(HttpRequestMessage request, TerraScaleDocumentMutationOptions? options)
    {
        if (options?.CreateOnly == true) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        if (!string.IsNullOrWhiteSpace(options?.ExpectedChangeToken)) request.Headers.TryAddWithoutValidation("If-Match", StrongEtag(options.ExpectedChangeToken));
        if (!string.IsNullOrWhiteSpace(options?.Session)) request.Headers.TryAddWithoutValidation("X-TerraScale-Session", options.Session.Trim());
        if (options?.Durability is { } durability) request.Headers.TryAddWithoutValidation("X-TerraScale-Durability", durability switch { TerraScaleDurability.Regional => "regional", TerraScaleDurability.Global => "global", _ => throw new ArgumentOutOfRangeException(nameof(options)) });
    }

    private static void ConfigureBatchMutation(HttpRequestMessage request, TerraScaleDocumentMutationOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.Session)) request.Headers.TryAddWithoutValidation("X-TerraScale-Session", options.Session.Trim());
        if (options?.Durability is { } durability) request.Headers.TryAddWithoutValidation("X-TerraScale-Durability", durability switch { TerraScaleDurability.Regional => "regional", TerraScaleDurability.Global => "global", _ => throw new ArgumentOutOfRangeException(nameof(options)) });
    }

    private static void ValidateBatchOptions(TerraScaleDocumentMutationOptions? options)
    {
        if (options is null) return;
        if (options.CreateOnly || options.ExpectedChangeToken is not null)
            throw new ArgumentException("Batch operations support only IdempotencyKey, Session, and Durability.", nameof(options));
        ValidateBounded(options.IdempotencyKey, "IdempotencyKey", 128);
        ValidateBounded(options.Session, "Session", 4096);
        if (options.Durability is not (null or TerraScaleDurability.Regional or TerraScaleDurability.Global)) throw new ArgumentOutOfRangeException(nameof(options));
    }

    private void ConfigureRead(HttpRequestMessage request, TerraScaleDocumentReadOptions? options, string database, string documentId)
    {
        if (!string.IsNullOrWhiteSpace(options?.IfNoneMatch))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", options.IfNoneMatch.Trim());
        }

        client.ApplyDocumentReadHeaders(request, database, documentId, options);
    }

    private static TerraScaleDocument ReadDocument(JsonElement value, HttpResponseMessage response, string database, string collection, string documentId)
    {
        var etag = RequiredEtag(response);
        return new TerraScaleDocument(database.Trim(), collection.Trim(), documentId.Trim(), value.Clone(), null, Unquote(etag), SessionToken(response));
    }

    private static TerraScaleDocument ReadWriteResult(TerraScaleDocumentWriteResponse value, HttpResponseMessage response, string database, string collection, string documentId)
    {
        var etag = RequiredEtag(response);
        if (!string.Equals(value.Revision, Unquote(etag), StringComparison.Ordinal)) throw new JsonException("TerraScale document revision must match the strong ETag.");
        return new TerraScaleDocument(database.Trim(), collection.Trim(), documentId.Trim(), default, null, value.Revision, SessionToken(response));
    }

    private static string? SessionToken(HttpResponseMessage response) => response.Headers.TryGetValues("X-TerraScale-Session", out var values) ? values.FirstOrDefault()?.Trim() : null;

    private static TerraScaleDocumentPage ToPage(TerraScaleDocumentsResponse value, HttpResponseMessage response, string database, string collection) => new(database.Trim(), collection.Trim(), 0, value.Documents.Count, value.Documents.Count, value.Documents.Select(item => new TerraScaleDocument(database.Trim(), collection.Trim(), item.Id, item.Value.Clone(), null, item.Version.ToString(CultureInfo.InvariantCulture))).ToArray(), TerraScaleRoutingObservation.From(response));
    private static string RequiredEtag(HttpResponseMessage response) { var tag = response.Headers.ETag?.Tag; if (string.IsNullOrWhiteSpace(tag) || response.Headers.ETag!.IsWeak) throw new JsonException("TerraScale document responses require a strong ETag header."); return tag; }
    private static void ValidateMutationOptions(TerraScaleDocumentMutationOptions? options, HttpMethod? method = null) { if (options is null) return; if (options.CreateOnly && (method != HttpMethod.Put || options.ExpectedChangeToken is not null)) throw new ArgumentException("CreateOnly is supported only for PUT and cannot be combined with ExpectedChangeToken.", nameof(options)); ValidateBounded(options.ExpectedChangeToken, "ExpectedChangeToken", 4096); ValidateBounded(options.IdempotencyKey, "IdempotencyKey", 128); ValidateBounded(options.Session, "Session", 4096); if (options.Durability is not (null or TerraScaleDurability.Regional or TerraScaleDurability.Global)) throw new ArgumentOutOfRangeException(nameof(options)); }
    private static void ValidateReadOptions(TerraScaleDocumentReadOptions? options) { ValidateBounded(options?.Session, "Session", 4096); ValidateBounded(options?.IfNoneMatch, "IfNoneMatch", 4096); if (options?.At is < 0) throw new ArgumentOutOfRangeException(nameof(options), "At must be non-negative."); }
    private static void ValidatePage(int limit, string? after) { if (limit is <= 0 or > MaxPageSize) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000."); ValidateBounded(after, "after", 255); }
    private static void ValidateIdentifier(string value, string name) { ArgumentException.ThrowIfNullOrWhiteSpace(value, name); if (Encoding.UTF8.GetByteCount(value.Trim()) > 255 || value.Any(char.IsControl)) throw new ArgumentException($"{name} must be printable and at most 255 UTF-8 bytes.", name); }
    internal static void ValidateBounded(string? value, string name, int maxBytes) { if (value is not null && (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value.Trim()) > maxBytes)) throw new ArgumentException($"{name} must be at most {maxBytes} UTF-8 bytes.", name); }
    private static string Escape(string value) => Uri.EscapeDataString(value.Trim());
    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    private static string StrongEtag(string value)
    {
        var token = value.Trim();
        return token == "*" || token.Length >= 2 && token[0] == '"' && token[^1] == '"' ? token : $"\"{token}\"";
    }
}

internal sealed record TerraScaleDocumentWriteResponse([property: JsonRequired] string Revision);
internal sealed record TerraScaleDocumentQueryWireRequest(
    [property: JsonPropertyName("predicates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TerraScaleDocumentPredicateWire>? Predicates,
    [property: JsonPropertyName("projection"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Projection,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("after"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? After,
    [property: JsonPropertyName("explain"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Explain = false);
internal sealed record TerraScaleDocumentExplainWireResponse(
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("explain")] TerraScaleDocumentExplainPlan Explain);
internal sealed record TerraScaleDocumentExplainCandidateWire(
    [property: JsonPropertyName("indexName")] string? IndexName,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("access")] string? Access,
    [property: JsonPropertyName("isCovered")] bool IsCovered,
    [property: JsonPropertyName("isFresh")] bool IsFresh,
    [property: JsonPropertyName("indexCommitSequence")] ulong IndexCommitSequence,
    [property: JsonPropertyName("indexObservedAt")] DateTimeOffset IndexObservedAt,
    [property: JsonPropertyName("estimatedMatches")] int EstimatedMatches,
    [property: JsonPropertyName("indexCost")] double IndexCost,
    [property: JsonPropertyName("sourceScanCost")] double SourceScanCost,
    [property: JsonPropertyName("totalCost")] double TotalCost,
    [property: JsonPropertyName("usesSnapshotFallback")] bool UsesSnapshotFallback,
    [property: JsonPropertyName("reason")] string? Reason);
internal sealed record TerraScaleDocumentPredicateWire(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("operator")] string Operator,
    [property: JsonPropertyName("value"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Value);
internal sealed record TerraScaleDocumentQueryWireResponse(
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("documents")] IReadOnlyList<TerraScaleDocumentQueryRowWire> Documents,
    [property: JsonPropertyName("returnedCount")] int ReturnedCount,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("nextAfter")] string? NextAfter,
    [property: JsonPropertyName("sourceCommitSequence")] ulong SourceCommitSequence,
    [property: JsonPropertyName("plan")] TerraScaleDocumentQueryPlan Plan,
    [property: JsonPropertyName("work")] TerraScaleDocumentQueryWork Work);
internal sealed record TerraScaleDocumentQueryRowWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] ulong Version,
    [property: JsonPropertyName("value")] JsonElement Value);
internal sealed record TerraScaleDocumentsResponse([property: JsonRequired] IReadOnlyList<TerraScaleDocumentWire> Documents);
internal sealed record TerraScaleDocumentWire([property: JsonRequired] string Id, [property: JsonRequired] ulong Version, [property: JsonRequired] JsonElement Value);
internal sealed record TerraScaleBatchMutation([property: JsonPropertyName("operations")] IReadOnlyList<TerraScaleDocumentBatchOperationWire> Operations);
internal sealed record TerraScaleBatchResponse([property: JsonRequired] long Committed, [property: JsonRequired] int Count);
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
/// <summary>The durable outcome of one committed atomic document batch.</summary>
public sealed record TerraScaleDocumentBatch(string DatabaseId, string Collection, long Committed, int Count, TerraScaleRoutingObservation? Routing = null);
public sealed record TerraScaleDocumentHistoryEntry(
    [property: JsonPropertyName("revision"), JsonRequired] string Revision,
    [property: JsonPropertyName("deleted"), JsonRequired] bool Deleted,
    [property: JsonPropertyName("commitUlid"), JsonRequired] string CommitUlid,
    [property: JsonPropertyName("hlc"), JsonRequired] long Hlc,
    [property: JsonPropertyName("origin"), JsonRequired] string Origin,
    [property: JsonPropertyName("policy"), JsonRequired] string Policy,
    [property: JsonPropertyName("explanation"), JsonRequired] string Explanation);
public sealed record TerraScaleDocumentReadOptions(string? IfNoneMatch = null, string? Session = null, long? At = null);
public sealed record TerraScaleDocumentMutationOptions(string? ExpectedChangeToken = null, string? IdempotencyKey = null, TerraScaleDurability? Durability = null, bool CreateOnly = false, string? Session = null);
public enum TerraScaleDurability { Regional, Global }

/// <summary>A bounded predicate query over one collection.</summary>
/// <param name="Predicates">At most 32 typed predicates; omitted means every document in the collection.</param>
/// <param name="Projection">At most 64 distinct field paths returned per matching document; omitted returns whole documents.</param>
/// <param name="Limit">Maximum documents returned per page, between 1 and 1000.</param>
/// <param name="After">Opaque continuation from <see cref="TerraScaleDocumentQueryResult.NextAfter"/>.</param>
public sealed record TerraScaleDocumentQuery(
    IReadOnlyList<TerraScaleDocumentPredicate>? Predicates = null,
    IReadOnlyList<string>? Projection = null,
    int Limit = 100,
    string? After = null)
{
    internal TerraScaleDocumentQueryWireRequest ToWire(bool explain = false)
    {
        if (Limit is <= 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(Limit), "Limit must be between 1 and 1000.");
        TerraScaleDocumentsClient.ValidateBounded(After, "after", 255);
        TerraScaleDocumentPredicateWire[]? predicates = null;
        if (Predicates is { Count: > 0 })
        {
            if (Predicates.Count > 32) throw new ArgumentException("A query can contain at most 32 predicates.", nameof(Predicates));
            predicates = new TerraScaleDocumentPredicateWire[Predicates.Count];
            for (var index = 0; index < Predicates.Count; index++)
            {
                predicates[index] = Predicates[index].ToWire();
            }
        }

        if (Projection is { Count: > 64 }) throw new ArgumentException("A query can contain at most 64 projection paths.", nameof(Projection));
        foreach (var path in Projection ?? [])
        {
            TerraScaleDocumentsClient.ValidateBounded(path, "projection", 255);
        }

        return new TerraScaleDocumentQueryWireRequest(predicates, Projection, Limit, After?.Trim(), explain);
    }
}

/// <summary>One typed predicate. <see cref="TerraScaleQueryOperator.Exists"/> must omit <paramref name="Value"/>; every other operator requires it.</summary>
public sealed record TerraScaleDocumentPredicate(string Path, TerraScaleQueryOperator Operator, JsonElement? Value = null)
{
    internal TerraScaleDocumentPredicateWire ToWire()
    {
        TerraScaleDocumentsClient.ValidateBounded(Path, "path", 255);
        if (Operator == TerraScaleQueryOperator.Exists && Value is not null)
            throw new ArgumentException("exists predicates must omit value.", nameof(Value));
        if (Operator != TerraScaleQueryOperator.Exists && Value is null)
            throw new ArgumentException("This predicate operator requires value.", nameof(Value));
        return new TerraScaleDocumentPredicateWire(Path.Trim(), Operator.ToWireName(), Value);
    }
}

public enum TerraScaleQueryOperator
{
    Equal,
    NotEqual,
    Exists,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Contains,
    StartsWith,
}

internal static class TerraScaleQueryOperatorWire
{
    public static string ToWireName(this TerraScaleQueryOperator @operator) => @operator switch
    {
        TerraScaleQueryOperator.Equal => "equal",
        TerraScaleQueryOperator.NotEqual => "notEqual",
        TerraScaleQueryOperator.Exists => "exists",
        TerraScaleQueryOperator.GreaterThan => "greaterThan",
        TerraScaleQueryOperator.GreaterThanOrEqual => "greaterThanOrEqual",
        TerraScaleQueryOperator.LessThan => "lessThan",
        TerraScaleQueryOperator.LessThanOrEqual => "lessThanOrEqual",
        TerraScaleQueryOperator.Contains => "contains",
        TerraScaleQueryOperator.StartsWith => "startsWith",
        _ => throw new ArgumentOutOfRangeException(nameof(@operator)),
    };
}

/// <summary>One matching document row of a <see cref="TerraScaleDocumentQueryResult"/>.</summary>
public sealed record TerraScaleDocumentQueryRow(string Id, ulong Version, JsonElement Value);

/// <summary>The bounded query result, including the plan and work the server reported for the execution.</summary>
public sealed record TerraScaleDocumentQueryResult(
    string DatabaseId,
    string Collection,
    IReadOnlyList<TerraScaleDocumentQueryRow> Documents,
    int ReturnedCount,
    bool HasMore,
    string? NextAfter,
    ulong SourceCommitSequence,
    TerraScaleDocumentQueryPlan Plan,
    TerraScaleDocumentQueryWork Work);

/// <summary>The access plan the server selected for the query.</summary>
public sealed record TerraScaleDocumentQueryPlan(
    [property: JsonPropertyName("indexName")] string? IndexName,
    [property: JsonPropertyName("access")] string? Access,
    [property: JsonPropertyName("isCovered")] bool IsCovered,
    [property: JsonPropertyName("isFresh")] bool IsFresh,
    [property: JsonPropertyName("indexCommitSequence")] ulong IndexCommitSequence,
    [property: JsonPropertyName("indexObservedAt")] DateTimeOffset IndexObservedAt,
    [property: JsonPropertyName("usedSnapshotFallback")] bool UsedSnapshotFallback,
    [property: JsonPropertyName("estimatedMatches")] int EstimatedMatches,
    [property: JsonPropertyName("actualMatches")] int ActualMatches,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>The bounded work the server performed for the query.</summary>
public sealed record TerraScaleDocumentQueryWork(
    [property: JsonPropertyName("pagesRead")] ulong? PagesRead,
    [property: JsonPropertyName("rowsExamined")] ulong RowsExamined,
    [property: JsonPropertyName("bytesRead")] ulong BytesRead,
    [property: JsonPropertyName("workUnits")] ulong WorkUnits);

/// <summary>The plan-only result of <see cref="TerraScaleDocumentsClient.ExplainAsync"/>.</summary>
public sealed record TerraScaleDocumentExplainResult(
    string DatabaseId,
    string Collection,
    TerraScaleDocumentExplainPlan Explain);

/// <summary>The candidate the planner would select, with every considered and rejected candidate.</summary>
public sealed record TerraScaleDocumentExplainPlan(
    [property: JsonPropertyName("indexName")] string? IndexName,
    [property: JsonPropertyName("access")] string? Access,
    [property: JsonPropertyName("isCovered")] bool IsCovered,
    [property: JsonPropertyName("isFresh")] bool IsFresh,
    [property: JsonPropertyName("indexCommitSequence")] ulong IndexCommitSequence,
    [property: JsonPropertyName("indexObservedAt")] DateTimeOffset IndexObservedAt,
    [property: JsonPropertyName("usedSnapshotFallback")] bool UsedSnapshotFallback,
    [property: JsonPropertyName("estimatedMatches")] int EstimatedMatches,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("candidates")] IReadOnlyList<TerraScaleDocumentExplainCandidate>? Candidates = null,
    [property: JsonPropertyName("rejectedCandidates")] IReadOnlyList<TerraScaleDocumentExplainCandidate>? RejectedCandidates = null);

/// <summary>Explain information for one candidate the planner considered.</summary>
public sealed record TerraScaleDocumentExplainCandidate(
    [property: JsonPropertyName("indexName")] string? IndexName,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("access")] string? Access,
    [property: JsonPropertyName("isCovered")] bool IsCovered,
    [property: JsonPropertyName("isFresh")] bool IsFresh,
    [property: JsonPropertyName("indexCommitSequence")] ulong IndexCommitSequence,
    [property: JsonPropertyName("indexObservedAt")] DateTimeOffset IndexObservedAt,
    [property: JsonPropertyName("estimatedMatches")] int EstimatedMatches,
    [property: JsonPropertyName("indexCost")] double IndexCost,
    [property: JsonPropertyName("sourceScanCost")] double SourceScanCost,
    [property: JsonPropertyName("totalCost")] double TotalCost,
    [property: JsonPropertyName("usesSnapshotFallback")] bool UsesSnapshotFallback,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record TerraScaleRoutingObservation(string? EdgeRegion, string? ServingRegion, bool IsDegraded, TerraScaleRouteMode? RouteMode = null, string? FailoverFromRegion = null)
{
    internal static TerraScaleRoutingObservation? From(HttpResponseMessage response) => Create(Read(response, "X-TerraScale-Edge-Region"), Read(response, "X-TerraScale-Serving-Region") ?? Read(response, "X-TerraScale-Region"), Read(response, "X-TerraScale-Route-Mode"), Read(response, "X-TerraScale-Failover-From-Region"));
    internal static TerraScaleRoutingObservation? Create(string? edgeRegion, string? servingRegion, string? routeMode, string? failoverFromRegion) { var mode = routeMode switch { "global" => TerraScaleRouteMode.Global, "preferred" => TerraScaleRouteMode.Preferred, "preferred-failover" => TerraScaleRouteMode.PreferredFailover, "regional-strict" => TerraScaleRouteMode.RegionalStrict, _ => (TerraScaleRouteMode?)null }; return edgeRegion is null && servingRegion is null && mode is null && failoverFromRegion is null ? null : new(edgeRegion, servingRegion, mode == TerraScaleRouteMode.PreferredFailover || mode is null && edgeRegion is not null && servingRegion is not null && !string.Equals(edgeRegion, servingRegion, StringComparison.OrdinalIgnoreCase), mode, failoverFromRegion); }
    private static string? Read(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim().ToUpperInvariant() : null;
}
public enum TerraScaleRouteMode { Global, Preferred, PreferredFailover, RegionalStrict }
internal static class TerraScaleDocumentJson { internal static readonly JsonSerializerOptions Options = TerraScaleTimestampJson.CreateOptions(); }

#pragma warning restore CS1591
