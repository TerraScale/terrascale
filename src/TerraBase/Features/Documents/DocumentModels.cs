using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Features.Documents;

/// <summary>A document as returned by point reads, lists and queries.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Version">Version, also sent as the <c>ETag</c> of point responses.</param>
/// <param name="State">Always <c>value</c>: a single-writer database never holds conflicting siblings.</param>
/// <param name="Document">The stored JSON object, or its projection.</param>
/// <param name="Distance">Distance to the query vector, for a near sort.</param>
/// <param name="Score">Native BM25 relevance for a score sort; higher values rank first.</param>
public sealed record DocumentBody(
    string Id, string Version, string State, JsonElement Document,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Distance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Score = null);

/// <summary>Result of a document write.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Version">The new version; for a delete, the tombstone's.</param>
/// <param name="Offset">The database commit offset of the write, a decimal string.</param>
/// <param name="Durability"><c>local_durable</c> once this region's commit is synced (the default, decision 0013e), <c>remote_durable</c> once another placed region confirmed the write (with <c>TB-Durability: remote_durable</c>, decision 0013a), <c>unchanged</c> for assertions only.</param>
/// <param name="Document">After a PATCH, the stored document, with counters as integers and sets as sorted arrays.</param>
public sealed record WriteBody(
    string Id, string Version, string Offset, string Durability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Document = null);

/// <summary>A count of query matches.</summary>
/// <param name="Value">Decimal count; a lower bound when not exact.</param>
/// <param name="Exact">Whether every match was counted (at most 100000).</param>
public sealed record CountBody(string Value, bool Exact);

/// <summary>One line of a database export.</summary>
/// <param name="Collection">Collection name.</param>
/// <param name="Id">Document id.</param>
/// <param name="Document">The stored document.</param>
public sealed record ExportLineBody(string Collection, string Id, JsonElement Document);

/// <summary>One page of documents.</summary>
/// <param name="Items">Documents in query order.</param>
/// <param name="NextCursor">Cursor of the next page; <see langword="null"/> on the last page.</param>
/// <param name="Count">Match count, when requested.</param>
/// <param name="Groups">An aggregate's groups, in key order; <c>items</c> is then empty.</param>
public sealed record DocumentPageBody(
    IReadOnlyList<DocumentBody> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CountBody? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GroupBody>? Groups = null);

/// <summary>One group of an aggregate query.</summary>
/// <param name="Key">Per <c>group_by</c> path, the group's value (missing members group as null).</param>
/// <param name="Count">Decimal count of matching documents in the group.</param>
/// <param name="Sum">Per requested path, the sum of its numbers (0 without any).</param>
/// <param name="Avg">Per requested path, the mean of its numbers, or null without any.</param>
/// <param name="Min">Per requested path, the least number, or null without any.</param>
/// <param name="Max">Per requested path, the greatest number, or null without any.</param>
public sealed record GroupBody(
    IReadOnlyList<JsonElement> Key, string Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, double?>? Sum,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, double?>? Avg,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, double?>? Min,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, double?>? Max);

/// <summary>A collection.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="CreatedAt">Creation time, decimal Unix milliseconds.</param>
/// <param name="Id">Canonical uppercase ULID; the collection name remains its route selector.</param>
public sealed record CollectionBody(string Name, [property: JsonPropertyName("created_at")] string CreatedAt, string Id);

/// <summary>One page of collections.</summary>
/// <param name="Items">Collections in name order.</param>
/// <param name="NextCursor">Cursor of the next page; <see langword="null"/> on the last page.</param>
public sealed record CollectionPageBody(IReadOnlyList<CollectionBody> Items, [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>A collection to create. Optional JSON Schema is attached through the schema endpoint.</summary>
/// <param name="Name">Collection name: <c>^[a-z][a-z0-9_-]{0,62}$</c>.</param>
public sealed record CreateCollectionRequest(string? Name);

/// <summary>One sort key of a query: a path, or a vector similarity order alone.</summary>
/// <param name="Path">JSON Pointer or <c>$id</c>.</param>
/// <param name="Order"><c>asc</c> (default) or <c>desc</c>.</param>
/// <param name="Near">Nearest documents first, by distance to a query vector; the only sort key.</param>
public sealed record QuerySortRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Path, string? Order,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QueryNearRequest? Near = null);

/// <summary>A vector similarity order (brute force).</summary>
/// <param name="Path">JSON Pointer of the embedding, a numeric array of the same length.</param>
/// <param name="Vector">1 to 4096 numbers.</param>
/// <param name="Metric"><c>cosine</c> (default) or <c>l2</c>.</param>
/// <param name="Lat">Instead of a vector: latitude of a point; documents order by meters from it to their <c>{lat, lon}</c> object.</param>
/// <param name="Lon">Longitude of that point.</param>
public sealed record QueryNearRequest(JsonElement Path, IReadOnlyList<double>? Vector, string? Metric, double? Lat = null, double? Lon = null);

/// <summary>Groups matches instead of returning documents. Summaries consider numbers only.</summary>
/// <param name="GroupBy">0 to 4 paths (JSON Pointers or <c>$id</c>).</param>
/// <param name="Sum">JSON Pointers to sum.</param>
/// <param name="Avg">JSON Pointers to average.</param>
/// <param name="Min">JSON Pointers to take the least number of.</param>
/// <param name="Max">JSON Pointers to take the greatest number of.</param>
public sealed record QueryAggregateRequest(
    [property: JsonPropertyName("group_by")] IReadOnlyList<JsonElement>? GroupBy,
    IReadOnlyList<string>? Sum, IReadOnlyList<string>? Avg, IReadOnlyList<string>? Min, IReadOnlyList<string>? Max);

/// <summary>A query (query v1). Every member is optional.</summary>
/// <param name="Filter">Filter grammar: <c>and</c>, <c>or</c>, <c>not</c>, <c>eq</c>, <c>ne</c>, <c>lt</c>, <c>lte</c>, <c>gt</c>, <c>gte</c>, <c>in</c>, <c>nin</c>, <c>exists</c>, <c>prefix</c>, <c>contains</c>, <c>search</c>, <c>within</c>.</param>
/// <param name="Sort">At most 4 sort keys; results are finally ordered by id.</param>
/// <param name="Project">At most 64 paths to keep.</param>
/// <param name="Limit">1 to 1000, default 50.</param>
/// <param name="Cursor"><c>next_cursor</c> of the previous page.</param>
/// <param name="Count">Whether to count every match (first page only).</param>
/// <param name="Explain">Not supported yet; must be absent.</param>
/// <param name="Aggregate">Groups instead of documents; <c>limit</c> then bounds the groups.</param>
public sealed record QueryRequest(
    JsonElement Filter,
    IReadOnlyList<QuerySortRequest>? Sort,
    IReadOnlyList<JsonElement>? Project,
    int? Limit,
    string? Cursor,
    bool? Count,
    JsonElement Explain,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QueryAggregateRequest? Aggregate = null);

/// <summary>One patch operation.</summary>
/// <param name="Op"><c>set</c>, <c>unset</c>, <c>append</c>, <c>inc</c>, <c>add</c> or <c>remove</c>.</param>
/// <param name="Path">JSON Pointer of object members; a top-level field (<c>/name</c>) for <c>inc</c>, <c>add</c> and <c>remove</c>.</param>
/// <param name="Value">Value for <c>set</c> and <c>append</c>; a JSON scalar of at most 1 KiB for <c>add</c> and <c>remove</c>.</param>
/// <param name="By">Non-zero 64-bit integer for <c>inc</c>.</param>
public sealed record PatchOpRequest(string? Op, string? Path, JsonElement Value, JsonElement By);

/// <summary>An <c>application/vnd.terrabase.ops+json</c> patch.</summary>
/// <param name="Ops">1 to 256 operations, applied in order.</param>
public sealed record PatchOpsRequest(IReadOnlyList<PatchOpRequest>? Ops);

/// <summary>One transaction operation.</summary>
/// <param name="Op"><c>create</c>, <c>put</c>, <c>patch</c>, <c>delete</c>, <c>assert</c> or <c>assert_count</c>.</param>
/// <param name="Collection">Target collection.</param>
/// <param name="Id">Target document; optional for <c>create</c>, absent for <c>assert_count</c>.</param>
/// <param name="Document">Document of <c>create</c> and <c>put</c>.</param>
/// <param name="IfMatch">Required current version.</param>
/// <param name="IfNoneMatch"><c>*</c>: the document must be absent or deleted.</param>
/// <param name="Merge">Merge patch of <c>patch</c>.</param>
/// <param name="Ops">Patch operations of <c>patch</c>.</param>
/// <param name="Filter">Filter of <c>assert</c> and <c>assert_count</c>.</param>
/// <param name="Absent"><c>assert</c> that the document is absent or deleted.</param>
/// <param name="Gte">Least count of <c>assert_count</c>.</param>
/// <param name="Lte">Greatest count of <c>assert_count</c>.</param>
public sealed record TransactionOpRequest(
    string? Op,
    string? Collection,
    string? Id,
    JsonElement Document,
    [property: JsonPropertyName("if_match")] string? IfMatch,
    [property: JsonPropertyName("if_none_match")] string? IfNoneMatch,
    JsonElement Merge,
    IReadOnlyList<PatchOpRequest>? Ops,
    JsonElement Filter,
    bool? Absent,
    long? Gte,
    long? Lte);

/// <summary>An atomic transaction.</summary>
/// <param name="Ops">1 to 1024 operations.</param>
public sealed record TransactionRequest(IReadOnlyList<TransactionOpRequest>? Ops);

/// <summary>Result of one transaction operation.</summary>
/// <param name="Id">Written document id.</param>
/// <param name="Version">Written document version.</param>
/// <param name="Asserted">Set by assertions.</param>
/// <param name="Count">Decimal count of <c>assert_count</c>.</param>
/// <param name="Document">After a <c>patch</c>, the stored document, with counters as integers and sets as sorted arrays.</param>
public sealed record TransactionResultBody(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Asserted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Document = null);

/// <summary>Result of a transaction.</summary>
/// <param name="Offset">The database commit offset, a decimal string.</param>
/// <param name="Durability"><c>local_durable</c> once this region's commit is synced (the default, decision 0013e), <c>remote_durable</c> once another placed region confirmed the write (with <c>TB-Durability: remote_durable</c>, decision 0013a), <c>unchanged</c> for assertions only.</param>
/// <param name="Results">One result per operation, in order.</param>
public sealed record TransactionBody(string Offset, string Durability, IReadOnlyList<TransactionResultBody> Results);
