using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Features.Documents;

/// <summary>A document as returned by point reads, lists and queries.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Version">Version, also sent as the <c>ETag</c> of point responses.</param>
/// <param name="State">Always <c>value</c>: a single-writer database never holds conflicting siblings.</param>
/// <param name="Document">The stored JSON object, or its projection.</param>
public sealed record DocumentBody(string Id, string Version, string State, JsonElement Document);

/// <summary>Result of a document write.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Version">The new version; for a delete, the tombstone's.</param>
/// <param name="Offset">The database commit offset of the write, a decimal string.</param>
/// <param name="Durability">Always <c>local_durable</c>.</param>
public sealed record WriteBody(string Id, string Version, string Offset, string Durability);

/// <summary>A count of query matches.</summary>
/// <param name="Value">Decimal count; a lower bound when not exact.</param>
/// <param name="Exact">Whether every match was counted (at most 100000).</param>
public sealed record CountBody(string Value, bool Exact);

/// <summary>One page of documents.</summary>
/// <param name="Items">Documents in query order.</param>
/// <param name="NextCursor">Cursor of the next page; <see langword="null"/> on the last page.</param>
/// <param name="Count">Match count, when requested.</param>
public sealed record DocumentPageBody(
    IReadOnlyList<DocumentBody> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CountBody? Count = null);

/// <summary>A collection.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="CreatedAt">Creation time, decimal Unix milliseconds.</param>
public sealed record CollectionBody(string Name, [property: JsonPropertyName("created_at")] string CreatedAt);

/// <summary>One page of collections.</summary>
/// <param name="Items">Collections in name order.</param>
/// <param name="NextCursor">Cursor of the next page; <see langword="null"/> on the last page.</param>
public sealed record CollectionPageBody(IReadOnlyList<CollectionBody> Items, [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>A collection to create. Optional JSON Schema is attached through the schema endpoint.</summary>
/// <param name="Name">Collection name: <c>^[a-z][a-z0-9_-]{0,62}$</c>.</param>
public sealed record CreateCollectionRequest(string? Name);

/// <summary>One sort key of a query.</summary>
/// <param name="Path">JSON Pointer or <c>$id</c>.</param>
/// <param name="Order"><c>asc</c> (default) or <c>desc</c>.</param>
public sealed record QuerySortRequest(JsonElement Path, string? Order);

/// <summary>A query (query v1). Every member is optional.</summary>
/// <param name="Filter">Filter grammar: <c>and</c>, <c>or</c>, <c>not</c>, <c>eq</c>, <c>ne</c>, <c>lt</c>, <c>lte</c>, <c>gt</c>, <c>gte</c>, <c>in</c>, <c>nin</c>, <c>exists</c>, <c>prefix</c>, <c>contains</c>.</param>
/// <param name="Sort">At most 4 sort keys; results are finally ordered by id.</param>
/// <param name="Project">At most 64 paths to keep.</param>
/// <param name="Limit">1 to 1000, default 50.</param>
/// <param name="Cursor"><c>next_cursor</c> of the previous page.</param>
/// <param name="Count">Whether to count every match (first page only).</param>
/// <param name="Explain">Not supported yet; must be absent.</param>
public sealed record QueryRequest(
    JsonElement Filter,
    IReadOnlyList<QuerySortRequest>? Sort,
    IReadOnlyList<JsonElement>? Project,
    int? Limit,
    string? Cursor,
    bool? Count,
    JsonElement Explain);

/// <summary>One patch operation.</summary>
/// <param name="Op"><c>set</c>, <c>unset</c>, <c>inc</c> or <c>append</c>.</param>
/// <param name="Path">JSON Pointer of object members.</param>
/// <param name="Value">Value for <c>set</c> and <c>append</c>.</param>
/// <param name="By">Number for <c>inc</c>.</param>
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
public sealed record TransactionResultBody(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Asserted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Count);

/// <summary>Result of a transaction.</summary>
/// <param name="Offset">The database commit offset, a decimal string.</param>
/// <param name="Durability">Always <c>local_durable</c>.</param>
/// <param name="Results">One result per operation, in order.</param>
public sealed record TransactionBody(string Offset, string Durability, IReadOnlyList<TransactionResultBody> Results);

/// <summary>A shape snapshot page or log chunk (docs/shape-reads.md).</summary>
/// <param name="ShapeId">32 lowercase hex characters naming the shape.</param>
/// <param name="Handle">Log lineage; a different one answers <c>409 must_refetch</c>.</param>
/// <param name="Offset">The requested offset.</param>
/// <param name="NextOffset">Offset to request next.</param>
/// <param name="UpToDate">Whether <paramref name="NextOffset"/> is the head.</param>
/// <param name="Changes">Commits with changes to the shape, in offset order; a snapshot page has exactly one.</param>
public sealed record ShapeBody(
    [property: JsonPropertyName("shape_id")] string ShapeId,
    string Handle,
    string Offset,
    [property: JsonPropertyName("next_offset")] string NextOffset,
    [property: JsonPropertyName("up_to_date")] bool UpToDate,
    IReadOnlyList<ShapeChangeBody> Changes);

/// <summary>The changes of one commit.</summary>
/// <param name="Offset">Decimal commit offset; the snapshot head for a snapshot page.</param>
/// <param name="Ops">Operations in document id order.</param>
public sealed record ShapeChangeBody(string Offset, IReadOnlyList<ShapeOpBody> Ops);

/// <summary>One document entering or leaving the shape.</summary>
/// <param name="Op"><c>upsert</c> or <c>delete</c> (deleted, or no longer matching).</param>
/// <param name="Id">Document id.</param>
/// <param name="Version">Version of an upserted document.</param>
/// <param name="Document">The upserted JSON object.</param>
public sealed record ShapeOpBody(
    string Op,
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Document = null);
