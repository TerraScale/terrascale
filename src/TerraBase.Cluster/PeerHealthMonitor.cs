using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed class PeerHealthMonitor(ClusterOptions options, ClusterTopology topology, QuicRpcClient client, TimeProvider timeProvider, ILogger<PeerHealthMonitor> logger)
{
    private readonly long _instanceId = Random.Shared.NextInt64(1, long.MaxValue);
    private readonly ConcurrentDictionary<string, PeerState> _peers = new(
        options.Peers.Select(static peer => KeyValuePair.Create(peer.NodeId, new PeerState(peer))),
        StringComparer.Ordinal);

    public IReadOnlyList<PeerHealth> Snapshot =>
        [.. _peers.Values.Select(static state => state.Health).OrderBy(static health => health.Peer.NodeId, StringComparer.Ordinal)];

    public PeerStatus StatusOf(string nodeId) => _peers.TryGetValue(nodeId, out var state) ? state.Health.Status : PeerStatus.Unreachable;

    public bool IsReachable(string nodeId) => StatusOf(nodeId) != PeerStatus.Unreachable;

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
                Record(state, success: false, TimeSpan.Zero);
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
        var (success, timedOut, remoteInstanceId) = response switch
        {
            PongMessage pong when string.Equals(pong.NodeId, state.Info.NodeId, StringComparison.Ordinal)
                && string.Equals(pong.Region, state.Info.Region, StringComparison.Ordinal) => (true, false, pong.InstanceId),
            PongMessage => (false, false, 0L),
            QuicTimeout => (false, true, 0L),
            QuicUnavailable or QuicRemoteError => (false, false, 0L),
        };
        if (remoteInstanceId != 0)
        {
            state.ObserveInstance(remoteInstanceId);
        }

        if (timedOut)
        {
            client.ResetPeer(state.Info.NodeId);
        }

        var failures = Record(state, success, timeProvider.GetElapsedTime(started));
        if (!success && failures >= options.UnreachableAfterFailures && failures % options.UnreachableAfterFailures == 0 && !IPAddress.TryParse(state.Info.Host, out _))
        {
            await RefreshAddressAsync(state, cancellationToken);
        }
    }

    private int Record(PeerState state, bool success, TimeSpan roundTrip)
    {
        var previous = state.Health.Status;
        var (next, failures) = state.Record(success, roundTrip, timeProvider.GetUtcNow(), options.UnreachableAfterFailures);
        if (previous != next)
        {
            ClusterLog.PeerStatusChanged(logger, state.Info.NodeId, state.Info.Region, previous, next);
        }

        return failures;
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

        var (restarted, previous, next) = state.ObserveInbound(message.InstanceId, timeProvider.GetUtcNow());
        if (previous != next)
        {
            ClusterLog.PeerStatusChanged(logger, state.Info.NodeId, state.Info.Region, previous, next);
        }

        if (restarted)
        {
            client.ResetPeer(context.RemoteNodeId);
        }

        return ValueTask.FromResult<QuicHandlerOutcome<PongMessage>>(new PongMessage(options.NodeId, options.Region, message.SentAtTicks, _instanceId));
    }

    private sealed class PeerState(PeerNodeInfo info)
    {
        private readonly Lock _gate = new();
        private PeerHealth _health = new(info, PeerStatus.Suspect, TimeSpan.Zero, DateTimeOffset.MinValue);
        private IPAddress? _resolvedAddress;
        private long _instanceId;
        private int _failures;

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

        public (PeerStatus Status, int Failures) Record(bool success, TimeSpan roundTrip, DateTimeOffset now, int unreachableAfter)
        {
            lock (_gate)
            {
                _failures = success ? 0 : _failures + 1;
                var status = success ? PeerStatus.Healthy : _failures >= unreachableAfter ? PeerStatus.Unreachable : PeerStatus.Suspect;
                _health = success
                    ? new PeerHealth(Info, status, roundTrip, now)
                    : _health with { Status = status };
                return (status, _failures);
            }
        }

        public void ObserveInstance(long instanceId)
        {
            lock (_gate)
            {
                _instanceId = instanceId;
            }
        }

        public (bool Restarted, PeerStatus Previous, PeerStatus Next) ObserveInbound(long instanceId, DateTimeOffset now)
        {
            lock (_gate)
            {
                var restarted = _instanceId != 0 && instanceId != 0 && _instanceId != instanceId;
                if (instanceId != 0)
                {
                    _instanceId = instanceId;
                }

                var previous = _health.Status;
                if (previous == PeerStatus.Unreachable)
                {
                    _failures = 0;
                    _health = _health with { Status = PeerStatus.Suspect, LastSeenUtc = now };
                }
                else
                {
                    _health = _health with { LastSeenUtc = now };
                }

                return (restarted, previous, _health.Status);
            }
        }
    }
}
