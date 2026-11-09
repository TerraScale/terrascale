using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

internal sealed record ShapeScope(TenantContext Database, string Table, string Generation, ShapeQuery Query, ShapeFilter? Filter)
{
    internal string Key => Table + "\0" + (Filter?.Key ?? "");
}

internal sealed record ShapeLineage(string ShapeId, string Handle);

internal sealed record ResolvedShape(ShapeScope Scope, ShapeLineage Lineage);

internal sealed record ShapeBody(ReadOnlyMemory<byte> Json, string NextOffset, bool UpToDate, IDisposable? Lease = null, ShapeMirrorManifest? Manifest = null,
    string? Local = null);

internal sealed record ShapeProvisional(byte[] Json, string Digest, IReadOnlyCollection<string> Held)
{
    public const string Empty = "0";

    public static string DigestOf(IReadOnlyList<string> ids) => ids.Count == 0
        ? Empty
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", ids))).AsSpan(0, 8));

    public static bool IsDigest(string text) => text == Empty || (text.Length == 16 && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
}

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
    ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, string? local, CancellationToken cancellationToken);
}

internal sealed record ShapeResponse(
    string ShapeId, string Handle, string Offset, string NextOffset, string ValuesAt, bool UpToDate, IReadOnlyList<ShapeChange> Changes);

internal sealed record ShapeChange(string Offset, IReadOnlyList<ShapeOperation> Ops);

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
