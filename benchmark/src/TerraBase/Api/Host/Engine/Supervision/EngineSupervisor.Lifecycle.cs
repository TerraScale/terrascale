// <copyright file="EngineSupervisor.Lifecycle.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Net.Sockets;
using TerraBase.Protocol;

public sealed partial class EngineSupervisor
{
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
            this.PublishServing();
            this.audit.Record(
                AuditKind.EngineStarted,
                tenant: this.Options.AdminTenant,
                principal: this.Options.AdminPrincipal,
                operation: "engine.start",
                outcome: AuditOutcome.Noted,
                detail: $"generation {this.Generation}, binding {this.Options.SocketPath}");
        }
        catch (Exception failure) when (failure is TbepException or IOException or SocketException or ObjectDisposedException or EngineUnavailableException)
        {
            // A startup failure owns the channels it opened. In
            // particular, the process-exit callback can race this path
            // while the recovery gate is held; do not call LoseEngineAsync
            // here or wait on the same gate recursively.
            this.SetStartupUnavailable();
            await this.CloseChannelsAsync().ConfigureAwait(false);
            throw failure is EngineUnavailableException
                ? failure
                : new EngineUnavailableException($"the engine endpoint could not start: {failure.Message}");
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
            // Recovery belongs to the supervisor, so an aborted health
            // request must not abandon the engine restart it discovered.
            await this.LoseEngineAsync($"the health check failed: {failure.Message}", cancellationToken: CancellationToken.None).ConfigureAwait(false);
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
            await this.LoseEngineAsync($"the shutdown request failed: {failure.Message}", cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw new EngineUnavailableException("the engine channel failed under the shutdown request");
        }

        this.audit.Record(
            AuditKind.EngineShutdown,
            tenant: this.Options.AdminTenant,
            principal: this.Options.AdminPrincipal,
            operation: "supervision.shutdown",
            outcome: AuditOutcome.Accepted,
            detail: $"state {answer.State.Variant}, in-flight {answer.InFlight}");
        await this.LoseEngineAsync("graceful shutdown", permanent: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return answer;
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

            // The restart budget is re-checked at recovery time, under
            // the recovery gate: a loss that queued while budget
            // remained must not restart past the policy when an earlier
            // loss already spent it — the honest answer then is the
            // truthful unavailable state, never a surprise generation.
            if (this.Options.ManagedBinary is null || this.restarts >= this.Options.MaxRestarts)
            {
                this.SetStatus(EngineStatus.Unavailable);
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
        this.bindGate.Dispose();
        this.recovery.Dispose();
        this.stopped.Dispose();
    }

    private void PublishServing()
    {
        lock (this.gate)
        {
            if (this.status != EngineStatus.Starting)
            {
                throw new EngineUnavailableException("the engine generation was lost during startup");
            }

            if (this.engineProcess is { HasExited: true })
            {
                this.status = EngineStatus.Unavailable;
                throw new EngineUnavailableException("the engine process exited during startup");
            }

            Interlocked.Increment(ref this.generation);
            this.status = EngineStatus.Serving;
        }
    }

    private void SetStartupUnavailable()
    {
        lock (this.gate)
        {
            if (this.status == EngineStatus.Starting)
            {
                this.status = EngineStatus.Unavailable;
            }
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

    private void SetStatus(EngineStatus value)
    {
        lock (this.gate)
        {
            this.status = value;
        }
    }
}
