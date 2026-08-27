// <copyright file="EngineSupervisor.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
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
public sealed class EngineSupervisor : IAsyncDisposable
{
    private readonly AuditLog audit;
    private readonly object gate = new();
    private readonly Dictionary<string, TbepChannel> tenantChannels = [];
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

    /// <summary>
    /// Ensures the engine endpoint exists and the host is serving it:
    /// spawns (or waits for) the engine process, opens the admin
    /// channel, and binds it. Idempotent.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the engine is serving.</returns>
    /// <exception cref="EngineUnavailableException">The engine endpoint could not be established within the policy.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await this.recovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.Status == EngineStatus.Serving)
            {
                return;
            }

            this.SetStatus(EngineStatus.Starting);
            await this.EnsureEngineAsync(cancellationToken).ConfigureAwait(false);
            await this.OpenAdminChannelAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref this.generation);
            this.SetStatus(EngineStatus.Serving);
            this.audit.Record(
                AuditKind.EngineStarted,
                tenant: this.Options.AdminTenant,
                principal: this.Options.AdminPrincipal,
                operation: "engine.start",
                outcome: AuditOutcome.Noted,
                detail: $"generation {this.Generation}, binding {this.Options.SocketPath}");
        }
        finally
        {
            this.recovery.Release();
        }
    }

    /// <summary>Answers the truthful health snapshot: the live engine state or the typed loss.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The composite health; the status is never optimistic.</returns>
    public async Task<EngineHealthSnapshot> HealthAsync(CancellationToken cancellationToken = default)
    {
        if (this.Status is not (EngineStatus.Serving or EngineStatus.Starting))
        {
            return new EngineHealthSnapshot(
                EngineStatus.Unavailable,
                EngineState: null,
                Sessions: null,
                Subscriptions: null,
                FramesServed: null,
                Generation: this.Generation,
                Restarts: this.restarts,
                Detail: "the engine endpoint is not established");
        }

        try
        {
            var channel = this.AdminChannel();
            var health = await channel.SupervisionHealthAsync(cancellationToken).ConfigureAwait(false);
            return new EngineHealthSnapshot(
                EngineStatus.Serving,
                EngineState: health.State.Variant,
                Sessions: health.Sessions,
                Subscriptions: health.Subscriptions,
                FramesServed: health.FramesServed,
                Generation: this.Generation,
                Restarts: this.restarts,
                Detail: null);
        }
        catch (Exception failure) when (failure is TbepException or IOException or SocketException or ObjectDisposedException or EngineUnavailableException)
        {
            await this.LoseEngineAsync($"the health check failed: {failure.Message}").ConfigureAwait(false);
            return new EngineHealthSnapshot(
                EngineStatus.Unavailable,
                EngineState: null,
                Sessions: null,
                Subscriptions: null,
                FramesServed: null,
                Generation: this.Generation,
                Restarts: this.restarts,
                Detail: "the engine channel failed under the health check");
        }
    }

    /// <summary>Begins the engine's graceful shutdown (the supervision contract) and ends this host's engine generation.</summary>
    /// <param name="reason">The shutdown reason.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The engine's shutdown answer.</returns>
    /// <exception cref="EngineUnavailableException">The engine channel is down.</exception>
    public async Task<SupervisionShutdownResponse> ShutdownAsync(ShutdownReason reason, CancellationToken cancellationToken = default)
    {
        var channel = this.AdminChannel();
        SupervisionShutdownResponse answer;
        try
        {
            answer = await channel.SupervisionShutdownAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is TbepException or IOException or SocketException or ObjectDisposedException or EngineUnavailableException)
        {
            await this.LoseEngineAsync($"the shutdown request failed: {failure.Message}").ConfigureAwait(false);
            throw new EngineUnavailableException("the engine channel failed under the shutdown request");
        }

        this.audit.Record(
            AuditKind.EngineShutdown,
            tenant: this.Options.AdminTenant,
            principal: this.Options.AdminPrincipal,
            operation: "supervision.shutdown",
            outcome: AuditOutcome.Accepted,
            detail: $"state {answer.State.Variant}, in-flight {answer.InFlight}");
        await this.LoseEngineAsync("graceful shutdown", permanent: true).ConfigureAwait(false);
        return answer;
    }

    /// <summary>
    /// Serves one typed engine call on the caller's tenant channel,
    /// binding it from the backend-supplied context on first use. A
    /// failed (closed) channel is retried once on a fresh channel of the
    /// current engine generation; a typed engine refusal propagates as-is.
    /// </summary>
    /// <typeparam name="T">The answer type.</typeparam>
    /// <param name="tenant">The backend-supplied tenant the engine enforces.</param>
    /// <param name="principal">The backend-supplied principal.</param>
    /// <param name="operation">The operation name, for the audit trail.</param>
    /// <param name="call">The typed call on the channel.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The call's answer.</returns>
    /// <exception cref="EngineUnavailableException">The engine channel failed on both attempts.</exception>
    /// <exception cref="TbepException">The engine's typed refusal.</exception>
    public async Task<T> CallAsync<T>(
        string tenant,
        string principal,
        string operation,
        Func<TbepChannel, Task<T>> call,
        CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        while (true)
        {
            TbepChannel channel;
            try
            {
                channel = await this.ChannelForAsync(tenant, principal, cancellationToken).ConfigureAwait(false);
            }
            catch (EngineUnavailableException)
            {
                throw;
            }
            catch (TbepException failure)
            {
                throw new EngineUnavailableException($"the engine channel cannot serve {operation}: {failure.Message}");
            }

            try
            {
                return await call(channel).ConfigureAwait(false);
            }
            catch (TbepException failure) when (failure.Code is null && attempt++ == 0)
            {
                // A transport failure (the channel died under the call):
                // surface the loss, then retry once on a fresh channel. A
                // typed refusal (Code set) never retries.
                await this.LoseEngineAsync(failure.Message).ConfigureAwait(false);
            }
            catch (TbepException failure) when (failure.Code is null)
            {
                throw new EngineUnavailableException($"the engine channel failed under {operation}: {failure.Message}");
            }
        }
    }

    /// <summary>
    /// Borrows the caller's tenant channel directly — for the
    /// subscription endpoints, whose sinks outlive the initiating call.
    /// </summary>
    /// <param name="tenant">The backend-supplied tenant.</param>
    /// <param name="principal">The backend-supplied principal.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The bound channel.</returns>
    /// <exception cref="EngineUnavailableException">The engine endpoint is down.</exception>
    /// <exception cref="TbepException">The engine refused the channel binding.</exception>
    public async Task<TbepChannel> ChannelForAsync(string tenant, string principal, CancellationToken cancellationToken = default)
    {
        if (this.Status is not (EngineStatus.Serving or EngineStatus.Starting))
        {
            throw new EngineUnavailableException("the engine endpoint is not established");
        }

        while (true)
        {
            TbepChannel channel;
            lock (this.gate)
            {
                if (this.disposed)
                {
                    throw new ObjectDisposedException(nameof(EngineSupervisor));
                }

                if (!this.tenantChannels.TryGetValue(tenant, out channel!) || channel.IsClosed)
                {
                    this.tenantChannels.Remove(tenant);
                    channel = TbepChannel.Connect(this.Options.SocketPath);
                    this.tenantChannels[tenant] = channel;
                }
            }

            if (channel.Tenant == tenant)
            {
                return channel;
            }

            if (channel.Tenant is not null)
            {
                // A channel bound to another tenant is never handed out:
                // discard it and start over (defense in depth).
                await this.RemoveChannelAsync(tenant, channel).ConfigureAwait(false);
                continue;
            }

            try
            {
                await channel.BindChannelAsync(principal, tenant, cancellationToken).ConfigureAwait(false);
            }
            catch (TbepException failure) when (failure.Code is not null)
            {
                // The engine refused the binding itself (a draining
                // engine refuses every new binding): the typed refusal
                // propagates and the unbound channel is discarded — the
                // fail-closed binding rule of PD-2026-08-26-11 D1.
                await this.RemoveChannelAsync(tenant, channel).ConfigureAwait(false);
                throw;
            }
            catch (Exception failure) when (failure is TbepException or IOException or SocketException or ObjectDisposedException)
            {
                await this.RemoveChannelAsync(tenant, channel).ConfigureAwait(false);
                await this.LoseEngineAsync(failure.Message).ConfigureAwait(false);
                continue;
            }

            this.audit.Record(
                AuditKind.ChannelBound,
                tenant,
                principal,
                operation: "channel.bind",
                outcome: AuditOutcome.Accepted,
                detail: $"generation {this.Generation}");
            return channel;
        }
    }

    /// <summary>Ends this host's engine generation: typed loss, closed streams, no orphaned state.</summary>
    /// <param name="reason">The machine-readable loss reason.</param>
    /// <param name="permanent">True when no restart is attempted (a deliberate shutdown).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the generation is closed and the bounded recovery ran.</returns>
    public async Task LoseEngineAsync(string reason, bool permanent = false, CancellationToken cancellationToken = default)
    {
        lock (this.gate)
        {
            if (this.status is not (EngineStatus.Serving or EngineStatus.Starting))
            {
                return;
            }

            this.status = EngineStatus.Unavailable;
        }

        this.audit.Record(
            AuditKind.EngineLost,
            tenant: null,
            principal: null,
            operation: "engine.lost",
            outcome: AuditOutcome.Failed,
            detail: reason);
        await this.CloseChannelsAsync().ConfigureAwait(false);
        this.EndTrackedState();
        this.EngineLost?.Invoke(this.Generation);

        if (permanent || this.Options.ManagedBinary is null || this.restarts >= this.Options.MaxRestarts)
        {
            // A deliberate shutdown, an attached engine (the supervisor
            // neither spawns nor restarts it), or a managed engine beyond
            // the bounded restart policy: truthfully unavailable.
            return;
        }

        await this.recovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.Status != EngineStatus.Unavailable)
            {
                return;
            }

            this.SetStatus(EngineStatus.Starting);
            await Task.Delay(this.Options.RestartDelay, cancellationToken).ConfigureAwait(false);
            await this.EnsureEngineAsync(cancellationToken).ConfigureAwait(false);
            await this.OpenAdminChannelAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref this.restarts);
            Interlocked.Increment(ref this.generation);
            this.SetStatus(EngineStatus.Serving);
            this.audit.Record(
                AuditKind.EngineStarted,
                tenant: this.Options.AdminTenant,
                principal: this.Options.AdminPrincipal,
                operation: "engine.restart",
                outcome: AuditOutcome.Noted,
                detail: $"generation {this.Generation}, restart {this.restarts}");
        }
        catch (OperationCanceledException)
        {
            this.SetStatus(EngineStatus.Unavailable);
            throw;
        }
        catch (Exception failure)
        {
            this.SetStatus(EngineStatus.Unavailable);
            this.audit.Record(
                AuditKind.EngineLost,
                tenant: null,
                principal: null,
                operation: "engine.restart",
                outcome: AuditOutcome.Failed,
                detail: failure.Message);
        }
        finally
        {
            this.recovery.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.status = EngineStatus.Stopped;
        }

        this.stopped.Cancel();
        if (this.engineProcess is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process had already exited; disposal continues.
            }

            process.Dispose();
        }

        await this.CloseChannelsAsync().ConfigureAwait(false);
        this.Sessions.Clear();
        this.recovery.Dispose();
        this.stopped.Dispose();
    }

    private TbepChannel AdminChannel()
    {
        lock (this.gate)
        {
            if (this.adminChannel is { IsClosed: false } channel)
            {
                return channel;
            }

            if (this.disposed)
            {
                throw new EngineUnavailableException("the engine endpoint is not established");
            }

            this.adminChannel = TbepChannel.Connect(this.Options.SocketPath);
            return this.adminChannel;
        }
    }

    private async Task OpenAdminChannelAsync(CancellationToken cancellationToken)
    {
        var channel = this.AdminChannel();
        if (channel.Tenant is not null)
        {
            return;
        }

        try
        {
            await channel.BindChannelAsync(this.Options.AdminPrincipal, this.Options.AdminTenant, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TbepException)
        {
            // The admin channel stays unbound: health and admin status
            // reads need no tenant scope, and the next bind retries.
        }
    }

    private async Task RemoveChannelAsync(string tenant, TbepChannel channel)
    {
        lock (this.gate)
        {
            if (this.tenantChannels.TryGetValue(tenant, out var tracked) && ReferenceEquals(tracked, channel))
            {
                this.tenantChannels.Remove(tenant);
            }
        }

        await channel.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CloseChannelsAsync()
    {
        List<TbepChannel> closing;
        lock (this.gate)
        {
            closing = [.. this.tenantChannels.Values];
            if (this.adminChannel is not null)
            {
                closing.Add(this.adminChannel);
            }

            this.tenantChannels.Clear();
            this.adminChannel = null;
        }

        foreach (var channel in closing)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void EndTrackedState()
    {
        // No orphaned state: every session this host opened on the lost
        // generation ends here, truthfully — a later frame naming one of
        // them is answered as ended, never silently re-served.
        foreach (var session in this.Sessions.Values)
        {
            this.audit.Record(
                AuditKind.SessionEndedByRestart,
                tenant: session.Tenant,
                principal: null,
                operation: "sync.session",
                outcome: AuditOutcome.Noted,
                detail: $"session {session.SessionId} ended with engine generation {session.Generation}");
        }

        this.Sessions.Clear();
    }

    private async Task EnsureEngineAsync(CancellationToken cancellationToken)
    {
        if (this.Options.ManagedBinary is null)
        {
            // Attached mode: the engine socket must exist; the supervisor
            // neither spawns nor restarts it, and reports truthfully.
            await WaitForSocketAsync(this.Options.SocketPath, this.Options.StartTimeout, cancellationToken).ConfigureAwait(false);
            return;
        }

        var current = Volatile.Read(ref this.engineProcess);
        if (current is { HasExited: false })
        {
            return;
        }

        var instance = checked(this.Generation + 1);
        var start = new ProcessStartInfo
        {
            FileName = this.Options.ManagedBinary,
            Arguments = $"\"{this.Options.SocketPath}\" {instance}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        var process = Process.Start(start) ?? throw new EngineUnavailableException("the engine process did not start");
        Volatile.Write(ref this.engineProcess, process);
        // The pipes drain in the background so a chatty engine can never
        // block on a full pipe; the supervisor reads health through TBEP.
        _ = process.StandardOutput.ReadToEndAsync(this.stopped.Token);
        _ = process.StandardError.ReadToEndAsync(this.stopped.Token);
        // Engine-process loss is detected the moment it happens — the
        // truthful loss handling (typed refusals, closed streams, ended
        // sessions) runs before any caller has to discover it.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = this.LoseEngineAsync("the engine process exited");

        await WaitForSocketAsync(this.Options.SocketPath, this.Options.StartTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForSocketAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
                    probe.Dispose();
                    return;
                }
                catch (SocketException)
                {
                    // The engine created the socket but is not listening
                    // yet; keep waiting within the bound.
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }

        throw new EngineUnavailableException($"the engine socket {path} did not serve within the start bound");
    }

    private void SetStatus(EngineStatus value)
    {
        lock (this.gate)
        {
            this.status = value;
        }
    }
}

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

/// <summary>The supervisor's lifecycle states.</summary>
public enum EngineStatus
{
    /// <summary>The engine endpoint is being established.</summary>
    Starting,

    /// <summary>The engine channel is serving.</summary>
    Serving,

    /// <summary>The engine endpoint is lost (or was never established); every caller hears the typed refusal.</summary>
    Unavailable,

    /// <summary>The host stopped the engine deliberately.</summary>
    Stopped,
}

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

/// <summary>One sync session this host tracked on the engine: the tenant, the scope, the generation.</summary>
/// <param name="SessionId">The engine-assigned session id.</param>
/// <param name="Tenant">The tenant whose channel opened it.</param>
/// <param name="Collection">The collection scope.</param>
/// <param name="Generation">The engine generation that owns it.</param>
public sealed record TrackedSession(ulong SessionId, string Tenant, string Collection, int Generation);

/// <summary>The typed, clean refusal every caller hears while the engine generation is down.</summary>
public sealed class EngineUnavailableException(string message) : Exception(message);
