// <copyright file="EngineHealthSnapshot.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The truthful composite health of the host's engine generation.</summary>
/// <param name="Status">The supervisor's state.</param>
/// <param name="EngineState">The engine's own state (starting/serving/draining/stopped), when reachable.</param>
/// <param name="Sessions">The engine's session count, when reachable.</param>
/// <param name="Subscriptions">The engine's subscription count, when reachable.</param>
/// <param name="FramesServed">The engine's frames-served counter, when reachable.</param>
/// <param name="Generation">The engine generation.</param>
/// <param name="Restarts">The completed restart count.</param>
/// <param name="Detail">The machine-readable detail of an unhealthy host.</param>
public sealed record EngineHealthSnapshot(
    EngineStatus Status,
    string? EngineState,
    uint? Sessions,
    uint? Subscriptions,
    ulong? FramesServed,
    int Generation,
    int Restarts,
    string? Detail);
