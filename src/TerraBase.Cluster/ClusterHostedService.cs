using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed record ClusterComponents(
    PeerHealthMonitor Health,
    DeltaReplicator Replicator,
    StrongParticipant Participant,
    InRegionRaftRouter Raft,
    AntiEntropyService AntiEntropy);

public sealed class ClusterHostedService(QuicRpcHost host, ClusterComponents components, IEnumerable<IQuicEndpoints> endpoints, ILogger<ClusterHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task[] _loops = [];

    public IPEndPoint? BoundEndPoint => host.BoundEndPoint;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        components.Health.Map(host);
        components.Replicator.Map(host);
        components.Participant.Map(host);
        components.Raft.Map(host);
        components.AntiEntropy.Map(host);
        foreach (var endpoint in endpoints)
        {
            endpoint.Map(host);
        }

        var started = await host.StartAsync(cancellationToken);
        var failure = started switch
        {
            IPEndPoint bound => Bound(bound),
            QuicUnavailable unavailable => unavailable,
        };
        if (failure is not null)
        {
            throw new InvalidOperationException(string.Concat("QUIC mesh failed to start: ", failure.Message));
        }

        await components.Health.RegisterPeersAsync(cancellationToken);
        await components.Raft.StartAsync(cancellationToken);
        var stopping = _stopping.Token;
        _loops =
        [
            Contained("health", components.Health.RunAsync(stopping)),
            Contained("anti-entropy", components.AntiEntropy.RunPeriodicAsync(stopping)),
            Contained("catch-up", components.AntiEntropy.RunOnDemandAsync(stopping)),
        ];
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await Task.WhenAll(_loops).WaitAsync(cancellationToken);
        await components.Raft.DisposeAsync();
        await components.Replicator.DisposeAsync();
        await host.DisposeAsync();
    }

    public ValueTask DisposeAsync()
    {
        _stopping.Dispose();
        return ValueTask.CompletedTask;
    }

    private QuicUnavailable? Bound(IPEndPoint bound)
    {
        ClusterLog.MeshStarted(logger, host.NodeId, host.Region, bound);
        return null;
    }

    private async Task Contained(string name, Task loop)
    {
        try
        {
            await loop;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ClusterLog.LoopFailed(logger, name, exception);
        }
    }
}
