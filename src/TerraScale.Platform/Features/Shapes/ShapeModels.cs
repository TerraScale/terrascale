using System.Globalization;
using System.Text.Json.Serialization;

namespace TerraScale.Platform.Features.Shapes;

public sealed record ShapeChange(string Offset, IReadOnlyList<ShapeOperation> Ops);

public sealed record ShapeOperation(
    string Op,
    string Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Row = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Move = null);

public sealed record ShapeResponse(
    string ShapeId,
    string Handle,
    string Offset,
    string NextOffset,
    string ValuesAt,
    bool UpToDate,
    IReadOnlyList<ShapeChange> Changes);

public sealed record ShapeErrorDetail(string Code, string Message, string Handle);

public sealed record ShapeErrorBody(ShapeErrorDetail Error);

public sealed record ShapeMirrorObject(
    string Offset,
    string NextOffset,
    string Binding,
    [property: JsonPropertyName("object")] string ObjectName,
    string Url,
    DateTimeOffset ExpiresAt,
    string Epoch,
    string KeyId,
    string Sha256,
    long Size);

public sealed record ShapeMirrorManifest(string Handle, IReadOnlyList<ShapeMirrorObject> Objects);

public sealed record ShapeKeyResponse(string Handle, string Epoch, string KeyId, string Key);

public sealed record ShapeHintFrame(
    string Type,
    string? Handle = null,
    string? Offset = null,
    string? NextOffset = null,
    ShapeMirrorManifest? Manifest = null,
    string? Code = null,
    int? RetryAfter = null);

public sealed record ShapeHintAuth(string Type, string Token);

public sealed record RowResponse(JsonElement Row);

public sealed record ShapeRowEntry(string Key, JsonElement Row);

public sealed record ShapeChunk(long Offset, long Previous, string Digest, IReadOnlyList<string> Columns, IReadOnlyList<ShapeRowEntry> Rows, IReadOnlyList<ShapeOperation> Ops);

public sealed record GetRowRequest(string Project, string Database, string Table, string Key, string? Columns);

public sealed record ShapeRouteRequest(string Project, string Database, string Table);

public sealed record ShapeSnapshot(IReadOnlyList<string> Columns, IReadOnlyList<ShapeRowEntry> Rows, string Digest);

public sealed record ShapeRecord(
    string Handle,
    string ShapeId,
    Ulid Organization,
    Ulid Project,
    Ulid Database,
    string ProjectRoute,
    string DatabaseRoute,
    string Table,
    string? Where,
    string? Columns,
    string ViewerKey,
    long Head,
    string HeadDigest);

public sealed record ShapeChunkInfo(long Offset, string Name, string Sha256, long Size, long Epoch, string Digest);

public sealed record ShapeRetirement(IReadOnlyList<string> Handles);

public closed record class ShapeOffset
{
    public static ShapeOffset? Parse(string text)
    {
        if (text == "-1")
        {
            return new BootstrapOffset();
        }

        if (ParseDecimal(text) is { } at)
        {
            return new DecimalOffset(at);
        }

        return text.Split('.') is ["snap", var snapshot, var page] && ParseDecimal(snapshot) is { } offset && ParseDecimal(page) is { } index and <= int.MaxValue
            ? new SnapshotOffset(offset, (int)index)
            : null;
    }

    public static long? ParseDecimal(string text) =>
        text.Length is > 0 and <= 19 && (text == "0" || text[0] != '0') && text.All(char.IsAsciiDigit)
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}

public sealed record BootstrapOffset : ShapeOffset;

public sealed record SnapshotOffset(long Snapshot, int Page) : ShapeOffset;

public sealed record DecimalOffset(long Value) : ShapeOffset;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ShapeResponse))]
[JsonSerializable(typeof(ShapeErrorBody))]
[JsonSerializable(typeof(ShapeMirrorManifest))]
[JsonSerializable(typeof(ShapeKeyResponse))]
[JsonSerializable(typeof(ShapeHintFrame))]
[JsonSerializable(typeof(ShapeHintAuth))]
[JsonSerializable(typeof(RowResponse))]
[JsonSerializable(typeof(ShapeChunk))]
public sealed partial class ShapesJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GetRowRequest))]
[JsonSerializable(typeof(ShapeRouteRequest))]
public sealed partial class ShapeRequestsJsonContext : JsonSerializerContext;
