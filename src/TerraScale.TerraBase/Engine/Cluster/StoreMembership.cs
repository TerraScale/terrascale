using TerraBase.Configuration;

namespace TerraBase.Engine.Cluster;

/// <summary>
/// Membership in an <see cref="IMemberStore"/> (NATS KV in a cell). Each member rewrites its key every
/// <see cref="Heartbeat"/>; a key nobody rewrites expires after the store's TTL. A member holds its lease while its last
/// successful renewal (its write, then a read of the others) began less than <see cref="Fence"/> ago by its own clock,
/// which ends it before its key can expire for the others. Refreshes happen from a background loop and from callers
/// that need a current view, at most once per heartbeat.
/// </summary>
// anna-full (TS-643): every region keeps its own members; regions find each other through Atlas, not this store.
internal sealed partial class StoreMembership(IMemberStore store, TerraBaseOptions options, TimeProvider time, ILogger<StoreMembership> logger)
    : IMembership, IDisposable
{
    /// <summary>How often this member renews its key and reads the others.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClusterView _view = new(0, []);
    private long? _attempted;
    private bool _left;

    // The exception type of the failing heartbeats, while they fail: a change of type is a new cause to report.
    private Type? _failure;

    // The timestamp of the last successful renewal, or -1 before the first.
    private long _renewed = -1;

    public ClusterMember Self { get; } = new(
        options.NodeName, options.Peers?.GetValueOrDefault(options.NodeName)?.ToString() ?? string.Empty, options.Region, string.Empty);

    public bool Dynamic => true;

    /// <summary>How long after its last successful renewal this member stops acting on its lease: a heartbeat before its key expires.</summary>
    public TimeSpan Fence => store.Ttl - Heartbeat;

    public bool HoldsLease => Interlocked.Read(ref _renewed) is var renewed and >= 0 && time.GetElapsedTime(renewed) < Fence;

    public async ValueTask<ClusterView> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_left || (_attempted is { } attempted && time.GetElapsedTime(attempted) < Heartbeat))
            {
                return _view;
            }

            _attempted = time.GetTimestamp();
            try
            {
                var started = time.GetTimestamp();
                await store.PutAsync(Self, cancellationToken).ConfigureAwait(false);
                _view = await store.ListAsync(cancellationToken).ConfigureAwait(false);
                // The lease runs from before the write, as the key may have been stored as soon as the write left, and
                // only a renewal that also read the others counts: a member never acts on a view older than its fence.
                Interlocked.Exchange(ref _renewed, started);
                if (_failure is not null)
                {
                    _failure = null;
                    LogRecovered(logger);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The last view stays; the lease ends on its own once renewals keep failing. A failure that coincides with
                // shutdown is only logged too, so the heartbeat never fails the host's stop.
                if (_failure == error.GetType())
                {
                    LogRetryFailed(logger, error);
                }
                else
                {
                    LogFailed(logger, error);
                }

                _failure = error.GetType();
            }

            return _view;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask LeaveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _left = true;
            Interlocked.Exchange(ref _renewed, -1);
            await store.RemoveAsync(Self.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            LogLeaveFailed(logger, error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "This member could not renew its membership or read the other members; it retries at the next heartbeat")]
    private static partial void LogFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "This member still could not renew its membership or read the other members; it retries at the next heartbeat")]
    private static partial void LogRetryFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "This member renews its membership and reads the other members again")]
    private static partial void LogRecovered(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "This member could not remove itself from the membership; the others take its work over once its key expires")]
    private static partial void LogLeaveFailed(ILogger logger, Exception error);
}

/// <summary>
/// Renews this member's membership every heartbeat, whether or not anything else asks for the view, and leaves at
/// shutdown. It stops after the services registered after it (continuous backup), so they no longer act on the lease.
/// </summary>
internal sealed class MembershipHeartbeat(IMembership membership, TimeProvider time) : BackgroundService
{
    private readonly Lock _stopGate = new();
    private Task? _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(StoreMembership.Heartbeat, time);
        do
        {
            _ = await membership.RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // The host can be stopped twice at once (see ContinuousBackup.StopAsync): every caller awaits the one stop.
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stopGate)
        {
            return _stopping ??= StopOnceAsync(cancellationToken);
        }
    }

    private async Task StopOnceAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await membership.LeaveAsync(cancellationToken).ConfigureAwait(false);
    }
}
