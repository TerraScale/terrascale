using System.Text.Json.Serialization;

namespace TerraScale.DotNet.Generated;

/// <summary>A page of project-scoped durable operations.</summary>
public sealed record OperationPage(
    [property: JsonPropertyName("operations"), JsonRequired] IReadOnlyList<OperationSummary> Operations,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("covered_kinds"), JsonRequired] IReadOnlyList<string> CoveredKinds
);

/// <summary>A durable operation summary returned by operation discovery.</summary>
public sealed record OperationSummary(
    [property: JsonPropertyName("operation_id"), JsonRequired] string OperationId,
    [property: JsonPropertyName("kind"), JsonRequired] string Kind,
    [property: JsonPropertyName("database_id"), JsonRequired] string DatabaseId,
    [property: JsonPropertyName("name"), JsonRequired] string Name,
    [property: JsonPropertyName("state"), JsonRequired] string State,
    [property: JsonPropertyName("stage"), JsonRequired] string Stage,
    [property: JsonPropertyName("created_at"), JsonRequired] string CreatedAt,
    [property: JsonPropertyName("revision"), JsonRequired] ulong Revision,
    [property: JsonPropertyName("cancellable"), JsonRequired] bool Cancellable,
    [property: JsonPropertyName("cancellation_reason"), JsonRequired] string CancellationReason
);
