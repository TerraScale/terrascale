using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Features.Data;

public sealed record SavedQueryRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; init; }

    public required string DatabaseId { get; init; }

    public required string Name { get; init; }

    public required string Sql { get; init; }

    public required DateTime CreatedAt { get; init; }
}

public sealed record SqlWorkspaceRow
{
    public string? OriginalVersion { get; set; }

    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; init; }

    public required string DatabaseId { get; init; }

    public required string UserId { get; init; }

    public required string Draft { get; set; }

    public required string History { get; set; }

    public string[] HistoryItems
    {
        get => JsonSerializer.Deserialize(History, DataRowJson.Default.StringArray) ?? [];
        set => History = JsonSerializer.Serialize(value, DataRowJson.Default.StringArray);
    }

    public required string HistoryEpoch { get; set; }

    public string? Receipt { get; set; }

    public SqlRunReceipt? RunReceipt
    {
        get => Receipt is null ? null : JsonSerializer.Deserialize(Receipt, DataRowJson.Default.SqlRunReceipt);
        set => Receipt = value is null ? null : JsonSerializer.Serialize(value, DataRowJson.Default.SqlRunReceipt);
    }

    public required string Version { get; set; }
}

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(SqlRunReceipt))]
internal sealed partial class DataRowJson : JsonSerializerContext;
