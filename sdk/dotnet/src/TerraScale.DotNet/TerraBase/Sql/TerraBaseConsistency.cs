using System.Diagnostics;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

[JsonConverter(typeof(TerraBaseStringEnumConverter<TerraBaseConsistencyMode>))]
public enum TerraBaseConsistencyMode
{
    [JsonStringEnumMemberName("fast")]
    Fast,

    [JsonStringEnumMemberName("strong")]
    Strong,
}

public sealed record TerraBaseSqlRequestOptions
{
    public TerraBaseConsistencyMode? Consistency { get; init; }

    public string? Bookmark { get; init; }
}

internal static class TerraBaseConsistency
{
    public const string BookmarkHeader = "TerraScale-Bookmark";
    public const string ConsistencyHeader = "TerraScale-Consistency";

    public static string Format(TerraBaseConsistencyMode mode) => mode switch
    {
        TerraBaseConsistencyMode.Fast => "fast",
        TerraBaseConsistencyMode.Strong => "strong",
        _ => throw new UnreachableException(),
    };

    public static string? Format(TerraBaseConsistencyMode? mode) => mode is { } value ? Format(value) : null;
}
