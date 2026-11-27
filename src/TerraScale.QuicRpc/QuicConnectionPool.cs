using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.CompilerServices;
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

    public ValueTask ReleaseAsync()
    {
        Slot.EndUse();
        return QuicIo.DisposeStreamAsync(Stream);
    }

    public void ReleaseFireAndForget()
    {
        Slot.EndUse();
        QuicIo.DisposeStreamFireAndForget(Stream);
    }
}

internal readonly union StreamLeaseOutcome(StreamLease, QuicFault);

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class UnaryStreamSlot(PeerSlot slot, QuicConnection connection, QuicStream stream, TimeProvider timeProvider, CancellationToken shutdownToken)
{
    private ReusableCallScope _callScope = ReusableCallScope.Create(timeProvider, shutdownToken);

    public PeerSlot Slot { get; } = slot;

    public QuicConnection Connection { get; } = connection;

    public QuicStream Stream { get; } = stream;

    public QuicFrameReader Reader { get; } = new(QuicFrameReader.UnaryBufferCapacity);

    public long LastUsedTicks { get; set; }

    public bool IsHealthy => !Stream.WritesClosed.IsCompleted && !Stream.ReadsClosed.IsCompleted;

    public CallScope BeginCall(TimeSpan timeout, TimeProvider clock, CancellationToken caller) =>
        _callScope.Begin(timeout, clock, caller);

    public void EndCall(TimeProvider clock) => _callScope.End(clock);

    public void CloseAndDispose(QuicMeshMetrics metrics, bool abort)
    {
        _callScope.Dispose();
        Reader.Dispose();
        if (abort)
        {
            Stream.Abort(QuicAbortDirection.Both, QuicProtocol.StreamErrorCode);
        }

        QuicIo.DisposeStreamFireAndForget(Stream);
        metrics.StreamClosed(QuicSide.Client);
    }
}

internal readonly union UnaryStreamOutcome(UnaryStreamSlot, StreamLease, QuicFault);

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed record class ActiveConnection(QuicPeer Peer, QuicConnection Connection, bool SupportsPersistentUnary);

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class PeerSlot(QuicPeer peer, TimeProvider timeProvider)
{
    private const int MaxIdleUnaryStreams = 32;
    private static readonly TimeSpan UnaryStreamIdleTimeout = TimeSpan.FromSeconds(45);

    private readonly Lock _gate = new();
    private readonly Lock _unaryPoolGate = new();
    private readonly ConcurrentDictionary<string, OrderedPushChannel> _channels = new(StringComparer.Ordinal);
    private readonly UnaryStreamSlot?[] _idleUnaryStreams = new UnaryStreamSlot?[MaxIdleUnaryStreams];
    private QuicPeer _peer = peer;
    private ActiveConnection? _active;
    private ActiveConnection? _currentActive;
    private OrderedPushChannel? _lastChannel;
    private Task<QuicConnectionOpenOutcome>? _connecting;
    private int _idleUnaryTop;
    private int _uses;
    private long _idleSince;
    private long _lastConnectFailureTimestamp;
    private QuicFault? _lastConnectFault;
    private bool _everConnected;
    private int _closed;

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

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void Observe(QuicPeer latest)
    {
        lock (_gate)
        {
            if (_peer != latest)
            {
                _peer = latest;
                _lastConnectFailureTimestamp = 0;
                _lastConnectFault = null;
                Volatile.Write(ref _currentActive, null);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QuicConnection? Current() => Volatile.Read(ref _currentActive)?.Connection;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ActiveConnection? CurrentActive() => Volatile.Read(ref _currentActive);

    public UnaryStreamSlot? TryRentUnaryStream(QuicConnection connection, QuicMeshMetrics metrics)
    {
        while (true)
        {
            UnaryStreamSlot? candidate;
            lock (_unaryPoolGate)
            {
                if (_idleUnaryTop == 0)
                {
                    return null;
                }

                candidate = _idleUnaryStreams[--_idleUnaryTop];
                _idleUnaryStreams[_idleUnaryTop] = null;
            }

            if (candidate is null)
            {
                continue;
            }

            if (ReferenceEquals(candidate.Connection, connection)
                && candidate.IsHealthy
                && timeProvider.GetElapsedTime(candidate.LastUsedTicks) < UnaryStreamIdleTimeout)
            {
                return candidate;
            }

            candidate.CloseAndDispose(metrics, abort: false);
        }
    }

    public bool TryReturnUnaryStream(UnaryStreamSlot unarySlot, QuicMeshMetrics metrics)
    {
        if (Volatile.Read(ref _closed) == 0
            && ReferenceEquals(Current(), unarySlot.Connection)
            && unarySlot.IsHealthy)
        {
            unarySlot.LastUsedTicks = timeProvider.GetTimestamp();
            var stored = false;
            lock (_unaryPoolGate)
            {
                if (_idleUnaryTop < MaxIdleUnaryStreams)
                {
                    _idleUnaryStreams[_idleUnaryTop++] = unarySlot;
                    stored = true;
                }
            }

            if (stored)
            {
                if (Volatile.Read(ref _closed) != 0 || !ReferenceEquals(Current(), unarySlot.Connection))
                {
                    DrainIdleUnaryStreams(metrics);
                }

                return true;
            }
        }

        unarySlot.CloseAndDispose(metrics, abort: false);
        return false;
    }

    public void DrainIdleUnaryStreams(QuicMeshMetrics metrics)
    {
        while (true)
        {
            UnaryStreamSlot? slot;
            lock (_unaryPoolGate)
            {
                if (_idleUnaryTop == 0)
                {
                    return;
                }

                slot = _idleUnaryStreams[--_idleUnaryTop];
                _idleUnaryStreams[_idleUnaryTop] = null;
            }

            slot?.CloseAndDispose(metrics, abort: false);
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
            Volatile.Write(ref _currentActive, null);
            return active;
        }
    }

    public bool TryActivate(ActiveConnection active, out bool reconnect)
    {
        lock (_gate)
        {
            reconnect = _everConnected;
            if (_closed != 0)
            {
                return false;
            }

            _active = active;
            Volatile.Write(ref _currentActive, active.Peer == _peer ? active : null);
            Volatile.Write(ref _idleSince, timeProvider.GetTimestamp());
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
            Volatile.Write(ref _currentActive, null);
            return true;
        }
    }

    public ActiveConnection? Take()
    {
        lock (_gate)
        {
            var active = _active;
            _active = null;
            Volatile.Write(ref _currentActive, null);
            _lastConnectFailureTimestamp = 0;
            _lastConnectFault = null;
            return active;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginUse() => Interlocked.Increment(ref _uses);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndUse()
    {
        if (Interlocked.Decrement(ref _uses) == 0)
        {
            Volatile.Write(ref _idleSince, timeProvider.GetTimestamp());
        }
    }

    public ActiveConnection? TakeIdle()
    {
        lock (_gate)
        {
            if (_closed != 0 || Volatile.Read(ref _uses) != 0 || _active is not { } active
                || timeProvider.GetElapsedTime(Volatile.Read(ref _idleSince)) < QuicMeshLimits.PooledConnectionIdleTimeout)
            {
                return null;
            }

            Volatile.Write(ref _currentActive, null);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _uses) != 0)
            {
                if (active.Peer == _peer)
                {
                    Volatile.Write(ref _currentActive, active);
                }

                return null;
            }

            _active = null;
            _everConnected = false;
            return active;
        }
    }

    public bool TryJoinOrStartConnect(
        TimeSpan storageFormatMismatchBackoff,
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
            if (_closed != 0)
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
                && timeProvider.GetElapsedTime(_lastConnectFailureTimestamp) < (recentFault is StorageFormatMismatch
                    ? storageFormatMismatchBackoff
                    : QuicMeshLimits.ConnectFailureBackoff))
            {
                immediateFault = recentFault;
                return false;
            }

            leader = new TaskCompletionSource<QuicConnectionOpenOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _connecting = leader.Task;
            return true;
        }
    }

    public (QuicFault? Failure, bool LogFailure) FinishConnect(
        TaskCompletionSource<QuicConnectionOpenOutcome> leader, QuicPeer peer, QuicConnectionOpenOutcome outcome)
    {
        var failure = outcome switch { QuicConnection => null, QuicFault fault => fault };
        var logFailure = false;
        lock (_gate)
        {
            if (ReferenceEquals(_connecting, leader.Task))
            {
                _connecting = null;
            }

            if (failure is not null)
            {
                logFailure = failure is not StorageFormatMismatch || failure != _lastConnectFault;
                if (peer == _peer)
                {
                    _lastConnectFault = failure;
                    _lastConnectFailureTimestamp = timeProvider.GetTimestamp();
                }
            }
        }

        leader.TrySetResult(outcome);
        return (failure, logFailure);
    }

    public OrderedPushChannel? GetOrAddChannel(string key, QuicConnectionPool pool)
    {
        if (Volatile.Read(ref _closed) == 0)
        {
            var last = Volatile.Read(ref _lastChannel);
            if (last is not null
                && (ReferenceEquals(last.Key, key) || string.Equals(last.Key, key, StringComparison.Ordinal))
                && !last.IsEvicted)
            {
                return last;
            }

            if (_channels.TryGetValue(key, out var cached))
            {
                Volatile.Write(ref _lastChannel, cached);
                return cached;
            }
        }

        lock (_gate)
        {
            if (_closed != 0)
            {
                return null;
            }

            if (!_channels.TryGetValue(key, out var channel))
            {
                channel = new OrderedPushChannel(pool, this, key);
                _channels[key] = channel;
            }

            Volatile.Write(ref _lastChannel, channel);
            return channel;
        }
    }

    public void EvictIdleChannel(string key, OrderedPushChannel channel)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(key, out var existing) && ReferenceEquals(existing, channel) && channel.TryCompleteIdle())
            {
                _ = _channels.TryRemove(key, out _);
                _ = Interlocked.CompareExchange(ref _lastChannel, null, channel);
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
            Volatile.Write(ref _closed, 1);
            Volatile.Write(ref _currentActive, null);
            Volatile.Write(ref _lastChannel, null);
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
    private readonly QuicMeshOptions _options;
    private readonly QuicCertificateValidator _validator;
    private readonly QuicCertificateSource _certificates;
    private readonly ILogger _logger;
    private readonly ITimer _idleSweep;

    public QuicConnectionPool(QuicMeshOptions options, QuicCertificateValidator validator, QuicMeshMetrics metrics,
        TimeProvider timeProvider, ILogger logger, CancellationToken shutdownToken)
    {
        _options = options;
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
        if (_slots.TryGetValue(nodeId, out var slot))
        {
            slot.DrainIdleUnaryStreams(Metrics);
            if (slot.Take() is { } active)
            {
                Metrics.ConnectionClosed(QuicSide.Client);
                _ = QuicIo.DisposeConnectionAsync(active.Connection);
            }
        }
    }

    public OrderedPushChannel? GetOrderedChannel(PeerSlot slot, string channelKey) => slot.GetOrAddChannel(channelKey, this);

    public Task CompleteChannelsAsync()
    {
        List<Task> pumps = [];
        foreach (var slot in _slots.Values)
        {
            slot.DrainIdleUnaryStreams(Metrics);
            foreach (var channel in slot.Channels())
            {
                channel.Complete();
                pumps.Add(channel.Completion);
            }
        }

        return Task.WhenAll(pumps);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<UnaryStreamOutcome>))]
    public async ValueTask<UnaryStreamOutcome> AcquireUnaryStreamAsync(PeerSlot slot, CallScope scope, bool preferPooled)
    {
        slot.BeginUse();
        var leased = false;
        try
        {
            var active = slot.CurrentActive();
            if (active is not null)
            {
                if (preferPooled && active.SupportsPersistentUnary && slot.TryRentUnaryStream(active.Connection, Metrics) is { } rented)
                {
                    leased = true;
                    return rented;
                }

                var opened = await QuicIo.OpenStreamAsync(active.Connection, QuicStreamType.Bidirectional, scope);
                var result = opened switch
                {
                    QuicStream stream => WrapUnary(slot, active.Connection, active.SupportsPersistentUnary, stream),
                    QuicFault fault when QuicFaults.IsDeadConnection(fault) => await ReopenUnaryAsync(slot, active.Connection, scope),
                    QuicFault fault => (UnaryStreamOutcome)fault,
                };
                leased = result switch
                {
                    UnaryStreamSlot => true,
                    StreamLease => true,
                    QuicFault => false,
                };
                return result;
            }

            var connectOutcome = await GetConnectionAsync(slot, scope);
            var (connected, connectFault) = connectOutcome switch
            {
                QuicConnection resolved => (resolved, (QuicFault?)null),
                QuicFault fault => ((QuicConnection?)null, fault),
            };
            if (connectFault is not null)
            {
                return connectFault;
            }

            if (connected is null)
            {
                return new TransportShutdown("The QUIC client connect did not complete.");
            }

            var supportsPersistent = QuicStorageHandshake.SupportsPersistentUnary(connected.NegotiatedApplicationProtocol);
            if (preferPooled && supportsPersistent && slot.TryRentUnaryStream(connected, Metrics) is { } pooled)
            {
                leased = true;
                return pooled;
            }

            var firstOpen = await QuicIo.OpenStreamAsync(connected, QuicStreamType.Bidirectional, scope);
            var firstResult = firstOpen switch
            {
                QuicStream stream => WrapUnary(slot, connected, supportsPersistent, stream),
                QuicFault fault => (UnaryStreamOutcome)fault,
            };
            leased = firstResult switch
            {
                UnaryStreamSlot => true,
                StreamLease => true,
                QuicFault => false,
            };
            return firstResult;
        }
        finally
        {
            if (!leased)
            {
                slot.EndUse();
            }
        }
    }

    public void ReturnUnaryStream(UnaryStreamSlot unarySlot, bool healthy)
    {
        if (healthy)
        {
            _ = unarySlot.Slot.TryReturnUnaryStream(unarySlot, Metrics);
        }
        else
        {
            unarySlot.CloseAndDispose(Metrics, abort: true);
        }

        unarySlot.Slot.EndUse();
    }

    public void InvalidateIfDead(PeerSlot slot, QuicConnection connection, QuicFault fault)
    {
        if (QuicFaults.IsDeadConnection(fault))
        {
            Invalidate(slot, connection);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<StreamLeaseOutcome>))]
    public async ValueTask<StreamLeaseOutcome> OpenStreamAsync(PeerSlot slot, QuicStreamType type, CallScope scope)
    {
        slot.BeginUse();
        var leased = false;
        try
        {
            var connection = slot.Current();
            if (connection is null)
            {
                var connectOutcome = await GetConnectionAsync(slot, scope);
                var (connected, connectFault) = connectOutcome switch
                {
                    QuicConnection resolved => (resolved, (QuicFault?)null),
                    QuicFault fault => ((QuicConnection?)null, fault),
                };
                if (connectFault is not null)
                {
                    return connectFault;
                }

                if (connected is null)
                {
                    return new TransportShutdown("The QUIC client connect did not complete.");
                }

                connection = connected;
            }

            var opened = await QuicIo.OpenStreamAsync(connection, type, scope);
            StreamLeaseOutcome result = opened switch
            {
                QuicStream stream => new StreamLease(slot, connection, stream),
                QuicFault fault when QuicFaults.IsDeadConnection(fault) => await ReopenAsync(slot, connection, type, scope),
                QuicFault fault => fault,
            };
            leased = result switch { StreamLease => true, QuicFault => false };
            return result;
        }
        finally
        {
            if (!leased)
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

    private UnaryStreamOutcome WrapUnary(PeerSlot slot, QuicConnection connection, bool supportsPersistent, QuicStream stream)
    {
        if (supportsPersistent)
        {
            Metrics.StreamOpened(QuicSide.Client);
            return new UnaryStreamSlot(slot, connection, stream, TimeProvider, ShutdownToken);
        }

        return new StreamLease(slot, connection, stream);
    }

    private async ValueTask<UnaryStreamOutcome> ReopenUnaryAsync(PeerSlot slot, QuicConnection dead, CallScope scope)
    {
        Invalidate(slot, dead);
        var outcome = await GetConnectionAsync(slot, scope);
        var (connection, connectFault) = outcome switch
        {
            QuicConnection connected => (connected, (QuicFault?)null),
            QuicFault fault => ((QuicConnection?)null, fault),
        };
        if (connectFault is not null)
        {
            return connectFault;
        }

        if (connection is null)
        {
            return new TransportShutdown("The QUIC client connect did not complete.");
        }

        var opened = await QuicIo.OpenStreamAsync(connection, QuicStreamType.Bidirectional, scope);
        return opened switch
        {
            QuicStream stream => WrapUnary(slot, connection, QuicStorageHandshake.SupportsPersistentUnary(connection.NegotiatedApplicationProtocol), stream),
            QuicFault fault => fault,
        };
    }

    private void EvictIdleConnections(object? state)
    {
        foreach (var slot in _slots.Values)
        {
            if (slot.TakeIdle() is { } idle)
            {
                slot.DrainIdleUnaryStreams(Metrics);
                Metrics.ConnectionClosed(QuicSide.Client);
                _ = QuicIo.DisposeConnectionAsync(idle.Connection);
            }
        }
    }

    private static async ValueTask<StreamLeaseOutcome> OpenOnAsync(PeerSlot slot, QuicConnection connection, QuicStreamType type, CallScope scope)
    {
        var outcome = await QuicIo.OpenStreamAsync(connection, type, scope);
        return outcome switch
        {
            QuicStream stream => new StreamLease(slot, connection, stream),
            QuicFault fault => fault,
        };
    }

    private async ValueTask<StreamLeaseOutcome> ReopenAsync(PeerSlot slot, QuicConnection dead, QuicStreamType type, CallScope scope)
    {
        Invalidate(slot, dead);
        var outcome = await GetConnectionAsync(slot, scope);
        return outcome switch
        {
            QuicConnection connection => await OpenOnAsync(slot, connection, type, scope),
            QuicFault fault => fault,
        };
    }

    private void Invalidate(PeerSlot slot, QuicConnection connection)
    {
        if (slot.Clear(connection))
        {
            slot.DrainIdleUnaryStreams(Metrics);
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

        if (!slot.TryJoinOrStartConnect(_options.StorageFormatMismatchBackoff, out var current, out var inFlight, out var immediateFault, out var leader))
        {
            if (current is not null)
            {
                return current;
            }

            if (immediateFault is not null)
            {
                return immediateFault;
            }

            if (inFlight is not null)
            {
                return await WaitForConnectAsync(inFlight, scope);
            }

            return new TransportShutdown("The QUIC client connect did not start.");
        }

        if (leader is null)
        {
            return new TransportShutdown("The QUIC client connect did not start.");
        }

        var shared = ConnectAsLeaderAsync(slot, leader);
        return await WaitForConnectAsync(shared, scope);
    }

    private async Task<QuicConnectionOpenOutcome> ConnectAsLeaderAsync(PeerSlot slot, TaskCompletionSource<QuicConnectionOpenOutcome> leader)
    {
        QuicConnectionOpenOutcome result = new TransportShutdown("The QUIC client connect did not complete.");
        var peer = slot.Peer;
        (QuicFault? Failure, bool LogFailure) completed;
        try
        {
            if (slot.TakeStale() is { } stale)
            {
                slot.DrainIdleUnaryStreams(Metrics);
                Metrics.ConnectionClosed(QuicSide.Client);
                await QuicIo.DisposeConnectionAsync(stale.Connection);
            }

            peer = slot.Peer;
            using var dialScope = CallScope.Create(QuicMeshLimits.HandshakeTimeout, TimeProvider, ShutdownToken, CancellationToken.None);
            var outcome = await QuicIo.ConnectAsync(ConnectionOptions(peer), dialScope);
            result = outcome switch
            {
                QuicConnection connection => await NegotiateAndActivateAsync(slot, peer, connection, dialScope),
                QuicFault fault => fault,
            };
        }
        finally
        {
            completed = slot.FinishConnect(leader, peer, result);
        }

        if (completed.Failure is { } failure)
        {
            Metrics.Error(QuicSide.Client, failure);
            if (completed.LogFailure)
            {
                QuicRpcLog.ClientConnectFailed(_logger, peer.NodeId, peer.EndPoint, failure.Message);
            }
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

    private async ValueTask<QuicConnectionOpenOutcome> NegotiateAndActivateAsync(PeerSlot slot, QuicPeer peer, QuicConnection connection, CallScope scope)
    {
        if (_options.StorageFormatId is { } localFormat
            && QuicStorageHandshake.RequiresStorageHandshake(connection.NegotiatedApplicationProtocol)
            && await QuicStorageHandshake.OfferAsync(connection, localFormat, scope) is { } fault)
        {
            await QuicIo.DisposeConnectionAsync(connection);
            return fault;
        }

        return await ActivateAsync(slot, peer, connection);
    }

    private async ValueTask<QuicConnectionOpenOutcome> ActivateAsync(PeerSlot slot, QuicPeer peer, QuicConnection connection)
    {
        var supportsPersistent = QuicStorageHandshake.SupportsPersistentUnary(connection.NegotiatedApplicationProtocol);
        if (!slot.TryActivate(new ActiveConnection(peer, connection, supportsPersistent), out var reconnect))
        {
            await QuicIo.DisposeConnectionAsync(connection);
            return new TransportShutdown("The peer was removed from the QUIC client.");
        }

        Metrics.ConnectionOpened(QuicSide.Client, reconnect);
        QuicRpcLog.ClientConnected(_logger, peer.NodeId, peer.EndPoint, reconnect);
        return connection;
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
            ApplicationProtocols = QuicStorageHandshake.Protocols(_options.StorageFormatId),
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
        slot.DrainIdleUnaryStreams(Metrics);
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
