using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class StreamLease(PeerSlot slot, QuicConnection connection, QuicStream stream)
{
    public PeerSlot Slot { get; } = slot;

    public QuicConnection Connection { get; } = connection;

    public QuicStream Stream { get; } = stream;

    public Task ReleaseAsync()
    {
        Slot.EndUse();
        return QuicIo.DisposeStreamAsync(Stream);
    }
}

internal readonly union StreamLeaseOutcome(StreamLease, QuicFault);

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed record class ActiveConnection(QuicPeer Peer, QuicConnection Connection);

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class PeerSlot(QuicPeer peer, TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, OrderedPushChannel> _channels = new(StringComparer.Ordinal);
    private QuicPeer _peer = peer;
    private ActiveConnection? _active;
    private Task<QuicConnectionOpenOutcome>? _connecting;
    private int _uses;
    private long _idleSince;
    private long _lastConnectFailureTimestamp;
    private QuicFault? _lastConnectFault;
    private bool _everConnected;
    private bool _closed;

    public QuicPeer Peer
    {
        get
        {
            lock (_gate)
            {
                return _peer;
            }
        }
    }

    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    public void Observe(QuicPeer latest)
    {
        lock (_gate)
        {
            if (_peer != latest)
            {
                _peer = latest;
                _lastConnectFailureTimestamp = 0;
                _lastConnectFault = null;
            }
        }
    }

    public QuicConnection? Current()
    {
        lock (_gate)
        {
            return !_closed && _active is { } active && active.Peer == _peer ? active.Connection : null;
        }
    }

    public ActiveConnection? TakeStale()
    {
        lock (_gate)
        {
            if (_active is not { } active || active.Peer == _peer)
            {
                return null;
            }

            _active = null;
            return active;
        }
    }

    public bool TryActivate(ActiveConnection active, out bool reconnect)
    {
        lock (_gate)
        {
            reconnect = _everConnected;
            if (_closed)
            {
                return false;
            }

            _active = active;
            _idleSince = timeProvider.GetTimestamp();
            _everConnected = true;
            _lastConnectFailureTimestamp = 0;
            _lastConnectFault = null;
            return true;
        }
    }

    public bool Clear(QuicConnection connection)
    {
        lock (_gate)
        {
            if (_active is null || !ReferenceEquals(_active.Connection, connection))
            {
                return false;
            }

            _active = null;
            return true;
        }
    }

    public ActiveConnection? Take()
    {
        lock (_gate)
        {
            var active = _active;
            _active = null;
            _lastConnectFailureTimestamp = 0;
            _lastConnectFault = null;
            return active;
        }
    }

    public void BeginUse()
    {
        lock (_gate)
        {
            _uses++;
        }
    }

    public void EndUse()
    {
        lock (_gate)
        {
            _uses--;
            if (_uses == 0)
            {
                _idleSince = timeProvider.GetTimestamp();
            }
        }
    }

    public ActiveConnection? TakeIdle()
    {
        lock (_gate)
        {
            if (_closed || _uses != 0 || _active is not { } active
                || timeProvider.GetElapsedTime(_idleSince) < QuicMeshLimits.PooledConnectionIdleTimeout)
            {
                return null;
            }

            _active = null;
            return active;
        }
    }

    public bool TryJoinOrStartConnect(
        out QuicConnection? current,
        out Task<QuicConnectionOpenOutcome>? inFlight,
        out QuicFault? immediateFault,
        out TaskCompletionSource<QuicConnectionOpenOutcome>? leader)
    {
        lock (_gate)
        {
            current = null;
            inFlight = null;
            immediateFault = null;
            leader = null;
            if (_closed)
            {
                immediateFault = new TransportShutdown("The peer was removed from the QUIC client.");
                return false;
            }

            if (_active is { } active && active.Peer == _peer)
            {
                current = active.Connection;
                return false;
            }

            if (_connecting is { } connecting)
            {
                inFlight = connecting;
                return false;
            }

            if (_lastConnectFault is { } recentFault
                && _lastConnectFailureTimestamp != 0
                && timeProvider.GetElapsedTime(_lastConnectFailureTimestamp) < QuicMeshLimits.ConnectFailureBackoff)
            {
                immediateFault = recentFault;
                return false;
            }

            leader = new TaskCompletionSource<QuicConnectionOpenOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _connecting = leader.Task;
            return true;
        }
    }

    public void FinishConnect(TaskCompletionSource<QuicConnectionOpenOutcome> leader, QuicConnectionOpenOutcome outcome)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_connecting, leader.Task))
            {
                _connecting = null;
            }

            if (outcome switch { QuicConnection => null, QuicFault fault => fault } is { } failure)
            {
                _lastConnectFault = failure;
                _lastConnectFailureTimestamp = timeProvider.GetTimestamp();
            }
        }

        leader.TrySetResult(outcome);
    }

    public OrderedPushChannel? GetOrAddChannel(string key, QuicConnectionPool pool)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return null;
            }

            if (!_channels.TryGetValue(key, out var channel))
            {
                channel = new OrderedPushChannel(pool, this, key);
                _channels.Add(key, channel);
            }

            return channel;
        }
    }

    public void EvictIdleChannel(string key, OrderedPushChannel channel)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(key, out var existing) && ReferenceEquals(existing, channel) && channel.TryCompleteIdle())
            {
                _ = _channels.Remove(key);
            }
        }
    }

    public IReadOnlyList<OrderedPushChannel> Channels()
    {
        lock (_gate)
        {
            return [.. _channels.Values];
        }
    }

    public IReadOnlyList<OrderedPushChannel> Close()
    {
        lock (_gate)
        {
            _closed = true;
            OrderedPushChannel[] detached = [.. _channels.Values];
            _channels.Clear();
            return detached;
        }
    }
}

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class QuicConnectionPool
{
    private readonly ConcurrentDictionary<string, PeerSlot> _slots = new(StringComparer.Ordinal);
    private readonly QuicCertificateValidator _validator;
    private readonly QuicCertificateSource _certificates;
    private readonly ILogger _logger;
    private readonly ITimer _idleSweep;

    public QuicConnectionPool(QuicMeshOptions options, QuicCertificateValidator validator, QuicMeshMetrics metrics,
        TimeProvider timeProvider, ILogger logger, CancellationToken shutdownToken)
    {
        _validator = validator;
        _logger = logger;
        _certificates = new QuicCertificateSource(options, timeProvider);
        Metrics = metrics;
        TimeProvider = timeProvider;
        ShutdownToken = shutdownToken;
        _idleSweep = timeProvider.CreateTimer(EvictIdleConnections, null, QuicMeshLimits.IdleSweepInterval, QuicMeshLimits.IdleSweepInterval);
    }

    public QuicMeshMetrics Metrics { get; }

    public TimeProvider TimeProvider { get; }

    public CancellationToken ShutdownToken { get; }

    public void Upsert(QuicPeer peer) =>
        _slots.AddOrUpdate(
            peer.NodeId,
            static (_, state) => new PeerSlot(state.Peer, state.Clock),
            static (_, slot, state) =>
            {
                slot.Observe(state.Peer);
                return slot;
            },
            (Peer: peer, Clock: TimeProvider));

    public bool TryGet(string nodeId, [NotNullWhen(true)] out PeerSlot? slot) => _slots.TryGetValue(nodeId, out slot);

    public async ValueTask RemoveAsync(string nodeId)
    {
        if (_slots.TryRemove(nodeId, out var slot))
        {
            await CloseSlotAsync(slot);
        }
    }

    public void Reset(string nodeId)
    {
        if (_slots.TryGetValue(nodeId, out var slot) && slot.Take() is { } active)
        {
            Metrics.ConnectionClosed(QuicSide.Client);
            _ = QuicIo.DisposeConnectionAsync(active.Connection);
        }
    }

    public OrderedPushChannel? GetOrderedChannel(PeerSlot slot, string channelKey) => slot.GetOrAddChannel(channelKey, this);

    public Task CompleteChannelsAsync()
    {
        List<Task> pumps = [];
        foreach (var slot in _slots.Values)
        {
            foreach (var channel in slot.Channels())
            {
                channel.Complete();
                pumps.Add(channel.Completion);
            }
        }

        return Task.WhenAll(pumps);
    }

    public async ValueTask<StreamLeaseOutcome> OpenStreamAsync(PeerSlot slot, QuicStreamType type, CallScope scope)
    {
        slot.BeginUse();
        StreamLease? opened = null;
        try
        {
            var outcome = await GetConnectionAsync(slot, scope);
            StreamLeaseOutcome result = outcome switch
            {
                QuicConnection connection => await OpenOnAsync(slot, connection, type, true, scope),
                QuicFault fault => fault,
            };
            opened = result switch { StreamLease lease => lease, QuicFault => null };
            return result;
        }
        finally
        {
            if (opened is null)
            {
                slot.EndUse();
            }
        }
    }

    public void InvalidateIfDead(StreamLease lease, QuicFault fault)
    {
        if (QuicFaults.IsDeadConnection(fault))
        {
            Invalidate(lease.Slot, lease.Connection);
        }
    }

    public async ValueTask ShutdownAsync()
    {
        await _idleSweep.DisposeAsync();
        await Task.WhenAll(_slots.Values.Select(slot => CloseSlotAsync(slot).AsTask()));
    }

    private void EvictIdleConnections(object? state)
    {
        foreach (var slot in _slots.Values)
        {
            if (slot.TakeIdle() is { } idle)
            {
                Metrics.ConnectionClosed(QuicSide.Client);
                _ = QuicIo.DisposeConnectionAsync(idle.Connection);
            }
        }
    }

    private async ValueTask<StreamLeaseOutcome> OpenOnAsync(PeerSlot slot, QuicConnection connection, QuicStreamType type, bool reconnectIfDead, CallScope scope)
    {
        var outcome = await QuicIo.OpenStreamAsync(connection, type, scope);
        return outcome switch
        {
            QuicStream stream => new StreamLease(slot, connection, stream),
            QuicFault fault when reconnectIfDead && QuicFaults.IsDeadConnection(fault) => await ReopenAsync(slot, connection, type, scope),
            QuicFault fault => fault,
        };
    }

    private async ValueTask<StreamLeaseOutcome> ReopenAsync(PeerSlot slot, QuicConnection dead, QuicStreamType type, CallScope scope)
    {
        Invalidate(slot, dead);
        var outcome = await GetConnectionAsync(slot, scope);
        return outcome switch
        {
            QuicConnection connection => await OpenOnAsync(slot, connection, type, false, scope),
            QuicFault fault => fault,
        };
    }

    private void Invalidate(PeerSlot slot, QuicConnection connection)
    {
        if (slot.Clear(connection))
        {
            Metrics.ConnectionClosed(QuicSide.Client);
            _ = QuicIo.DisposeConnectionAsync(connection);
        }
    }

    private async ValueTask<QuicConnectionOpenOutcome> GetConnectionAsync(PeerSlot slot, CallScope scope)
    {
        if (slot.Current() is { } cached)
        {
            return cached;
        }

        if (!slot.TryJoinOrStartConnect(out var current, out var inFlight, out var immediateFault, out var leader))
        {
            if (current is not null)
            {
                return current;
            }

            if (immediateFault is not null)
            {
                return immediateFault;
            }

            return await WaitForConnectAsync(inFlight!, scope);
        }

        var shared = ConnectAsLeaderAsync(slot, leader!);
        return await WaitForConnectAsync(shared, scope);
    }

    private async Task<QuicConnectionOpenOutcome> ConnectAsLeaderAsync(PeerSlot slot, TaskCompletionSource<QuicConnectionOpenOutcome> leader)
    {
        QuicConnectionOpenOutcome result = new TransportShutdown("The QUIC client connect did not complete.");
        try
        {
            if (slot.TakeStale() is { } stale)
            {
                Metrics.ConnectionClosed(QuicSide.Client);
                await QuicIo.DisposeConnectionAsync(stale.Connection);
            }

            var peer = slot.Peer;
            using var dialScope = new CallScope(QuicMeshLimits.HandshakeTimeout, TimeProvider, ShutdownToken, CancellationToken.None);
            var outcome = await QuicIo.ConnectAsync(ConnectionOptions(peer), dialScope);
            result = outcome switch
            {
                QuicConnection connection => await ActivateAsync(slot, peer, connection),
                QuicFault fault => ConnectFailed(peer, fault),
            };
        }
        finally
        {
            slot.FinishConnect(leader, result);
        }

        return result;
    }

    private static async ValueTask<QuicConnectionOpenOutcome> WaitForConnectAsync(Task<QuicConnectionOpenOutcome> connectTask, CallScope scope)
    {
        var wait = connectTask.WaitAsync(scope.Token);
        await ((Task)wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (wait.IsCanceled && !scope.Caller.IsCancellationRequested)
        {
            return scope.Interrupted();
        }

        return await wait;
    }

    private async ValueTask<QuicConnectionOpenOutcome> ActivateAsync(PeerSlot slot, QuicPeer peer, QuicConnection connection)
    {
        if (!slot.TryActivate(new ActiveConnection(peer, connection), out var reconnect))
        {
            await QuicIo.DisposeConnectionAsync(connection);
            return new TransportShutdown("The peer was removed from the QUIC client.");
        }

        Metrics.ConnectionOpened(QuicSide.Client, reconnect);
        QuicRpcLog.ClientConnected(_logger, peer.NodeId, peer.EndPoint, reconnect);
        return connection;
    }

    private QuicFault ConnectFailed(QuicPeer peer, QuicFault fault)
    {
        Metrics.Error(QuicSide.Client, fault);
        QuicRpcLog.ClientConnectFailed(_logger, peer.NodeId, peer.EndPoint, fault.Message);
        return fault;
    }

    private QuicClientConnectionOptions ConnectionOptions(QuicPeer peer) => new()
    {
        RemoteEndPoint = peer.EndPoint,
        DefaultStreamErrorCode = QuicProtocol.StreamErrorCode,
        DefaultCloseErrorCode = QuicProtocol.CloseErrorCode,
        IdleTimeout = QuicMeshLimits.IdleTimeout,
        KeepAliveInterval = QuicMeshLimits.KeepAliveInterval,
        HandshakeTimeout = QuicMeshLimits.HandshakeTimeout,
        MaxInboundBidirectionalStreams = 0,
        MaxInboundUnidirectionalStreams = 0,
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [new SslApplicationProtocol(QuicProtocol.ApplicationProtocol)],
            TargetHost = peer.NodeId,
            ClientCertificateContext = _certificates.Current(),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                _validator.ValidateRemote(certificate, chain, QuicCertificateValidator.ServerAuthOid) is { } identity
                && string.Equals(identity.NodeId, peer.NodeId, StringComparison.Ordinal)
                && string.Equals(identity.Region, peer.Region, StringComparison.Ordinal),
        },
    };

    private async ValueTask CloseSlotAsync(PeerSlot slot)
    {
        await Task.WhenAll(slot.Close().Select(static channel => channel.DisposeAsync().AsTask()));
        if (slot.Take() is { } active)
        {
            var closeTask = QuicIo.CloseAsync(active.Connection, QuicProtocol.CloseErrorCode).AsTask();
            using var timeout = new CancellationTokenSource();
            var delay = Task.Delay(QuicMeshLimits.CloseTimeout, TimeProvider, timeout.Token);
            var winner = await Task.WhenAny(closeTask, delay);
            await timeout.CancelAsync();
            if (winner == closeTask)
            {
                Observe(await closeTask);
            }

            Metrics.ConnectionClosed(QuicSide.Client);
            _ = QuicIo.DisposeConnectionAsync(active.Connection);
        }
    }

    private void Observe(QuicConnectionCloseOutcome closed)
    {
        if (closed switch { QuicConnectionClosed => null, QuicFault fault => fault } is { } failure)
        {
            Metrics.Error(QuicSide.Client, failure);
        }
    }
}
