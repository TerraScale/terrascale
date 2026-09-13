// <copyright file="MetricsDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The host metrics answer.</summary>
public sealed record MetricsResponse(
    int EngineGeneration,
    int EngineRestarts,
    string EngineStatus,
    int OpenStreams,
    long DroppedFrames,
    int TrackedSessions,
    int AuditRecords);
