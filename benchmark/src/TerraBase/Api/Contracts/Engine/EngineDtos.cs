// <copyright file="EngineDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json.Serialization;

/// <summary>The engine shutdown request.</summary>
public sealed record ShutdownRequestDto
{
    /// <summary>The shutdown reason: <c>operator</c> (default) or <c>supervisor</c>.</summary>
    public string? Reason { get; init; }
}

/// <summary>The engine shutdown answer.</summary>
public sealed record ShutdownResponseDto(string State, uint InFlight);

/// <summary>The composite health answer; never optimistic.</summary>
public sealed record HealthResponse(string Status, EngineHealth Engine);

/// <summary>The engine half of the composite health.</summary>
public sealed record EngineHealth(
    string? State,
    uint? Sessions,
    uint? Subscriptions,
    ulong? FramesServed,
    int Generation,
    int Restarts,
    string? Detail);

/// <summary>The host metrics answer.</summary>
public sealed record MetricsResponse(
    int EngineGeneration,
    int EngineRestarts,
    string EngineStatus,
    int OpenStreams,
    long DroppedFrames,
    int TrackedSessions,
    int AuditRecords);

/// <summary>The audit answer.</summary>
public sealed record AuditResponse(IReadOnlyList<AuditRecord> Records);

/// <summary>The typed error answer.</summary>
public sealed record ErrorDto(string Code, string Detail)
{
    /// <summary>
    /// Set on a checkpoint refusal: the consumer resyncs by pulling a
    /// snapshot. Serialized only when demanded — a plain typed refusal
    /// omits the member instead of stating a null marker, so null-unsafe
    /// refusal parsers never see one (the #93 cross-repo defect).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ResyncRequired { get; init; }
}
