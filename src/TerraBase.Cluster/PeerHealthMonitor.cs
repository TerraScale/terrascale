using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed class PeerHealthMonitor(ClusterOptions options, ClusterTopology topology, QuicRpcClient client, ClusterDatabases databases, TimeProvider timeProvider, ILogger<PeerHealthMonitor> logger)
{
    private readonly long _instanceId = Random.Shared.NextInt64(1, long.MaxValue);
    private readonly ConcurrentDictionary<string, PeerState> _peers = new(
        options.Peers.Select(static peer => KeyValuePair.Create(peer.NodeId, new PeerState(peer))),
        StringComparer.Ordinal);

    public IReadOnlyList<PeerHealth> Snapshot =>
        [.. _peers.Values.Select(static state => state.Health).OrderBy(static health => health.Peer.NodeId, StringComparer.Ordinal)];

    public PeerStatus StatusOf(string nodeId) => _peers.TryGetValue(nodeId, out var state) ? state.Health.Status : PeerStatus.Unreachable;

    public bool IsReachable(string nodeId) => StatusOf(nodeId) != PeerStatus.Unreachable;

    public void ObserveReachable(string nodeId)
    {
        if (!_peers.TryGetValue(nodeId, out var state))
        {
            return;
        }

        var (_, previous, next) = state.ObserveInbound(0, 0, timeProvider.GetUtcNow());
        if (previous == next)
        {
            return;
        }

        ClusterLog.PeerStatusChanged(logger, state.Info.NodeId, state.Info.Region, previous, next);
        client.ResetPeer(nodeId);
        Recovered(previous, next);
    }

    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapUnary<PingMessage, PongMessage>(QuicMessageType.Ping, HandlePingAsync);
    }

    public async Task RegisterPeersAsync(CancellationToken cancellationToken)
    {
        foreach (var state in _peers.Values)
        {
            await RegisterAsync(state, cancellationToken);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.PingInterval, timeProvider);
        try
        {
            do
            {
                await Parallel.ForEachAsync(
                    _peers.Values,
                    new ParallelOptions { MaxDegreeOfParallelism = options.MaxReplicationParallelism, CancellationToken = cancellationToken },
                    PingAsync);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    public async ValueTask<PeerHealth?> PingAsync(string nodeId, CancellationToken cancellationToken)
    {
        if (!_peers.TryGetValue(nodeId, out var state))
        {
            return null;
        }

        await PingAsync(state, cancellationToken);
        return state.Health;
    }

    private async ValueTask PingAsync(PeerState state, CancellationToken cancellationToken)
    {
        if (!state.Registered)
        {
            await RegisterAsync(state, cancellationToken);
            if (!state.Registered)
            {
                _ = Record(state, success: false, TimeSpan.Zero, refused: false, instanceId: 0, observedAt: 0);
                return;
            }
        }

        var started = timeProvider.GetTimestamp();
        var response = await client.CallAsync<PingMessage, PongMessage>(
            state.Info.NodeId,
            QuicMessageType.Ping,
            new PingMessage(options.NodeId, options.Region, started, _instanceId),
            options.PingTimeout,
            cancellationToken);
        var (success, timedOut, refused, remoteInstanceId) = response switch
        {
            PongMessage pong when string.Equals(pong.NodeId, state.Info.NodeId, StringComparison.Ordinal)
                && string.Equals(pong.Region, state.Info.Region, StringComparison.Ordinal) => (true, false, false, pong.InstanceId),
            QuicUnavailable { Code: QuicRemoteError.StorageFormatMismatch } => (false, false, true, 0L),
            QuicTimeout => (false, true, false, 0L),
            PongMessage or QuicUnavailable or QuicRemoteError => (false, false, false, 0L),
        };
        if (timedOut)
        {
            client.ResetPeer(state.Info.NodeId);
        }

        var (restarted, previous, next, failures, outboundRecovered) = Record(state, success, timeProvider.GetElapsedTime(started), refused, remoteInstanceId, started);
        if (restarted || outboundRecovered || (previous == PeerStatus.Unreachable && next == PeerStatus.Healthy))
        {
            databases.OnPeerRecovered();
        }

        if (!success && !refused && failures >= options.UnreachableAfterFailures && failures % options.UnreachableAfterFailures == 0 && !IPAddress.TryParse(state.Info.Host, out _))
        {
            await RefreshAddressAsync(state, cancellationToken);
        }
    }

    private (bool Restarted, PeerStatus Previous, PeerStatus Next, int Failures, bool OutboundRecovered) Record(PeerState state, bool success, TimeSpan roundTrip, bool refused, long instanceId, long observedAt)
    {
        var recorded = state.Record(success, roundTrip, timeProvider.GetUtcNow(), refused ? 1 : options.UnreachableAfterFailures, instanceId, observedAt);
        if (recorded.Previous != recorded.Next)
        {
            ClusterLog.PeerStatusChanged(logger, state.Info.NodeId, state.Info.Region, recorded.Previous, recorded.Next);
        }

        return recorded;
    }

    private void Recovered(PeerStatus previous, PeerStatus next)
    {
        if (previous == PeerStatus.Unreachable && next != PeerStatus.Unreachable)
        {
            databases.OnPeerRecovered();
        }
    }

    private async ValueTask RegisterAsync(PeerState state, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(state.Info, cancellationToken) is not { } address)
        {
            ClusterLog.PeerUnresolved(logger, state.Info.NodeId);
            return;
        }

        state.SetResolvedAddress(address);
        client.UpsertPeer(new QuicPeer(state.Info.NodeId, state.Info.Region, new IPEndPoint(address, state.Info.QuicPort)));
        state.Registered = true;
    }

    private async ValueTask RefreshAddressAsync(PeerState state, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(state.Info, cancellationToken) is not { } address)
        {
            return;
        }

        if (state.SetResolvedAddress(address))
        {
            client.UpsertPeer(new QuicPeer(state.Info.NodeId, state.Info.Region, new IPEndPoint(address, state.Info.QuicPort)));
            client.ResetPeer(state.Info.NodeId);
        }
    }

    private static async ValueTask<IPAddress?> ResolveAsync(PeerNodeInfo peer, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(peer.Host, out var literal))
        {
            return literal;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(peer.Host, cancellationToken);
            return addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private ValueTask<QuicHandlerOutcome<PongMessage>> HandlePingAsync(QuicCallContext context, PingMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context, message.NodeId)
            || !string.Equals(context.RemoteRegion, message.Region, StringComparison.Ordinal)
            || !_peers.TryGetValue(context.RemoteNodeId, out var state))
        {
            return ValueTask.FromResult<QuicHandlerOutcome<PongMessage>>(ClusterTopology.PeerForbidden());
        }

        var (restarted, previous, next) = state.ObserveInbound(message.InstanceId, timeProvider.GetTimestamp(), timeProvider.GetUtcNow());
        if (previous != next)
        {
            ClusterLog.PeerStatusChanged(logger, state.Info.NodeId, state.Info.Region, previous, next);
        }

        if (restarted || previous != next)
        {
            client.ResetPeer(context.RemoteNodeId);
        }

        if (restarted)
        {
            databases.OnPeerRecovered();
        }
        else
        {
            Recovered(previous, next);
        }

        return ValueTask.FromResult<QuicHandlerOutcome<PongMessage>>(new PongMessage(options.NodeId, options.Region, message.SentAtTicks, _instanceId));
    }

    private sealed class PeerState(PeerNodeInfo info)
    {
        private readonly Lock _gate = new();
        private PeerHealth _health = new(info, PeerStatus.Suspect, TimeSpan.Zero, DateTimeOffset.MinValue);
        private IPAddress? _resolvedAddress;
        private long _instanceId;
        private long _instanceObservedAt;
        private int _failures;
        private bool _inboundPromoted;

        public PeerNodeInfo Info { get; } = info;

        public bool Registered { get; set; }

        public bool SetResolvedAddress(IPAddress address)
        {
            lock (_gate)
            {
                if (_resolvedAddress is { } previous && previous.Equals(address))
                {
                    return false;
                }

                _resolvedAddress = address;
                return true;
            }
        }

        public PeerHealth Health
        {
            get
            {
                lock (_gate)
                {
                    return _health;
                }
            }
        }

        public (bool Restarted, PeerStatus Previous, PeerStatus Next, int Failures, bool OutboundRecovered) Record(
            bool success, TimeSpan roundTrip, DateTimeOffset now, int unreachableAfter, long instanceId, long observedAt)
        {
            lock (_gate)
            {
                var previous = _health.Status;
                if (instanceId != 0 && _instanceId != 0 && instanceId != _instanceId && observedAt < _instanceObservedAt)
                {
                    return (false, previous, previous, _failures, false);
                }

                var restarted = AcceptInstance(instanceId, observedAt);
                _failures = success ? 0 : _failures + 1;
                var outboundRecovered = success && _inboundPromoted;
                _inboundPromoted &= !success;
                var status = success ? PeerStatus.Healthy : _failures >= unreachableAfter ? PeerStatus.Unreachable : PeerStatus.Suspect;
                _health = success
                    ? new PeerHealth(Info, status, roundTrip, now)
                    : _health with { Status = status };
                return (restarted, previous, status, _failures, outboundRecovered);
            }
        }

        public (bool Restarted, PeerStatus Previous, PeerStatus Next) ObserveInbound(long instanceId, long observedAt, DateTimeOffset now)
        {
            lock (_gate)
            {
                var restarted = AcceptInstance(instanceId, observedAt);
                var previous = _health.Status;
                if (previous == PeerStatus.Unreachable && !_inboundPromoted)
                {
                    _failures = 0;
                    _inboundPromoted = true;
                    _health = _health with { Status = PeerStatus.Suspect, LastSeenUtc = now };
                }
                else
                {
                    _health = _health with { LastSeenUtc = now };
                }

                return (restarted, previous, _health.Status);
            }
        }

        private bool AcceptInstance(long instanceId, long observedAt)
        {
            if (instanceId == 0 || observedAt < _instanceObservedAt)
            {
                return false;
            }

            var restarted = _instanceId != 0 && _instanceId != instanceId;
            _instanceId = instanceId;
            _instanceObservedAt = observedAt;
            _inboundPromoted &= !restarted;
            return restarted;
        }
    }
}
