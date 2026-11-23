using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

public sealed record ShapeOptions
{
    public string? Where { get; init; }

    public IReadOnlyList<string>? Columns { get; init; }
}

public enum ShapeStatus
{
    Loading,
    Live,
    Reconnecting,
}

public sealed record ShapeView(IReadOnlyList<TerraBaseSqlRecord> Rows, string Offset, ShapeStatus Status);

internal sealed record ShapePage(string Handle, string NextOffset, bool UpToDate, IReadOnlyList<ShapeCommit> Changes,
    IReadOnlyList<ShapePending>? Provisional = null, string? Local = null);
internal sealed record ShapeCommit(string Offset, IReadOnlyList<ShapeOperation> Ops, IReadOnlyList<string>? Confirmed = null, IReadOnlyList<string>? Revert = null);
internal sealed record ShapePending(string Id, IReadOnlyList<ShapeOperation> Ops);
internal sealed record ShapeOperation(string Op, string Key, TerraBaseSqlRecord? Row = null, string? Move = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, Converters = [typeof(ShapeRowConverter)])]
[JsonSerializable(typeof(ShapePage))]
internal sealed partial class ShapeJsonContext : JsonSerializerContext;
