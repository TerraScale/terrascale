using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Features.Sync;
using TerraBase.Features.Clock;

namespace TerraBase.Features.HomeApply;

internal sealed record RegionTransaction(string? Id, string? Region, HlcTimestamp? Hlc, IReadOnlyList<RegionChange>? Changes)
{
    [JsonIgnore]
    internal StreamSequence? Position { get; init; }
}

internal sealed record RegionChange(string? Op, string? Table, IReadOnlyDictionary<string, JsonElement>? Key, IReadOnlyDictionary<string, JsonElement>? Values);

internal sealed record HomeApplyResult(string Id, string State, IReadOnlyList<HomeApplyConflict> Conflicts, IReadOnlyList<HomeApplyRow> Rows);

internal sealed record HomeApplyConflict(string Kind, string? Table, JsonElement? Key, string Message);

internal sealed record HomeApplyRow(string Table, JsonElement Key, JsonElement? Values);

internal static class WriteStates
{
    public const string Provisional = "provisional";

    public const string Confirmed = "confirmed";

    public const string Reverted = "reverted";

    public const string Superseded = "superseded";
}
