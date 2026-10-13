using System.Diagnostics.Metrics;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>
/// The sending side of the peer streams (docs/terrabase/multi-region-replication.md section 8): one <c>Push</c> stream to
/// every other machine of the membership, following it every heartbeat. Each stream carries this machine's own change
/// groups of every database the peer's region holds, at most one batch per database in flight, resumes from the peer's
/// last ack after a reconnect, and heartbeats while idle. Its cursors and lags are the outbound replication status.
/// </summary>
internal sealed class PeerStreams : BackgroundService, IReplicationStatus
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefillTimeout = TimeSpan.FromMinutes(10);
    private const int MaxDetail = 128;

    private readonly IMembership _membership;
    private readonly IChangeGroupSource _source;
    private readonly ITenantDirectory _tenants;
    private readonly LocalReplica _self;
    private readonly TerraBaseOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PeerStreams> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Link> _links = [with(StringComparer.Ordinal)];

    public PeerStreams(
        IMembership membership, IChangeGroupSource source, ITenantDirectory tenants, LocalReplica self, TerraBaseOptions options, TimeProvider time,
        IMeterFactory meters, ILogger<PeerStreams> logger)
    {
        (_membership, _source, _tenants, _self, _options, _time, _logger) = (membership, source, tenants, self, options, time, logger);
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.replication.send_lag", () => Lags(cursor => cursor.BehindSince), "s",
            "Age of the oldest change group not yet acknowledged by the peer region's machines.");
        meter.CreateObservableGauge("terrabase.replication.ack_lag", () => Lags(cursor => cursor.InFlight is null ? null : cursor.SentAt), "s",
            "Age of the oldest batch sent to the peer region's machines but not acknowledged.");
    }

    public ReplicationStatus Snapshot()
    {
        List<ReplicationPeerStatus> peers = [];
        var databases = new HashSet<TenantContext>();
        var omitted = 0;
        foreach (var (index, link) in Links().Index())
        {
            lock (link.Gate)
            {
                if (!link.Connected)
                {
                    continue;
                }

                List<ReplicationDatabaseStatus> detail = [];
                foreach (var (database, cursor) in link.Cursors)
                {
                    databases.Add(database);
                    if (detail.Count == MaxDetail)
                    {
                        omitted++;
                        continue;
                    }

                    detail.Add(new(detail.Count, database.OrganizationId, database.ProjectId!, database.DatabaseId!, Ulid.Empty, cursor.Head, cursor.Acked ?? 0,
                        cursor.InFlightGroups, Diverged: false, Age(cursor.BehindSince), cursor.Convergence));
                }

                peers.Add(new(index, detail.Sum(database => database.DurableLag), detail, link.Peer.Region));
            }
        }

        return new ReplicationStatus(true, databases.Count, omitted, peers);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new Dictionary<string, (CancellationTokenSource Stop, Task Run)>(StringComparer.Ordinal);
        try
        {
            using var timer = new PeriodicTimer(DirectoryMembership.Heartbeat, _time);
            do
            {
                var view = await _membership.RefreshAsync(stoppingToken).ConfigureAwait(false);
                var self = _membership.Self.Name;
                var peers = view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere)
                    .Where(member => member.Name != self && member.PeerAddress.Length > 0)
                    .ToDictionary(member => member.Name, StringComparer.Ordinal);
                foreach (var (name, (stop, run)) in running.Where(entry => !peers.TryGetValue(entry.Key, out var member) || Linked(entry.Key)?.Peer != member).ToList())
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                    await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    stop.Dispose();
                    running.Remove(name);
                    lock (_gate)
                    {
                        _links.Remove(name);
                    }
                }

                foreach (var member in peers.Values.Where(member => !running.ContainsKey(member.Name)))
                {
                    var link = new Link(member);
                    lock (_gate)
                    {
                        _links[member.Name] = link;
                    }

                    var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    running[member.Name] = (stop, RunAsync(link, stop.Token));
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        finally
        {
            foreach (var (stop, run) in running.Values)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                stop.Dispose();
            }
        }
    }

    // One peer, until its machine leaves the membership: reconnects with a growing backoff, reset by every welcome.
    private async Task RunAsync(Link link, CancellationToken cancellationToken)
    {
        var backoff = Idle;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StreamAsync(link, () => backoff = Idle, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.ZLogWarning(error, $"The replication stream to machine {link.Peer.Name:@Peer} in {link.Peer.Region:@Region} failed; it reconnects and resumes from the last ack");
            }
            finally
            {
                lock (link.Gate)
                {
                    link.Connected = false;
                }
            }

            await Task.Delay(backoff, _time, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    private async Task StreamAsync(Link link, Action welcomed, CancellationToken cancellationToken)
    {
        using var channel = PeerChannel.Create(new Uri(link.Peer.PeerAddress));
        using var call = new V1.Replication.ReplicationClient(channel).Push(cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new PushFrame
        {
            Hello = new Hello { Node = _self.Id.Value, Hlc = _self.Clock.Tick().Value, Region = _options.Region, Machine = _membership.Self.Name },
        }, cancellationToken).ConfigureAwait(false);
        using (var welcome = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            welcome.CancelAfter(ReplicationProtocol.ProgressTimeout);
            if (!await call.ResponseStream.MoveNext(welcome.Token).ConfigureAwait(false) || call.ResponseStream.Current.Welcome is not { Node: not 0 })
            {
                throw new IOException("the peer did not welcome the stream");
            }
        }

        var peer = new ReplicaId(call.ResponseStream.Current.Welcome.Node);
        lock (link.Gate)
        {
            link.Connected = true;
            foreach (var cursor in link.Cursors.Values)
            {
                (cursor.Acked, cursor.InFlight, cursor.InFlightGroups) = (null, null, 0);
            }
        }

        welcomed();
        var reading = call.ResponseStream.MoveNext(cancellationToken);
        Task? changed = null;
        var quiet = _time.GetTimestamp();
        while (true)
        {
            // Asked for before the pass reads, so a commit during the pass wakes the next one.
            changed = changed is null || changed.IsCompleted ? _source.ChangedAsync(cancellationToken) : changed;
            if (await ShipAsync(link, call.RequestStream, peer, cancellationToken).ConfigureAwait(false))
            {
                quiet = _time.GetTimestamp();
            }
            else if (_time.GetElapsedTime(quiet) >= Idle)
            {
                await call.RequestStream.WriteAsync(new PushFrame { Heartbeat = new Heartbeat { Hlc = _self.Clock.Tick().Value } }, cancellationToken).ConfigureAwait(false);
                quiet = _time.GetTimestamp();
            }

            lock (link.Gate)
            {
                // A refill joins whole buckets on the peer: it may take longer than a batch.
                if (link.Cursors.Values.Any(cursor => cursor.InFlight is not null && _time.GetElapsedTime(cursor.SentAt) > (cursor.Refilling ? RefillTimeout : ReplicationProtocol.ProgressTimeout)))
                {
                    throw new TimeoutException("the peer did not acknowledge a batch in time");
                }
            }

            using (var pass = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                _ = await Task.WhenAny(reading, changed, Task.Delay(Idle, _time, pass.Token)).ConfigureAwait(false);
                await pass.CancelAsync().ConfigureAwait(false);
            }

            if (reading.IsCompleted)
            {
                if (!await reading.ConfigureAwait(false))
                {
                    throw new IOException("the peer ended the stream");
                }

                await AckedAsync(link, call.ResponseStream.Current.Ack, peer, cancellationToken).ConfigureAwait(false);
                reading = call.ResponseStream.MoveNext(cancellationToken);
            }
        }
    }

    // Sends the next batch of every database placed in the peer's region with none in flight; whether any was sent.
    private async ValueTask<bool> ShipAsync(Link link, IClientStreamWriter<PushFrame> stream, ReplicaId peer, CancellationToken cancellationToken)
    {
        var sent = false;
        await foreach (var database in _source.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await PlacedAsync(link, database, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            Cursor cursor;
            lock (link.Gate)
            {
                cursor = link.Cursors.TryGetValue(database, out var known) ? known : link.Cursors[database] = new Cursor();
            }

            var acked = cursor.Acked ?? await _source.AckedAsync(database, peer, cancellationToken).ConfigureAwait(false);
            lock (link.Gate)
            {
                cursor.Acked = acked;
            }

            if (cursor.InFlight is not null || _time.GetTimestamp() < cursor.RetryAt)
            {
                continue;
            }

            // Read before the retention check: retention only cuts from the oldest group on, so a trim between the two shows
            // as a cut here and never ships a batch past a hole.
            var groups = await _source.ReadAsync(database, acked, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, cancellationToken).ConfigureAwait(false);
            if (!await _source.RetainsAsync(database, acked, cancellationToken).ConfigureAwait(false))
            {
                // Retention cut groups after the peer's ack (it was silent or far behind, or it is new): it refills from this
                // machine, then the stream goes on from the newest change.
                var head = await _source.HeadAsync(database, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(new PushFrame { Refill = new Refill { Database = ChangeGroups.Wire(database), ChangeId = checked((ulong)head) } }, cancellationToken)
                    .ConfigureAwait(false);
                lock (link.Gate)
                {
                    cursor.BehindSince ??= _time.GetTimestamp();
                    (cursor.InFlight, cursor.InFlightGroups, cursor.SentAt, cursor.Head, cursor.Refilling) = (head, 0, _time.GetTimestamp(), Math.Max(cursor.Head, head), true);
                }

                sent = true;
                continue;
            }

            if (groups.Count == 0)
            {
                lock (link.Gate)
                {
                    cursor.Convergence = cursor.BehindSince is { } since ? _time.GetElapsedTime(since).TotalSeconds : cursor.Convergence;
                    cursor.BehindSince = null;
                }

                continue;
            }

            var last = groups[^1].ChangeId;
            await stream.WriteAsync(new PushFrame
            {
                Batch = new Batch { Database = ChangeGroups.Wire(database), LastChangeId = checked((ulong)last), Groups = ChangeGroups.Encode(groups) },
            }, cancellationToken).ConfigureAwait(false);
            lock (link.Gate)
            {
                cursor.BehindSince ??= _time.GetTimestamp();
                (cursor.InFlight, cursor.InFlightGroups, cursor.SentAt, cursor.Head) = (last, groups.Count, _time.GetTimestamp(), Math.Max(cursor.Head, last));
            }

            sent = true;
        }

        return sent;
    }

    private async ValueTask AckedAsync(Link link, Ack? ack, ReplicaId peer, CancellationToken cancellationToken)
    {
        var database = ChangeGroups.Tenant(ack?.Database);
        Cursor? cursor;
        lock (link.Gate)
        {
            cursor = link.Cursors.GetValueOrDefault(database);
            if (cursor?.InFlight is not { } inFlight || (!ack!.Refused && ack.ChangeId != (ulong)inFlight))
            {
                throw new InvalidDataException("the peer acknowledged a batch that is not in flight");
            }

            (cursor.InFlight, cursor.InFlightGroups, cursor.Refilling) = (null, 0, false);
            if (ack.Refused)
            {
                // The peer does not hold the database yet (its catalog lags): try again later.
                cursor.RetryAt = _time.GetTimestamp() + (long)(Retry.TotalSeconds * _time.TimestampFrequency);
                return;
            }

            cursor.Acked = inFlight;
        }

        await _source.AckAsync(database, peer, (long)ack.ChangeId, cancellationToken).ConfigureAwait(false);
    }

    // Whether the database's placement includes the peer's region, read again every heartbeat.
    private async ValueTask<bool> PlacedAsync(Link link, TenantContext database, CancellationToken cancellationToken)
    {
        if (link.Placements.TryGetValue(database, out var known) && _time.GetElapsedTime(known.ReadAt) < DirectoryMembership.Heartbeat)
        {
            return known.Placed;
        }

        var placed = await _tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord record
            && record.Placement.Includes(link.Peer.Region);
        link.Placements[database] = (placed, _time.GetTimestamp());
        return placed;
    }

    private Link? Linked(string name)
    {
        lock (_gate)
        {
            return _links.GetValueOrDefault(name);
        }
    }

    private List<Link> Links()
    {
        lock (_gate)
        {
            return [.. _links.Values.OrderBy(link => link.Peer.Name, StringComparer.Ordinal)];
        }
    }

    private double Age(long? since) => since is { } timestamp ? _time.GetElapsedTime(timestamp).TotalSeconds : 0;

    // Per peer region, the largest age over its connected machines' databases.
    private IEnumerable<Measurement<double>> Lags(Func<Cursor, long?> since) =>
        Links().GroupBy(link => link.Peer.Region, StringComparer.Ordinal).Select(region => new Measurement<double>(
            region.Max(link =>
            {
                lock (link.Gate)
                {
                    return link.Cursors.Values.Select(cursor => Age(since(cursor))).DefaultIfEmpty(0).Max();
                }
            }),
            new KeyValuePair<string, object?>("peer_region", region.Key)));

    private sealed class Link(ClusterMember peer)
    {
        public ClusterMember Peer { get; } = peer;

        public Lock Gate { get; } = new();

        public bool Connected { get; set; }

        public Dictionary<TenantContext, Cursor> Cursors { get; } = [];

        // Only the link's own loop reads and writes these.
        public Dictionary<TenantContext, (bool Placed, long ReadAt)> Placements { get; } = [];
    }

    private sealed class Cursor
    {
        // The peer's last ack (null until read from the source on this connection), the batch in flight and when it went.
        public long? Acked { get; set; }

        public long? InFlight { get; set; }

        // Whether the batch in flight is a refill.
        public bool Refilling { get; set; }

        public int InFlightGroups { get; set; }

        public long SentAt { get; set; }

        public long RetryAt { get; set; }

        // The highest change id read, since when a backlog has existed, and how long the last one took to clear.
        public long Head { get; set; }

        public long? BehindSince { get; set; }

        public double? Convergence { get; set; }
    }
}
