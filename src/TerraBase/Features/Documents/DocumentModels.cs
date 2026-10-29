using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Features.Documents;
public sealed record DocumentBody(
    string Id, string Version, string State, JsonElement Document,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Distance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Score = null);
public sealed record WriteBody(
    string Id, string Version, string Offset, string Durability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Document = null);
public sealed record CountBody(string Value, bool Exact);
public sealed record ExportLineBody(string Collection, string Id, JsonElement Document);
public sealed record DocumentPageBody(
    IReadOnlyList<DocumentBody> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CountBody? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GroupBody>? Groups = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExplainBody? Explain = null);
public sealed record ExplainBody(
    IReadOnlyList<string> Plan, IReadOnlyList<string> Indexes,
    [property: JsonPropertyName("full_scan")] bool FullScan,
    [property: JsonPropertyName("rows_scanned")] long RowsScanned,
    [property: JsonPropertyName("work_units")] long WorkUnits,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    [property: JsonPropertyName("suggested_index"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? SuggestedIndex);
public sealed record GroupBody(
    IReadOnlyList<JsonElement> Key, string Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, double?>? Sum,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, double?>? Avg,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, double?>? Min,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, double?>? Max);
public sealed record CollectionBody(string Name, [property: JsonPropertyName("created_at")] string CreatedAt, string Id);
public sealed record CollectionPageBody(IReadOnlyList<CollectionBody> Items, [property: JsonPropertyName("next_cursor")] string? NextCursor);
public sealed record CreateCollectionRequest(string? Name);
public sealed record QuerySortRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Path, string? Order,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QueryNearRequest? Near = null);
public sealed record QueryNearRequest(JsonElement Path, IReadOnlyList<double>? Vector, string? Metric, double? Lat = null, double? Lon = null);
public sealed record QueryAggregateRequest(
    [property: JsonPropertyName("group_by")] IReadOnlyList<JsonElement>? GroupBy,
    IReadOnlyList<string>? Sum, IReadOnlyList<string>? Avg, IReadOnlyList<string>? Min, IReadOnlyList<string>? Max);
public sealed record QueryRequest(
    JsonElement Filter,
    IReadOnlyList<QuerySortRequest>? Sort,
    IReadOnlyList<JsonElement>? Project,
    int? Limit,
    string? Cursor,
    bool? Count,
    JsonElement Explain,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QueryAggregateRequest? Aggregate = null);
public sealed record PatchOpRequest(string? Op, string? Path, JsonElement Value, JsonElement By);
public sealed record PatchOpsRequest(IReadOnlyList<PatchOpRequest>? Ops);
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
public sealed record TransactionRequest(IReadOnlyList<TransactionOpRequest>? Ops);
public sealed record TransactionResultBody(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Asserted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Document = null);
public sealed record TransactionBody(string Offset, string Durability, IReadOnlyList<TransactionResultBody> Results);
