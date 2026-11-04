using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

internal sealed record ShapeScope(TenantContext Database, string Table, string Generation, ShapeQuery Query, ShapeFilter? Filter)
{
    internal string Key => Table + "\0" + (Filter?.Key ?? "");
}

internal sealed record ShapeLineage(string ShapeId, string Handle);

internal sealed record ResolvedShape(ShapeScope Scope, ShapeLineage Lineage);

internal sealed record ShapeBody(ReadOnlyMemory<byte> Json, string NextOffset, bool UpToDate, IDisposable? Lease = null);

internal sealed record MustRefetch(string Message, ShapeLineage? Lineage = null);

internal readonly union LineageResult(ResolvedShape, QuotaExceeded, Validation, Forbidden, NotFound, Unavailable);

internal readonly union SnapshotResult(long, MustRefetch, QuotaExceeded, NotFound, Unavailable);

internal readonly union ShapeRead(ShapeBody, MustRefetch, QuotaExceeded, NotFound, Unavailable);

internal interface IShapeLineages
{
    ValueTask<LineageResult> ResolveAsync(TenantContext database, string table, ShapeQuery query, CancellationToken cancellationToken);

    ValueTask<LineageResult> CheckAsync(TenantContext database, string table, ShapeQuery query, CancellationToken cancellationToken);
}

internal interface IShapeSnapshots
{
    ValueTask<SnapshotResult> NewestAsync(ShapeScope scope, ShapeLineage lineage, CancellationToken cancellationToken);

    ValueTask<ShapeRead> PageAsync(ShapeScope scope, ShapeLineage lineage, long snapshot, int page, CancellationToken cancellationToken);
}

internal interface IShapeChunks
{
    ValueTask<ShapeRead> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken);
}

internal interface IShapeLiveTail
{
    ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken);
}

/// <summary>A shape response body; served bodies are the sealed bytes, never re-serialized.</summary>
/// <param name="ShapeId">32 lowercase hex characters.</param>
/// <param name="Handle"><c>h1-</c> and 16 lowercase hex characters.</param>
/// <param name="Offset">The request's offset.</param>
/// <param name="NextOffset">Where to continue.</param>
/// <param name="ValuesAt">The commit boundary the row values are at.</param>
/// <param name="UpToDate">Only fresh bodies at the head are up to date.</param>
/// <param name="Changes">One change per commit, in commit order.</param>
internal sealed record ShapeResponse(
    string ShapeId, string Handle, string Offset, string NextOffset, string ValuesAt, bool UpToDate, IReadOnlyList<ShapeChange> Changes);

/// <summary>One commit's row operations on the table.</summary>
/// <param name="Offset">The commit boundary, or <c>S</c> on snapshot pages.</param>
/// <param name="Ops">The commit's row operations in commit order.</param>
internal sealed record ShapeChange(string Offset, IReadOnlyList<ShapeOperation> Ops);

/// <summary>An <c>insert</c> or <c>update</c> with the row's column values, or a <c>delete</c>.</summary>
/// <param name="Op"><c>insert</c>, <c>update</c> or <c>delete</c>.</param>
/// <param name="Key">The row id, as a decimal string.</param>
/// <param name="Row">The shape's column values: null, numbers, strings, or blobs as <c>{"base64":…}</c>.</param>
/// <param name="Move"><c>in</c> on an insert for a row that started matching where; <c>out</c> on a delete for a row that stopped matching.</param>
internal sealed record ShapeOperation(
    string Op, string Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Row = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Move = null);

internal sealed record ShapeErrorBody(ShapeErrorDetail Error);

internal sealed record ShapeErrorDetail(string Code, string Message, string Handle);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ShapeResponse))]
[JsonSerializable(typeof(ShapeErrorBody))]
internal sealed partial class ShapeJsonContext : JsonSerializerContext;
