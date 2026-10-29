using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A database as the Documents API returns it.</summary>
/// <param name="Id">Database id, a canonical uppercase ULID.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Database name, unique within the project.</param>
public sealed record TerraBaseDatabaseInfo(string Id, string ProjectId, string Name);

/// <summary>A collection. Collections are schemaless.</summary>
/// <param name="Name">Collection name: <c>^[a-z][a-z0-9_-]{0,62}$</c>.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Id">Canonical uppercase collection ULID; routes still use the name.</param>
public sealed record TerraBaseCollection(
    string Name,
    [property: JsonPropertyName("created_at"), JsonConverter(typeof(UnixMillisecondsConverter))] DateTimeOffset CreatedAt, string Id);

/// <summary>One page of collections in name order.</summary>
/// <param name="Items">Collections of this page.</param>
/// <param name="NextCursor">Cursor of the next page; <see langword="null"/> on the last page.</param>
public sealed record TerraBaseCollectionPage(
    IReadOnlyList<TerraBaseCollection> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Items);
}

/// <summary>A stored JSON document.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Version">
/// Opaque version (<c>tbv1-…</c> or <c>tbv2-…</c>; never parse it), also the document's <c>ETag</c>. Pass it to <see cref="TerraBasePrecondition.Matches"/>
/// for optimistic concurrency.
/// </param>
/// <param name="State"><c>value</c>, or <c>conflict</c> on causal reads; <see cref="Document"/> then holds the last-writer-wins value. Other states may be added.</param>
/// <param name="Document">The JSON object, or its projection for queries that project.</param>
/// <param name="Distance">
/// Distance to the query vector of a <see cref="TerraBaseSort.Near"/> query, or meters to the point of a <see cref="TerraBaseSort.NearPoint"/> query; otherwise null.
/// </param>
public sealed record TerraBaseDocument(string Id, string Version, string State, JsonElement Document, double? Distance = null)
{
    /// <summary>Full-text relevance score on results sorted by <c>$score</c>; otherwise null.</summary>
    public double? Score { get; init; }
}

/// <summary>A count of query matches.</summary>
/// <param name="Value">Number of matches; a lower bound when <paramref name="Exact"/> is false.</param>
/// <param name="Exact">Whether every match was counted (TerraBase examines at most 100000 documents).</param>
public sealed record TerraBaseCount(long Value, bool Exact);

/// <summary>One page of documents.</summary>
/// <param name="Items">Documents in id order (lists) or query order.</param>
/// <param name="NextCursor">Cursor of the next page; <see langword="null"/> on the last page.</param>
/// <param name="Count">Match count, when the query asked for it.</param>
/// <param name="Groups">An <see cref="TerraBaseQuery.Aggregate"/> query's groups in key order (<paramref name="Items"/> is then empty); otherwise null.</param>
/// <param name="Explain">How the query ran, when <see cref="TerraBaseQuery.Explain"/> asked for it; otherwise null.</param>
public sealed record TerraBaseDocumentPage(
    IReadOnlyList<TerraBaseDocument> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    TerraBaseCount? Count = null,
    IReadOnlyList<TerraBaseGroup>? Groups = null,
    TerraBaseQueryExplain? Explain = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        TerraBaseJsonContext.RequireNonNullItems(Items);
        if (Groups is not null)
        {
            TerraBaseJsonContext.RequireNonNullItems(Groups);
        }
    }
}

/// <summary>How a document query ran (<see cref="TerraBaseQuery.Explain"/>).</summary>
/// <param name="Plan">Engine plan steps, such as <c>SCAN items</c> or <c>SEARCH items USING INDEX tb_index_status (…)</c>.</param>
/// <param name="Indexes">TerraBase index names the plan uses.</param>
/// <param name="FullScan">Whether a step walks a whole table or index, including the id-order walk.</param>
/// <param name="RowsScanned">Candidate documents visited.</param>
/// <param name="WorkUnits">Engine work units charged against the per-query budget.</param>
/// <param name="ElapsedMs">Server time for the query, in milliseconds.</param>
/// <param name="SuggestedIndex">On a full scan, the filtered field to index in the <c>fields</c> form of <c>POST …/indexes</c>; otherwise null.</param>
public sealed record TerraBaseQueryExplain(
    IReadOnlyList<string> Plan,
    IReadOnlyList<string> Indexes,
    [property: JsonPropertyName("full_scan")] bool FullScan,
    [property: JsonPropertyName("rows_scanned")] long RowsScanned,
    [property: JsonPropertyName("work_units")] long WorkUnits,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    [property: JsonPropertyName("suggested_index")] IReadOnlyList<string>? SuggestedIndex = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        TerraBaseJsonContext.RequireNonNullItems(Plan);
        TerraBaseJsonContext.RequireNonNullItems(Indexes);
        if (SuggestedIndex is not null)
        {
            TerraBaseJsonContext.RequireNonNullItems(SuggestedIndex);
        }
    }
}

/// <summary>One group of an aggregate query. Summaries consider numbers only and are keyed by their JSON Pointer.</summary>
/// <param name="Key">One JSON value per <see cref="TerraBaseAggregate.GroupBy"/> path; missing members group as JSON null.</param>
/// <param name="Count">Matching documents in the group.</param>
/// <param name="Sum">Per <see cref="TerraBaseAggregate.Sum"/> path, the sum (0 without numbers).</param>
/// <param name="Avg">Per <see cref="TerraBaseAggregate.Avg"/> path, the mean; null without numbers.</param>
/// <param name="Min">Per <see cref="TerraBaseAggregate.Min"/> path, the least number; null without numbers.</param>
/// <param name="Max">Per <see cref="TerraBaseAggregate.Max"/> path, the greatest number; null without numbers.</param>
public sealed record TerraBaseGroup(
    IReadOnlyList<JsonElement> Key,
    long Count,
    IReadOnlyDictionary<string, double?>? Sum = null,
    IReadOnlyDictionary<string, double?>? Avg = null,
    IReadOnlyDictionary<string, double?>? Min = null,
    IReadOnlyDictionary<string, double?>? Max = null);

/// <summary>
/// Groups a query's matches instead of returning documents. Each summary list takes distinct JSON Pointers, at most 16
/// summary paths in total; sums and averages are 64-bit floats.
/// </summary>
public sealed record TerraBaseAggregate
{
    /// <summary>0 to 4 JSON Pointers or <c>$id</c>; without them every match forms one group.</summary>
    [JsonPropertyName("group_by")]
    public IReadOnlyList<string>? GroupBy { get; init; }

    /// <summary>JSON Pointers to sum.</summary>
    public IReadOnlyList<string>? Sum { get; init; }

    /// <summary>JSON Pointers to average.</summary>
    public IReadOnlyList<string>? Avg { get; init; }

    /// <summary>JSON Pointers to take the least number of.</summary>
    public IReadOnlyList<string>? Min { get; init; }

    /// <summary>JSON Pointers to take the greatest number of.</summary>
    public IReadOnlyList<string>? Max { get; init; }
}

/// <summary>The result of one document write.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Version">The new version; for a delete, the tombstone's.</param>
/// <param name="Offset">The database commit offset of the write.</param>
/// <param name="Durability">Durability label, for example <c>local_durable</c>; new labels may be added.</param>
/// <param name="Document">Stored document after PATCH, including counters and sets; omitted for other writes.</param>
public sealed record TerraBaseWriteResult(string Id, string Version, long Offset, string Durability, JsonElement? Document = null)
{
    /// <summary>Whether the write created the document (it was absent or deleted). Set for creates and puts.</summary>
    [JsonIgnore]
    public bool Created { get; init; }
}

/// <summary>
/// The condition a write requires. TerraBase compares <c>If-Match</c> strongly and accepts only <c>*</c> for
/// <c>If-None-Match</c> on writes; a failed condition throws <see cref="TerraBasePreconditionFailedException"/>.
/// </summary>
public sealed class TerraBasePrecondition
{
    private readonly EntityTagHeaderValue? ifMatch;
    private readonly bool ifNoneMatch;

    private TerraBasePrecondition(EntityTagHeaderValue? ifMatch, bool ifNoneMatch)
    {
        this.ifMatch = ifMatch;
        this.ifNoneMatch = ifNoneMatch;
    }

    /// <summary>Create-only: the document must be absent or deleted (<c>If-None-Match: *</c>).</summary>
    public static TerraBasePrecondition Absent { get; } = new(null, ifNoneMatch: true);

    /// <summary>The document must exist (<c>If-Match: *</c>).</summary>
    public static TerraBasePrecondition Exists { get; } = new(EntityTagHeaderValue.Any, ifNoneMatch: false);

    /// <summary>The document's current version must be <paramref name="version"/> (<c>If-Match</c>).</summary>
    /// <param name="version">A <see cref="TerraBaseDocument.Version"/> or <see cref="TerraBaseWriteResult.Version"/>.</param>
    public static TerraBasePrecondition Matches(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        try
        {
            return new(new EntityTagHeaderValue($"\"{version}\""), ifNoneMatch: false);
        }
        catch (FormatException error)
        {
            throw new ArgumentException("version must be a TerraBase document version.", nameof(version), error);
        }
    }

    internal void Apply(HttpRequestHeaders headers)
    {
        if (ifMatch is not null)
        {
            headers.IfMatch.Add(ifMatch);
        }

        if (ifNoneMatch)
        {
            headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }
    }
}

/// <summary>One sort key of a query.</summary>
/// <param name="Path">A JSON Pointer to object members, <c>$id</c>, or <c>$score</c> with a search filter; null for a <see cref="Near"/> or <see cref="NearPoint"/> sort.</param>
/// <param name="Order"><c>asc</c> (the default) or <c>desc</c>.</param>
public sealed record TerraBaseSort(string? Path, string? Order = null)
{
    private const int MaxNearDimensions = 4096;

    /// <summary>Highest full-text relevance first. Must be the only sort key and requires exactly one search predicate.</summary>
    public static TerraBaseSort Score { get; } = new("$score", "desc");

    /// <summary>The vector or geo distance order of a <see cref="Near"/> or <see cref="NearPoint"/> sort.</summary>
    [JsonPropertyName("near")]
    public TerraBaseNearSort? Nearest { get; private init; }

    /// <summary>
    /// Nearest documents first, by ascending distance from <paramref name="vector"/> to the numeric array at <paramref name="path"/>
    /// (approximate when a matching vector index is active). It must be the only sort key; the query returns one page of at most 1000 documents, without a cursor, and
    /// each item's <see cref="TerraBaseDocument.Distance"/>. Only documents whose member is a numeric array of the same length match.
    /// </summary>
    /// <param name="path">JSON Pointer of the embedding member.</param>
    /// <param name="vector">1 to 4096 finite numbers; not all zero for <c>cosine</c>.</param>
    /// <param name="metric"><c>cosine</c> (the default) or <c>l2</c>.</param>
    public static TerraBaseSort Near(string path, IReadOnlyList<float> vector, string metric = "cosine")
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(vector);
        if (metric is not ("cosine" or "l2"))
        {
            throw new ArgumentException("metric must be cosine or l2.", nameof(metric));
        }

        if (vector.Count is < 1 or > MaxNearDimensions || !vector.All(float.IsFinite))
        {
            throw new ArgumentException("vector must have 1 to 4096 finite numbers.", nameof(vector));
        }

        if (metric == "cosine" && vector.All(static value => value == 0))
        {
            throw new ArgumentException("a cosine vector must not be all zeros.", nameof(vector));
        }

        return new(Path: null) { Nearest = new(path, vector, metric) };
    }

    /// <summary>
    /// Nearest points first, by ascending haversine meters from (<paramref name="lat"/>, <paramref name="lon"/>) to the
    /// <c>{"lat","lon"}</c> object at <paramref name="path"/>. Like <see cref="Near"/>, it must be the only sort key; the query
    /// returns one page of at most 1000 documents, without a cursor, with each item's <see cref="TerraBaseDocument.Distance"/>
    /// in meters. Documents without a numeric point are skipped.
    /// </summary>
    /// <param name="path">JSON Pointer of the point member.</param>
    /// <param name="lat">Latitude, -90 to 90 WGS 84 degrees.</param>
    /// <param name="lon">Longitude, -180 to 180 WGS 84 degrees.</param>
    public static TerraBaseSort NearPoint(string path, double lat, double lon)
    {
        TerraBaseFilter.RequirePoint(path, lat, lon);
        return new(Path: null) { Nearest = new(path) { Lat = lat, Lon = lon } };
    }
}

/// <summary>A vector or geo distance order; create it with <see cref="TerraBaseSort.Near"/> or <see cref="TerraBaseSort.NearPoint"/>.</summary>
/// <param name="Path">JSON Pointer of the embedding or point member.</param>
/// <param name="Vector">Query vector of a vector order.</param>
/// <param name="Metric"><c>cosine</c> or <c>l2</c> for a vector order.</param>
public sealed record TerraBaseNearSort(string Path, IReadOnlyList<float>? Vector = null, string? Metric = null)
{
    /// <summary>Latitude of a geo order's point.</summary>
    public double? Lat { get; init; }

    /// <summary>Longitude of a geo order's point.</summary>
    public double? Lon { get; init; }
}

/// <summary>
/// A query (TerraBase query v1). Every member is optional. <see cref="Filter"/> uses TerraBase's JSON filter grammar,
/// for example <c>{"eq":["/status","active"]}</c>, <c>{"and":[…]}</c> or <c>{"in":["/role",["a","b"]]}</c>;
/// <see cref="TerraBaseFilter"/> builds geo <c>within</c> nodes.
/// </summary>
public sealed record TerraBaseQuery
{
    /// <summary>The filter; every document matches when absent.</summary>
    public JsonElement? Filter { get; init; }

    /// <summary>At most 4 sort keys; results are finally ordered by id. A <see cref="TerraBaseSort.Near"/> sort is the only key.</summary>
    public IReadOnlyList<TerraBaseSort>? Sort { get; init; }

    /// <summary>At most 64 JSON Pointers to keep in each returned document.</summary>
    public IReadOnlyList<string>? Project { get; init; }

    /// <summary>Page size, 1 to 1000 (TerraBase's default is 50). A page also stops at 8 MiB of JSON.</summary>
    public int? Limit { get; init; }

    /// <summary>The previous page's <see cref="TerraBaseDocumentPage.NextCursor"/>; valid only with the same filter, sort and projection.</summary>
    public string? Cursor { get; init; }

    /// <summary>Whether to count every match (first page only).</summary>
    public bool? Count { get; init; }

    /// <summary>
    /// Groups the matches instead of returning documents (<see cref="TerraBaseDocumentPage.Groups"/>). It takes no sort,
    /// project, cursor or count; <see cref="Limit"/> then bounds the groups, and more groups fail the query.
    /// </summary>
    public TerraBaseAggregate? Aggregate { get; init; }

    /// <summary>
    /// Whether to return <see cref="TerraBaseDocumentPage.Explain"/> (plan, indexes, scan and cost, index suggestion). The query
    /// still runs, bypassing TerraBase's result cache; works with <see cref="Aggregate"/>, <see cref="Count"/> and near sorts.
    /// </summary>
    public bool? Explain { get; init; }
}

/// <summary>One operation of an <c>application/vnd.terrabase.ops+json</c> patch, applied in order.</summary>
public sealed record TerraBasePatchOperation
{
    private TerraBasePatchOperation(string op, string path, JsonElement? value, JsonElement? by)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Op = op;
        Path = path;
        Value = value;
        By = by;
    }

    /// <summary><c>set</c>, <c>unset</c>, <c>inc</c>, <c>append</c>, <c>add</c> or <c>remove</c>.</summary>
    public string Op { get; }

    /// <summary>JSON Pointer to object members.</summary>
    public string Path { get; }

    /// <summary>Value of <c>set</c> and <c>append</c>.</summary>
    public JsonElement? Value { get; }

    /// <summary>Number of <c>inc</c>.</summary>
    public JsonElement? By { get; }

    /// <summary>Sets <paramref name="path"/>, creating missing parents.</summary>
    public static TerraBasePatchOperation Set(string path, JsonElement value) => new("set", path, value, null);

    /// <summary>Removes <paramref name="path"/>.</summary>
    public static TerraBasePatchOperation Unset(string path) => new("unset", path, null, null);

    /// <summary>Adds a non-zero Int64 to a top-level counter field (missing is 0).</summary>
    public static TerraBasePatchOperation Increment(string path, JsonElement by) => new("inc", path, null, by);

    /// <summary>Appends <paramref name="value"/> to the array at <paramref name="path"/>, creating a missing array.</summary>
    public static TerraBasePatchOperation Append(string path, JsonElement value) => new("append", path, value, null);

    /// <summary>Adds a scalar (at most 1 KiB) to a top-level add-wins set.</summary>
    public static TerraBasePatchOperation Add(string path, JsonElement value) => new("add", path, value, null);

    /// <summary>Removes a scalar (at most 1 KiB) from a top-level add-wins set.</summary>
    public static TerraBasePatchOperation Remove(string path, JsonElement value) => new("remove", path, value, null);
}

/// <summary>The <c>{"ops":[…]}</c> patch body.</summary>
internal sealed record TerraBasePatchOperations(IReadOnlyList<TerraBasePatchOperation> Ops);

/// <summary>
/// One operation of an atomic transaction. Assertions see the state before the transaction's writes, and two
/// operations may not target the same document. Create instances with the static factories.
/// </summary>
public sealed record TerraBaseTransactionOperation
{
    private TerraBaseTransactionOperation(string op, string collection)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        Op = op;
        Collection = collection;
    }

    /// <summary>Operation kind: <c>create</c>, <c>put</c>, <c>patch</c>, <c>delete</c>, <c>assert</c> or <c>assert_count</c>.</summary>
    public string Op { get; }

    /// <summary>Target collection.</summary>
    public string Collection { get; }

    /// <summary>Target document id: a canonical uppercase ULID.</summary>
    public string? Id { get; private init; }

    /// <summary>Document of <c>create</c> and <c>put</c>.</summary>
    public JsonElement? Document { get; private init; }

    /// <summary>Required current version.</summary>
    [JsonPropertyName("if_match")]
    public string? IfMatch { get; private init; }

    /// <summary><c>*</c>: the document must be absent or deleted.</summary>
    [JsonPropertyName("if_none_match")]
    public string? IfNoneMatch { get; private init; }

    /// <summary>RFC 7396 merge patch of <c>patch</c>.</summary>
    public JsonElement? Merge { get; private init; }

    /// <summary>Patch operations of <c>patch</c>.</summary>
    public IReadOnlyList<TerraBasePatchOperation>? Ops { get; private init; }

    /// <summary>Filter of <c>assert</c> and <c>assert_count</c>.</summary>
    public JsonElement? Filter { get; private init; }

    /// <summary><c>assert</c> that the document is absent or deleted.</summary>
    public bool? Absent { get; private init; }

    /// <summary>Least count of <c>assert_count</c>.</summary>
    public long? Gte { get; private init; }

    /// <summary>Greatest count of <c>assert_count</c>.</summary>
    public long? Lte { get; private init; }

    /// <summary>Creates a document that must be absent or deleted; TerraBase generates a ULID when <paramref name="id"/> is null.</summary>
    public static TerraBaseTransactionOperation Create(string collection, JsonElement document, string? id = null) =>
        new("create", collection) { Id = id is null ? null : TerraScaleIds.Validate(id), Document = document };

    /// <summary>Inserts or replaces a document, optionally only at version <paramref name="ifMatch"/>.</summary>
    public static TerraBaseTransactionOperation Put(string collection, string id, JsonElement document, string? ifMatch = null) =>
        new("put", collection) { Id = TerraScaleIds.Validate(id), Document = document, IfMatch = ifMatch };

    /// <summary>Applies an RFC 7396 merge patch to an existing document.</summary>
    public static TerraBaseTransactionOperation Patch(string collection, string id, JsonElement merge, string? ifMatch = null) =>
        new("patch", collection) { Id = TerraScaleIds.Validate(id), Merge = merge, IfMatch = ifMatch };

    /// <summary>Applies patch operations to an existing document.</summary>
    public static TerraBaseTransactionOperation Patch(string collection, string id, IReadOnlyList<TerraBasePatchOperation> ops, string? ifMatch = null) =>
        new("patch", collection) { Id = TerraScaleIds.Validate(id), Ops = ops ?? throw new ArgumentNullException(nameof(ops)), IfMatch = ifMatch };

    /// <summary>Deletes an existing document.</summary>
    public static TerraBaseTransactionOperation Delete(string collection, string id, string? ifMatch = null) =>
        new("delete", collection) { Id = TerraScaleIds.Validate(id), IfMatch = ifMatch };

    /// <summary>Asserts that the document exists and matches <paramref name="filter"/>.</summary>
    public static TerraBaseTransactionOperation Assert(string collection, string id, JsonElement filter) =>
        new("assert", collection) { Id = TerraScaleIds.Validate(id), Filter = filter };

    /// <summary>Asserts that the document is absent or deleted.</summary>
    public static TerraBaseTransactionOperation AssertAbsent(string collection, string id) =>
        new("assert", collection) { Id = TerraScaleIds.Validate(id), Absent = true };

    /// <summary>Asserts that between <paramref name="gte"/> and <paramref name="lte"/> documents match <paramref name="filter"/> (at least one bound).</summary>
    public static TerraBaseTransactionOperation AssertCount(string collection, JsonElement? filter = null, long? gte = null, long? lte = null) =>
        gte is null && lte is null
            ? throw new ArgumentException("assert_count needs gte, lte or both.", nameof(gte))
            : new("assert_count", collection) { Filter = filter, Gte = gte, Lte = lte };

}

/// <summary>The <c>{"ops":[…]}</c> transaction body.</summary>
internal sealed record TerraBaseTransaction(IReadOnlyList<TerraBaseTransactionOperation> Ops);

/// <summary>The result of one transaction operation.</summary>
/// <param name="Id">Written document id.</param>
/// <param name="Version">Written document version.</param>
/// <param name="Asserted">True for assertions.</param>
/// <param name="Count">Counted matches of <c>assert_count</c>.</param>
/// <param name="Document">Stored document after a patch, including counters and sorted sets.</param>
public sealed record TerraBaseOperationResult(string? Id = null, string? Version = null, bool? Asserted = null, long? Count = null, JsonElement? Document = null);

/// <summary>The result of a committed transaction.</summary>
/// <param name="Offset">The database commit offset.</param>
/// <param name="Durability">Durability label, for example <c>local_durable</c>; new labels may be added.</param>
/// <param name="Results">One result per operation, in order.</param>
public sealed record TerraBaseTransactionResult(long Offset, string Durability, IReadOnlyList<TerraBaseOperationResult> Results) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Results);
}
