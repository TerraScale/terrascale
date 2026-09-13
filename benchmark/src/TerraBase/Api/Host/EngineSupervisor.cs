// <copyright file="EngineSupervisor.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Collections.Concurrent;
using System.Diagnostics;
using TerraBase.Protocol;

/// <summary>
/// The engine-process supervision of the API host (SE-08, the SE-14
/// contract): the host owns the engine endpoint — a managed engine
/// process it spawns, watches, and restarts, or an attached engine
/// socket — and every engine frame rides a <see cref="TbepChannel"/>
/// from the SE-14 client (never a C ABI, a raw socket, or storage
/// access).
///
/// Supervision is truthful: the host's health is the health of its
/// engine channel, checked live on demand; an engine-process loss is
/// surfaced as a typed <c>engine-unavailable</c> refusal for every
/// caller, closes every open stream with a resume-able close frame,
/// ends (never orphans) every sync session it tracked, and — within the
/// bounded restart policy — brings a fresh engine generation back.
/// </summary>
public sealed partial class EngineSupervisor : IAsyncDisposable
{
    private readonly AuditLog audit;
    private readonly object gate = new();
    private readonly Dictionary<(string Tenant, string Principal), TbepChannel> identityChannels = [];
    private readonly SemaphoreSlim bindGate = new(1, 1);
    private readonly SemaphoreSlim recovery = new(1, 1);
    private readonly CancellationTokenSource stopped = new();
    private TbepChannel? adminChannel;
    private Process? engineProcess;
    private EngineStatus status = EngineStatus.Starting;
    private int generation;
    private int restarts;
    private bool disposed;

    /// <summary>Initializes the supervisor over one engine endpoint.</summary>
    /// <param name="options">The engine endpoint and restart policy.</param>
    /// <param name="audit">The lifecycle audit log.</param>
    public EngineSupervisor(EngineOptions options, AuditLog audit)
    {
        this.Options = options;
        this.audit = audit;
    }

    /// <summary>Raised when the engine generation is lost; streams and session state end with it.</summary>
    public event Action<int>? EngineLost;

    /// <summary>The engine endpoint and restart policy.</summary>
    public EngineOptions Options { get; }

    /// <summary>The current engine generation; every restart bumps it.</summary>
    public int Generation => Volatile.Read(ref this.generation);

    /// <summary>The completed restart count.</summary>
    public int Restarts => Volatile.Read(ref this.restarts);

    /// <summary>
    /// The managed engine process of the current generation, when this
    /// supervisor spawned one (the supervision surface: the test family
    /// drives real process loss on it).
    /// </summary>
    internal Process? EngineProcess => Volatile.Read(ref this.engineProcess);

    /// <summary>The supervisor's own lifecycle state.</summary>
    public EngineStatus Status
    {
        get
        {
            lock (this.gate)
            {
                return this.status;
            }
        }
    }

    /// <summary>
    /// The sessions this host opened on the engine, keyed by the
    /// engine's session id (tracked per generation, so nothing orphans).
    /// </summary>
    public ConcurrentDictionary<ulong, TrackedSession> Sessions { get; } = new();
}
