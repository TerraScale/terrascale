// <copyright file="EngineSupervisor.Channels.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Net.Sockets;
using TerraBase.Protocol;

public sealed partial class EngineSupervisor
{
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
                await this.LoseEngineAsync(failure.Message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (TbepException failure) when (failure.Code is null)
            {
                throw new EngineUnavailableException($"the engine channel failed under {operation}: {failure.Message}");
            }
        }
    }

    /// <summary>
    /// Borrows the caller's tenant-principal channel directly — for the
    /// subscription endpoints, whose sinks outlive the initiating call.
    /// The first use of a channel binds it under the bind gate:
    /// concurrent first callers queue there, and a late arrival adopts
    /// the first bind's outcome — it never races a second bind onto the
    /// same channel (the engine answers <c>AlreadyBound</c>) and never
    /// disposes the live binding another caller is already serving on.
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
            try
            {
                channel = this.EnsureChannel(tenant, principal);
            }
            catch (Exception failure) when (failure is TbepException or IOException or SocketException or ObjectDisposedException or System.ComponentModel.Win32Exception)
            {
                await this.LoseEngineAsync($"the channel could not connect: {failure.Message}", cancellationToken: CancellationToken.None).ConfigureAwait(false);
                throw new EngineUnavailableException($"the engine channel cannot serve the caller: {failure.Message}");
            }

            if (channel.Tenant == tenant && channel.Principal == principal)
            {
                return channel;
            }

            if (channel.Tenant is not null || channel.Principal is not null)
            {
                // A channel bound to another identity is never handed out:
                // discard it and start over (defense in depth).
                await this.RemoveChannelAsync(tenant, principal, channel).ConfigureAwait(false);
                continue;
            }

            await this.bindGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                TbepChannel current;
                try
                {
                    current = this.EnsureChannel(tenant, principal);
                }
                catch (Exception failure) when (failure is TbepException or IOException or SocketException or ObjectDisposedException or System.ComponentModel.Win32Exception)
                {
                    await this.LoseEngineAsync($"the channel could not connect: {failure.Message}", cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    throw new EngineUnavailableException($"the engine channel cannot serve the caller: {failure.Message}");
                }

                if (!ReferenceEquals(current, channel))
                {
                    // The channel this caller saw left the map while it
                    // waited (a failed bind discards it): whoever removed
                    // it owns its disposal — continue on the current one.
                    channel = current;
                }

                if (channel.Tenant == tenant && channel.Principal == principal)
                {
                    // The first bind completed while this caller waited:
                    // adopt its outcome.
                    return channel;
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
                    await this.RemoveChannelAsync(tenant, principal, channel).ConfigureAwait(false);
                    throw;
                }
                catch (Exception failure) when (failure is TbepException or IOException or SocketException or ObjectDisposedException)
                {
                    await this.RemoveChannelAsync(tenant, principal, channel).ConfigureAwait(false);
                    await this.LoseEngineAsync(failure.Message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
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
            finally
            {
                this.bindGate.Release();
            }
        }
    }

    /// <summary>Returns the caller identity's tracked live channel, connecting a fresh one when there is none.</summary>
    /// <param name="tenant">The backend-supplied tenant.</param>
    /// <param name="principal">The backend-supplied principal.</param>
    /// <returns>The channel to bind or serve on.</returns>
    private TbepChannel EnsureChannel(string tenant, string principal)
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                ObjectDisposedException.ThrowIf(this.disposed, nameof(EngineSupervisor));
            }

            var identity = (tenant, principal);
            if (!this.identityChannels.TryGetValue(identity, out var channel) || channel.IsClosed)
            {
                this.identityChannels.Remove(identity);
                if (channel is not null)
                {
                    _ = channel.DisposeAsync();
                }

                channel = TbepChannel.Connect(this.Options.SocketPath);
                this.identityChannels[identity] = channel;
            }

            return channel;
        }
    }

    private TbepChannel AdminChannel()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                throw new EngineUnavailableException("the engine endpoint is not established");
            }

            if (this.adminChannel is { IsClosed: false } channel)
            {
                return channel;
            }

            if (this.adminChannel is { IsClosed: true } closed)
            {
                _ = closed.DisposeAsync();
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
        catch (Exception failure) when (failure is TbepException or IOException or SocketException or ObjectDisposedException)
        {
            // Serving is published only after the supervisor's own
            // channel is bound. A connected but unbound channel cannot
            // represent a serving generation, even though health itself
            // does not require tenant scope.
            throw new EngineUnavailableException($"the admin channel could not bind: {failure.Message}");
        }

        if (channel.Tenant != this.Options.AdminTenant)
        {
            throw new EngineUnavailableException("the admin channel did not publish its tenant binding");
        }
    }

    private async Task RemoveChannelAsync(string tenant, string principal, TbepChannel channel)
    {
        lock (this.gate)
        {
            var identity = (tenant, principal);
            if (this.identityChannels.TryGetValue(identity, out var tracked) && ReferenceEquals(tracked, channel))
            {
                this.identityChannels.Remove(identity);
            }
        }

        await channel.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CloseChannelsAsync()
    {
        List<TbepChannel> closing;
        lock (this.gate)
        {
            closing = [.. this.identityChannels.Values];
            if (this.adminChannel is not null)
            {
                closing.Add(this.adminChannel);
            }

            this.identityChannels.Clear();
            this.adminChannel = null;
        }

        foreach (var channel in closing)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
