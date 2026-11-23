using System.Diagnostics;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>How TerraBase commits a SQL request or transaction, sent as <c>TerraScale-Consistency</c>.</summary>
[JsonConverter(typeof(TerraBaseStringEnumConverter<TerraBaseConsistencyMode>))]
public enum TerraBaseConsistencyMode
{
    /// <summary>
    /// <c>fast</c>, the default: the write commits in the region that answered, durable on two of its three nodes, and
    /// reaches every other region of the database afterwards. Concurrent writes to one row converge in every region.
    /// </summary>
    [JsonStringEnumMemberName("fast")]
    Fast,

    /// <summary>
    /// <c>strong</c>: the request or transaction runs serializable across regions with a 2-of-3 quorum
    /// (<c>BEGIN STRONG</c>). A lost race answers <see cref="TerraBaseSqlErrorCodes.SerializationConflict"/>.
    /// </summary>
    [JsonStringEnumMemberName("strong")]
    Strong,
}

/// <summary>Per-call options of one SQL request or WebSocket frame.</summary>
public sealed record TerraBaseSqlRequestOptions
{
    /// <summary>The consistency of this call; null keeps the handle's consistency, or the database's default when the handle has none.</summary>
    public TerraBaseConsistencyMode? Consistency { get; init; }

    /// <summary>
    /// A <c>TerraScale-Bookmark</c> from another process or handle that this call must observe. It is merged with the
    /// bookmark the handle tracks, so a call never reads older data than either; null or empty sends only the tracked one.
    /// </summary>
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
