using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed class PeerHealthMonitor(ClusterOptions options, QuicRpcClient client, TimeProvider timeProvider, ILogger<PeerHealthMonitor> logger)
{
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
            await RegisterAsync(state, cancellationToken).ConfigureAwait(false);
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
                    PingAsync).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
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

        await PingAsync(state, cancellationToken).ConfigureAwait(false);
        return state.Health;
    }

    private async ValueTask PingAsync(PeerState state, CancellationToken cancellationToken)
    {
        if (!state.Registered)
        {
            await RegisterAsync(state, cancellationToken).ConfigureAwait(false);
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
            new PingMessage(options.NodeId, options.Region, started),
            options.PingTimeout,
            cancellationToken).ConfigureAwait(false);
        var success = response switch
        {
            PongMessage pong => string.Equals(pong.NodeId, state.Info.NodeId, StringComparison.Ordinal),
            Unavailable or RequestTimeout or QuicRemoteError => false,
        };
        Record(state, success, timeProvider.GetElapsedTime(started));
    }

    private void Record(PeerState state, bool success, TimeSpan roundTrip)
    {
        var previous = state.Health.Status;
        var next = state.Record(success, roundTrip, timeProvider.GetUtcNow(), options.UnreachableAfterFailures);
        if (previous != next)
        {
            ClusterLog.PeerStatusChanged(logger, state.Info.NodeId, state.Info.Region, previous, next);
        }
    }

    private async ValueTask RegisterAsync(PeerState state, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(state.Info, cancellationToken).ConfigureAwait(false) is not { } address)
        {
            ClusterLog.PeerUnresolved(logger, state.Info.NodeId);
            return;
        }

        client.UpsertPeer(new QuicPeer(state.Info.NodeId, state.Info.Region, new IPEndPoint(address, state.Info.QuicPort)));
        state.Registered = true;
    }

    private static async ValueTask<IPAddress?> ResolveAsync(PeerNodeInfo peer, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(peer.Host, out var literal))
        {
            return literal;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(peer.Host, cancellationToken).ConfigureAwait(false);
            return addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private ValueTask<Result<PongMessage, QuicRemoteError>> HandlePingAsync(QuicCallContext context, PingMessage message, CancellationToken cancellationToken)
    {
        if (_peers.TryGetValue(message.NodeId, out var state))
        {
            state.MarkSeen(timeProvider.GetUtcNow());
        }

        return ValueTask.FromResult<Result<PongMessage, QuicRemoteError>>(new PongMessage(options.NodeId, options.Region, message.SentAtTicks));
    }

    private sealed class PeerState(PeerNodeInfo info)
    {
        private readonly Lock _gate = new();
        private PeerHealth _health = new(info, PeerStatus.Suspect, TimeSpan.Zero, DateTimeOffset.MinValue);
        private int _failures;

        public PeerNodeInfo Info { get; } = info;

        public bool Registered { get; set; }

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

        public PeerStatus Record(bool success, TimeSpan roundTrip, DateTimeOffset now, int unreachableAfter)
        {
            lock (_gate)
            {
                _failures = success ? 0 : _failures + 1;
                var status = success ? PeerStatus.Healthy : _failures >= unreachableAfter ? PeerStatus.Unreachable : PeerStatus.Suspect;
                _health = success
                    ? new PeerHealth(Info, status, roundTrip, now)
                    : _health with { Status = status };
                return status;
            }
        }

        public void MarkSeen(DateTimeOffset now)
        {
            lock (_gate)
            {
                _health = _health with { LastSeenUtc = now };
            }
        }
    }
}
