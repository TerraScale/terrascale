using System.Collections.Immutable;
using TerraBase.Configuration;
using ZLogger;

namespace TerraBase.Engine.Cluster;

/// <summary>
/// Membership read from an <see cref="IMemberDirectory"/> (ts-orch's peers file in a cluster). Every <see cref="Heartbeat"/>
/// this member lists the live nodes again; its view is the members of its own region. A member holds its lease while its
/// last successful read began less than <see cref="Fence"/> ago by its own clock and listed it: a node ts-orch sees stopped
/// leaves the directory, and stops acting on its lease within a heartbeat of the others seeing it gone. Refreshes happen from a
/// background loop and from callers that need a current view, at most once per heartbeat.
/// </summary>
internal sealed class DirectoryMembership(IMemberDirectory directory, TerraBaseOptions options, TimeProvider time, ILogger<DirectoryMembership> logger)
    : IMembership, IDisposable
{
    /// <summary>How often this member lists the members again.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClusterView _view = new(0, []);
    private long? _attempted;
    private bool _left;

    // The exception type of the failing reads, while they fail: a change of type is a new cause to report.
    private Type? _failure;

    // The timestamp of the last successful read that listed this member, or -1 before the first.
    private long _renewed = -1;

    public ClusterMember Self { get; } = new(options.MachineId, string.Empty, options.Region, string.Empty);

    public bool Dynamic => true;

    /// <summary>How long after its last successful read this member stops acting on its lease: two heartbeats.</summary>
    public TimeSpan Fence => 2 * Heartbeat;

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
                var machines = await directory.ListAsync(cancellationToken).ConfigureAwait(false);
                var ordered = machines.OrderBy(member => member.Name, StringComparer.Ordinal).ToList();
                ImmutableArray<ClusterMember> members = [.. ordered.Where(member => member.Region == options.Region)];
                ImmutableArray<ClusterMember> elsewhere = [.. ordered.Where(member => member.Region != options.Region)];
                _view = members.SequenceEqual(_view.Members) ? _view with { Elsewhere = elsewhere }
                    : new(_view.Revision + 1, members, elsewhere);
                // The lease runs from before the read, and only a read that listed this member counts.
                Interlocked.Exchange(ref _renewed, _view.Contains(Self.Name) ? started : -1);
                if (_failure is not null)
                {
                    _failure = null;
                    logger.ZLogInformation($"This member reads the other members again");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The last view stays; the lease ends on its own once reads keep failing.
                if (_failure == error.GetType())
                {
                    logger.ZLogDebug(error, $"This member still could not read the other members; it retries at the next heartbeat");
                }
                else
                {
                    logger.ZLogWarning(error, $"This member could not read the other members; it retries at the next heartbeat");
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
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// Reads the members every heartbeat, whether or not anything else asks for the view, and leaves at shutdown. It stops
/// after the services registered after it (continuous backup), so they no longer act on the lease.
/// </summary>
internal sealed class MembershipHeartbeat(IMembership membership, TimeProvider time) : BackgroundService
{
    private readonly Lock _stopGate = new();
    private Task? _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(DirectoryMembership.Heartbeat, time);
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
