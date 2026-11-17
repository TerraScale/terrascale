using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Filters and projects a live table shape.</summary>
public sealed record ShapeOptions
{
    /// <summary>A non-empty SQL condition over the table columns.</summary>
    public string? Where { get; init; }

    /// <summary>Distinct column names; null selects every stored column.</summary>
    public IReadOnlyList<string>? Columns { get; init; }
}

/// <summary>The connection state of a shape view.</summary>
public enum ShapeStatus
{
    /// <summary>The paged snapshot or catch-up is in progress.</summary>
    Loading,
    /// <summary>The rows reached the server head.</summary>
    Live,
    /// <summary>A transient connection failure is being recovered from the applied offset.</summary>
    Reconnecting,
}

/// <summary>An immutable table view at an applied commit boundary, with regional provisional writes overlaid.</summary>
/// <param name="Rows">Rows keyed by column name, using the SQL API JSON cell representation.</param>
/// <param name="Offset">Last applied server offset.</param>
/// <param name="Status">Snapshot and connection progress.</param>
public sealed record ShapeView(IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Rows, string Offset, ShapeStatus Status);

internal sealed record ShapePage(string Handle, string NextOffset, bool UpToDate, IReadOnlyList<ShapeCommit> Changes,
    IReadOnlyList<ShapePending>? Provisional = null, string? Local = null);
internal sealed record ShapeCommit(string Offset, JsonElement Ops, IReadOnlyList<string>? Confirmed = null, IReadOnlyList<string>? Revert = null);
internal sealed record ShapePending(string Id, JsonElement Ops);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ShapePage))]
internal sealed partial class ShapeJsonContext : JsonSerializerContext;
