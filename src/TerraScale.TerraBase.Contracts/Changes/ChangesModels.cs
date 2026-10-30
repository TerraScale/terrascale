using System.Text.Json;
using MessagePack;

namespace TerraScale.TerraBase.Contracts.Changes
{
    /// <summary>A committed mutation; deduplicate by origin, version, collection and id.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseChange(string Collection, string Id, string Op, string Version, string Origin, JsonElement? Document);

    /// <summary>A mutation or heartbeat; preserve checkpoints from both when resuming with the same collection filter.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseChangesEvent(string? Cursor, TerraBaseChange? Change, string? Region);

    /// <summary>One live document from an export's read snapshot.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseExportLine(string Collection, string Id, JsonElement Document);
}
