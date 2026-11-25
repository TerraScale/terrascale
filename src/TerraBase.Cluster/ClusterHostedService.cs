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

public sealed class ClusterHostedService(QuicRpcHost host, ClusterComponents components, IEnumerable<IQuicEndpoints> endpoints, StrongCommitRecovery recovery, TimeProvider timeProvider, ILogger<ClusterHostedService> logger) : IHostedService, IAsyncDisposable
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
            ClusterLoop.RunAsync("health", components.Health.RunAsync, timeProvider, logger, stopping),
            ClusterLoop.RunAsync("anti-entropy", components.AntiEntropy.RunPeriodicAsync, timeProvider, logger, stopping),
            ClusterLoop.RunAsync("catch-up", components.AntiEntropy.RunOnDemandAsync, timeProvider, logger, stopping),
            recovery.RunAsync(),
        ];
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        recovery.Complete();
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
}

internal static class ClusterLoop
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(string name, Func<CancellationToken, Task> run, TimeProvider timeProvider, ILogger logger, CancellationToken cancellationToken)
    {
        var delay = InitialDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = timeProvider.GetTimestamp();
            try
            {
                await run(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (timeProvider.GetElapsedTime(started) >= MaximumDelay)
                {
                    delay = InitialDelay;
                }

                ClusterLog.LoopFailed(logger, name, delay, exception);
            }

            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumDelay.Ticks));
        }
    }
}
