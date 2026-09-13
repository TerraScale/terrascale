// <copyright file="HealthDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

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
