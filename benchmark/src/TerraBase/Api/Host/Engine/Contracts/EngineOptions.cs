// <copyright file="EngineOptions.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The engine endpoint and restart policy of the host.</summary>
public sealed record EngineOptions
{
    /// <summary>The UDS path the engine serves (PD-2026-08-26-11 D1: the required local binding).</summary>
    public required string SocketPath { get; init; }

    /// <summary>
    /// The engine binary the supervisor spawns, watches, and restarts,
    /// or <c>null</c> to attach to an externally managed engine at
    /// <see cref="SocketPath"/> (no respawn: truthfully unavailable on loss).
    /// </summary>
    public string? ManagedBinary { get; init; }

    /// <summary>The bounded restart count of the managed policy.</summary>
    public int MaxRestarts { get; init; } = 3;

    /// <summary>The delay before a managed restart.</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The bound within which the engine socket must serve.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The principal the host's own admin channel binds.</summary>
    public string AdminPrincipal { get; init; } = "terrabase-host";

    /// <summary>The tenant the host's own admin channel binds (the host's identity, never a customer tenant).</summary>
    public string AdminTenant { get; init; } = "terrabase-host";

    /// <summary>The loopback URL the HTTP surface binds (PD-2026-08-26-11: loopback/UDS only).</summary>
    public string ListenUrl { get; init; } = "http://127.0.0.1:8420";
}
