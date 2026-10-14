using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
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
internal sealed class PeerStreams : BackgroundService, IReplicationStatus, IRemoteDurability
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefillTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FrontierEvery = TimeSpan.FromSeconds(5);
    private const int MaxDetail = 128;
    private const int MaxPendingResponses = 32;
    // Linked nodes HeldOriginsAsync considers at most; past it none is held. The membership bounds links well below it.
    private const int MaxOrigins = 256;

    private static readonly Unavailable NoRemote =
        new("no machine of another region that holds the database is connected; this write executed nothing, retry later", "remote_region_unavailable");

    private readonly IMembership _membership;
    private readonly PeerCredential _credential;
    private readonly IChangeGroupSource _source;
    private readonly RemoteAckWaiter _acks;
    private readonly IReplicaStates _states;
    private readonly ITenantDirectory _tenants;
    private readonly LocalReplica _self;
    private readonly TerraBaseOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PeerStreams> _logger;
    private readonly Counter<long> _unreadable;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Link> _links = [with(StringComparer.Ordinal)];

    private readonly Lazy<ICatalogGate> _catalogGate;

    public PeerStreams(
        IMembership membership, IChangeGroupSource source, RemoteAckWaiter acks, IReplicaStates states, ITenantDirectory tenants, LocalReplica self, TerraBaseOptions options,
        TimeProvider time, IMeterFactory meters, ILogger<PeerStreams> logger, Lazy<ICatalogGate> catalogGate, PeerCredential credential)
    {
        _credential = credential;
        (_membership, _source, _acks, _states, _tenants, _self, _options, _time, _logger) =
            (membership, source, acks, states, tenants, self, options, time, logger);
        _catalogGate = catalogGate;
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.replication.send_lag", () => Lags(cursor => cursor.BehindSince), "s",
            "Age of the oldest change group not yet acknowledged by the peer region's machines.");
        _unreadable = meter.CreateCounter<long>("terrabase.replication.unreadable_reads", "{read}",
            "Reads of a database's change log that failed (a damaged row is refused), by the peer region the batch was for.");
        meter.CreateObservableGauge("terrabase.replication.ack_lag", () => Lags(cursor => cursor.InFlight is null ? null : cursor.SentAt), "s",
            "Age of the oldest batch sent to the peer region's machines but not acknowledged.");
    }

    public bool Confirms => true;

    public async ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken)
    {
        var view = await _membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return view.Elsewhere.IsDefault ? [] : [.. view.Elsewhere.Select(member => member.Region).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    public async ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken) =>
        Eligible(await PlacementAsync(database, cancellationToken).ConfigureAwait(false))
            ? _acks.TryAdmit(database)
            : NoRemote;

    public async ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken)
    {
        var placement = await PlacementAsync(database, cancellationToken).ConfigureAwait(false);
        return await _acks.WaitAsync(database, marker, () => Eligible(placement), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlySet<ReplicaId>> HeldOriginsAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await PlacementAsync(database, cancellationToken).ConfigureAwait(false) is not { } placement)
        {
            return FrozenSet<ReplicaId>.Empty;
        }

        // The node each connected link's machine welcomed it with, under the region the membership names for that machine. A
        // node two links name with different regions is trusted for neither. Past MaxOrigins nodes none is: fail closed.
        var regions = new Dictionary<ReplicaId, string?>();
        foreach (var link in Links())
        {
            lock (link.Gate)
            {
                if (!link.Connected || link.Origin is not { } origin)
                {
                    continue;
                }

                if (regions.TryGetValue(origin, out var known))
                {
                    regions[origin] = known == link.Peer.Region ? known : null;
                }
                else if (regions.Count == MaxOrigins)
                {
                    return FrozenSet<ReplicaId>.Empty;
                }
                else
                {
                    regions[origin] = link.Peer.Region;
                }
            }
        }

        return regions.Where(entry => entry.Key != _self.Id && entry.Value is { } region && region != _options.Region && placement.Includes(region))
            .Select(entry => entry.Key).ToFrozenSet();
    }

    private async ValueTask<DatabasePlacement?> PlacementAsync(TenantContext database, CancellationToken cancellationToken) =>
        await _tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord record ? record.Placement : null;

    // Whether a connected link reaches a machine of another region the placement includes: one that can confirm a write.
    private bool Eligible(DatabasePlacement? placement) =>
        placement is not null && Links().Any(link =>
        {
            lock (link.Gate)
            {
                return link.Connected && link.Peer.Region != _options.Region && placement.Includes(link.Peer.Region);
            }
        });

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

                    detail.Add(new(detail.Count, database.OrganizationId, database.ProjectId!, database.DatabaseId!, cursor.Incarnation ?? Ulid.Empty, cursor.Head, cursor.Acked ?? 0,
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
                    (link.Connected, link.Origin) = (false, null);
                }

                _acks.Lost();
            }

            await Task.Delay(backoff, _time, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    // The peer answers every frame (a heartbeat with an empty frame), so silence for the progress timeout means a half-open
    // connection or a peer that stopped reading, where this side's writes stall: either ends the stream, which reconnects.
    private async Task StreamAsync(Link link, Action welcomed, CancellationToken cancellationToken)
    {
        using var silent = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout, _time);
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, silent.Token);
        try
        {
            await ConnectedAsync(link, welcomed, silent, stream.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (silent.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("the peer did not answer in time", error);
        }
    }

    private async Task ConnectedAsync(Link link, Action welcomed, CancellationTokenSource silent, CancellationToken cancellationToken)
    {
        using var channel = PeerChannel.Create(new Uri(link.Peer.PeerAddress), _credential);
        using var call = new V1.Replication.ReplicationClient(channel).Push(cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new PushFrame
        {
            Hello = new Hello { Node = _self.Id.Value, Hlc = _self.Clock.Tick().Value, Region = _options.Region, Machine = _membership.Self.Name },
        }, cancellationToken).ConfigureAwait(false);
        if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false) || call.ResponseStream.Current.Welcome is not { Node: not 0 })
        {
            throw new IOException("the peer did not welcome the stream");
        }

        silent.CancelAfter(ReplicationProtocol.ProgressTimeout);

        var peer = new ReplicaId(call.ResponseStream.Current.Welcome.Node);
        lock (link.Gate)
        {
            (link.Connected, link.Origin) = (true, peer);
            foreach (var cursor in link.Cursors.Values)
            {
                (cursor.Incarnation, cursor.Acked, cursor.InFlight, cursor.InFlightGroups, cursor.RequiresRefill, cursor.Refilling, cursor.Live) =
                    (null, null, null, 0, false, false, null);
            }
        }

        welcomed();
        using var activity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var responses = Channel.CreateBounded<AckFrame>(2 * MaxPendingResponses);
        var reading = responses.Reader.ReadAsync(activity.Token).AsTask();
        var pending = 0;
        ExceptionDispatchInfo? failure = null;
        using var writer = new LinkWriter(call.RequestStream, _time, ReserveAsync);
        var receiving = ReceiveAsync();
        var heartbeats = HeartbeatAsync();
        var progress = ProgressAsync();
        Task? changed = null;
        try
        {
            while (true)
            {
                activity.Token.ThrowIfCancellationRequested();
                // Asked for before the pass reads, so a commit during the pass wakes the next one.
                changed = changed is null || changed.IsCompleted ? _source.ChangedAsync(activity.Token) : changed;
                _ = await ShipAsync(link, writer, peer, DrainAsync, activity.Token).ConfigureAwait(false);

                using (var pass = CancellationTokenSource.CreateLinkedTokenSource(activity.Token))
                {
                    _ = await Task.WhenAny(reading, changed, Task.Delay(Idle, _time, pass.Token)).ConfigureAwait(false);
                    await pass.CancelAsync().ConfigureAwait(false);
                }

                await DrainAsync().ConfigureAwait(false);

                CheckProgress();
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && Volatile.Read(ref failure) is not null)
        {
            Volatile.Read(ref failure)!.Throw();
            throw;
        }
        finally
        {
            await activity.CancelAsync().ConfigureAwait(false);
            await receiving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await heartbeats.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await progress.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await ((Task)reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // Process each database's responses before shipping the next one: a large placement cannot fill the queue while
        // the pass waits on another database's writer. InFlight was published before this consumer sees a batch's ack.
        async ValueTask DrainAsync()
        {
            while (reading.IsCompleted)
            {
                var response = await reading.ConfigureAwait(false);
                if (pending == 0)
                {
                    throw new InvalidDataException("the peer sent a response with no frame outstanding");
                }

                if (response.Stable is { } report)
                {
                    // A report for a frontier of a replaced copy says nothing of the copy in place.
                    _ = await _source.RecordStableAsync(
                        ChangeGroups.Tenant(report.Database), ChangeGroups.RequiredIncarnation(report.Database), peer, new Hlc(report.Stable), activity.Token).ConfigureAwait(false);
                }
                else
                {
                    await AckedAsync(link, response.Ack, peer, activity.Token).ConfigureAwait(false);
                }

                pending--;
                reading = responses.Reader.ReadAsync(activity.Token).AsTask();
            }
        }

        // At most 32 response-producing frames await durable processing, leaving room to read their replies and heartbeats
        // even while local storage is busy. The budget is released by the single consumer, not by mere receipt.
        async ValueTask ReserveAsync()
        {
            while (pending >= MaxPendingResponses)
            {
                _ = await reading.ConfigureAwait(false);
                await DrainAsync().ConfigureAwait(false);
            }

            pending++;
        }

        void CheckProgress()
        {
            lock (link.Gate)
            {
                if (link.Cursors.Values.Any(cursor => cursor.InFlight is not null && !cursor.ResponseReceived
                    && _time.GetElapsedTime(cursor.SentAt) >= (cursor.Refilling ? RefillTimeout : ReplicationProtocol.ProgressTimeout)))
                {
                    throw new TimeoutException("the peer did not acknowledge a batch in time");
                }
            }
        }

        // Observe liveness while local storage is busy; heartbeat replies need no durable processing or queue space.
        async Task ReceiveAsync()
        {
            try
            {
                while (await call.ResponseStream.MoveNext(activity.Token).ConfigureAwait(false))
                {
                    silent.CancelAfter(ReplicationProtocol.ProgressTimeout);
                    var frame = call.ResponseStream.Current;
                    if (frame.KindCase != AckFrame.KindOneofCase.None)
                    {
                        if (frame.Ack is { } ack)
                        {
                            var database = ChangeGroups.Tenant(ack.Database);
                            lock (link.Gate)
                            {
                                var cursor = link.Cursors.GetValueOrDefault(database);
                                if (cursor?.InFlight is { } inFlight && cursor.Incarnation is { } pinned
                                    && ChangeGroups.RequiredIncarnation(ack.Database) == pinned && (ack.Refused || ack.ChangeId == (ulong)inFlight))
                                {
                                    // Receipt satisfies the peer's deadline. It never advances the durable cursor.
                                    cursor.ResponseReceived = true;
                                }
                            }
                        }

                        await responses.Writer.WriteAsync(frame, activity.Token).ConfigureAwait(false);
                    }
                }

                await FailAsync(new IOException("the peer ended the stream")).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await FailAsync(error).ConfigureAwait(false);
            }
        }

        async Task HeartbeatAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(Idle, _time);
                while (await timer.WaitForNextTickAsync(activity.Token).ConfigureAwait(false))
                {
                    if (writer.Quiet >= Idle)
                    {
                        await writer.WriteAsync(new PushFrame { Heartbeat = new Heartbeat { Hlc = _self.Clock.Tick().Value } }, activity.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error)
            {
                await FailAsync(error).ConfigureAwait(false);
            }
        }

        // A stalled transport write can also hold the heartbeat writer's semaphore. Deadline checks never await it.
        async Task ProgressAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(Idle, _time);
                while (await timer.WaitForNextTickAsync(activity.Token).ConfigureAwait(false))
                {
                    CheckProgress();
                }
            }
            catch (Exception error)
            {
                await FailAsync(error).ConfigureAwait(false);
            }
        }

        // Preserve the first background failure before cancellation unblocks storage or transport with a different error.
        async Task FailAsync(Exception error)
        {
            if (!activity.IsCancellationRequested)
            {
                _ = Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null);
            }

            responses.Writer.TryComplete(error);
            await activity.CancelAsync().ConfigureAwait(false);
        }
    }

    // Sends the next batch of every database placed in the peer's region with none in flight; whether any was sent.
    private async ValueTask<bool> ShipAsync(Link link, LinkWriter stream, ReplicaId peer, Func<ValueTask> drain, CancellationToken cancellationToken)
    {
        var sent = false;
        await using var databases = _source.ListDatabasesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            await drain().ConfigureAwait(false);
            if (!await databases.MoveNextAsync().ConfigureAwait(false))
            {
                break;
            }

            var database = databases.Current;
            if (database == ChangeGroups.System && !_catalogGate.Value.Trusted
                || !await PlacedAsync(link, database, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            Cursor cursor;
            lock (link.Gate)
            {
                cursor = link.Cursors.TryGetValue(database, out var known) ? known : link.Cursors[database] = new Cursor();
            }

            // The cursor is pinned to one incarnation of this machine's copy: its change ids mean nothing in another.
            var position = cursor is { Incarnation: { } pinned, Acked: { } resume } ? new SourceCursor(pinned, resume) { RequiresRefill = cursor.RequiresRefill }
                : await _source.AckedAsync(database, peer, cancellationToken).ConfigureAwait(false);
            if (position is not (var incarnation, var acked))
            {
                continue;
            }

            lock (link.Gate)
            {
                (cursor.Incarnation, cursor.Acked, cursor.RequiresRefill) = (incarnation, acked, position.Value.RequiresRefill);
            }

            if (cursor.InFlight is not null || _time.GetTimestamp() < cursor.RetryAt)
            {
                continue;
            }

            // Read before the retention check: retention only cuts from the oldest group on, so a trim between the two shows
            // as a cut here and never ships a batch past a hole.
            var frontier = _self.Clock.Tick();
            SourceGroups? read;
            try
            {
                read = await _source.ReadAsync(database, incarnation, acked, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // A change log this machine cannot read (a damaged row is refused, never skipped) holds back that database only:
                // nothing after the peer's ack ships or is acknowledged, the other databases on the stream go on, and this one is
                // read again from the same ack. Its send_lag grows meanwhile.
                _logger.ZLogError(error, $"The change log of database {database.DatabaseId:@Database} could not be read for machine {link.Peer.Name:@Peer} in {link.Peer.Region:@Region}; that database is held and read again");
                _unreadable.Add(1, new KeyValuePair<string, object?>("peer_region", link.Peer.Region));
                lock (link.Gate)
                {
                    cursor.BehindSince ??= _time.GetTimestamp();
                    cursor.RetryAt = _time.GetTimestamp() + (long)(MaxBackoff.TotalSeconds * _time.TimestampFrequency);
                }

                continue;
            }

            // The copy was replaced (a copy, restore or rebuild) since the cursor was read: start over from its own acks.
            var retains = read is null || read.Incarnation != incarnation ? null
                : await _source.RetainsAsync(database, incarnation, acked, cancellationToken).ConfigureAwait(false);
            if (read is not { Groups: var groups } || retains is null)
            {
                Reset(link, cursor);
                continue;
            }

            if (retains is false || cursor.RequiresRefill)
            {
                // Retention cut groups after the peer's ack (it was silent or far behind, or it is new): it refills from this
                // copy, then the stream goes on from its newest change. A copy or restore whose own change log is whole refills
                // the current state and goes on from its ack: its writes since then also ship in live batches, which
                // can confirm them, instead of hiding under an uncertified refill head (decision 0013a, B2).
                if (await _source.HeadAsync(database, incarnation, cancellationToken).ConfigureAwait(false) is not { } head)
                {
                    Reset(link, cursor);
                    continue;
                }

                var resume = retains is true ? acked : head;
                var horizon = await _states.HorizonAsync(database, cancellationToken).ConfigureAwait(false);
                await stream.ReserveAsync().ConfigureAwait(false);
                lock (link.Gate)
                {
                    cursor.BehindSince ??= _time.GetTimestamp();
                    (cursor.InFlight, cursor.InFlightGroups, cursor.SentAt, cursor.Head, cursor.Refilling) = (resume, 0, _time.GetTimestamp(), Math.Max(cursor.Head, head), true);
                    cursor.ResponseReceived = false;
                }
                await stream.WriteAsync(new PushFrame
                {
                    Refill = new Refill { Database = ChangeGroups.Wire(database, incarnation), ChangeId = checked((ulong)resume), Horizon = horizon.Value },
                }, cancellationToken).ConfigureAwait(false);

                sent = true;
                continue;
            }

            if (groups.Count == 0)
            {
                if (_time.GetElapsedTime(cursor.FrontierAt) >= FrontierEvery || cursor.FrontierAt == 0)
                {
                    await stream.ReserveAsync().ConfigureAwait(false);
                    await stream.WriteAsync(new PushFrame { Frontier = new Frontier { Database = ChangeGroups.Wire(database, incarnation), Hlc = frontier.Value } }, cancellationToken)
                        .ConfigureAwait(false);
                    cursor.FrontierAt = _time.GetTimestamp();
                }

                lock (link.Gate)
                {
                    cursor.Convergence = cursor.BehindSince is { } since ? _time.GetElapsedTime(since).TotalSeconds : cursor.Convergence;
                    cursor.BehindSince = null;
                }

                continue;
            }

            var last = groups[^1].ChangeId;
            await stream.ReserveAsync().ConfigureAwait(false);
            lock (link.Gate)
            {
                cursor.BehindSince ??= _time.GetTimestamp();
                (cursor.InFlight, cursor.InFlightGroups, cursor.SentAt, cursor.Head, cursor.Refilling) = (last, groups.Count, _time.GetTimestamp(), Math.Max(cursor.Head, last), false);
                cursor.ResponseReceived = false;
            }
            await stream.WriteAsync(new PushFrame
            {
                Batch = new Batch { Database = ChangeGroups.Wire(database, incarnation), LastChangeId = checked((ulong)last), Groups = ChangeGroups.Encode(groups) },
            }, cancellationToken).ConfigureAwait(false);

            sent = true;
        }

        return sent;
    }

    private async ValueTask AckedAsync(Link link, Ack? ack, ReplicaId peer, CancellationToken cancellationToken)
    {
        var database = ChangeGroups.Tenant(ack?.Database);
        Cursor? cursor;
        Ulid incarnation;
        bool live;
        long from;
        lock (link.Gate)
        {
            cursor = link.Cursors.GetValueOrDefault(database);
            if (cursor?.InFlight is not { } inFlight || cursor.Incarnation is not { } pinned || ChangeGroups.RequiredIncarnation(ack!.Database) != pinned
                || (!ack.Refused && ack.ChangeId != (ulong)inFlight))
            {
                throw new InvalidDataException("the peer acknowledged a batch that is not in flight");
            }

            incarnation = pinned;
            // Capture the original batch kind before clearing it: stored positions and uncertified refills never confirm.
            live = !cursor.Refilling && link.Peer.Region != _options.Region && link.Placements.GetValueOrDefault(database).Placed;
            from = cursor.Live is { } run && run.Incarnation == incarnation ? run.From : cursor.Acked ?? inFlight;
            (cursor.InFlight, cursor.InFlightGroups, cursor.Refilling) = (null, 0, false);
            if (ack.Refused)
            {
                // The peer does not hold the database yet (its catalog lags): try again later.
                cursor.RetryAt = _time.GetTimestamp() + (long)(Retry.TotalSeconds * _time.TimestampFrequency);
                return;
            }

            (cursor.Acked, cursor.Live) = (inFlight, null);
        }

        // Shape reads publish from the same proof, whether or not a client write still waits for it. Proved before the ack is
        // recorded, which cuts the change log down to the frontier this moves (FR-25); ProveAsync checks the incarnation itself.
        // A failure only delays publication until the next ack of the run, which covers the same range: it never fails the stream.
        if (live && (await PlacementAsync(database, cancellationToken).ConfigureAwait(false))?.Includes(link.Peer.Region) == true)
        {
            try
            {
                var held = await HeldOriginsAsync(database, cancellationToken).ConfigureAwait(false);
                _ = await _source.ProveAsync(database, incarnation, from, (long)ack.ChangeId, held, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.ZLogWarning(error, $"Recording a remote durable proof for shape reads failed; the next ack retries");
            }
        }

        // Recorded only while the copy in place is still the one the batch came from; otherwise the cursor starts over.
        if (!await _source.AckAsync(database, incarnation, peer, (long)ack.ChangeId, new Hlc(ack.Stable), cancellationToken).ConfigureAwait(false))
        {
            Reset(link, cursor);
            return;
        }

        lock (link.Gate)
        {
            cursor.RequiresRefill = false;
            if (live)
            {
                cursor.Live = (incarnation, from);
            }
        }

        // The conditional writer accepted the pinned ACK. Recheck placement before releasing any client wait.
        if (live && (await PlacementAsync(database, cancellationToken).ConfigureAwait(false))?.Includes(link.Peer.Region) == true)
        {
            _acks.Acked(database, incarnation, from, (long)ack.ChangeId);
        }
    }

    // Forgets a cursor's incarnation and position: the next pass reads both again from the copy in place.
    private static void Reset(Link link, Cursor cursor)
    {
        lock (link.Gate)
        {
            (cursor.Incarnation, cursor.Acked, cursor.Head, cursor.RequiresRefill, cursor.Live) = (null, null, 0, false, null);
        }
    }

    // Whether the database's placement includes the peer's region, read again every heartbeat. The system database is placed
    // in every region.
    private async ValueTask<bool> PlacedAsync(Link link, TenantContext database, CancellationToken cancellationToken)
    {
        if (database == ChangeGroups.System)
        {
            return true;
        }

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

    // gRPC permits one pending write. Heartbeats can run beside storage reads, but never beside a batch write.
    private sealed class LinkWriter(IClientStreamWriter<PushFrame> stream, TimeProvider time, Func<ValueTask> reserve) : IDisposable
    {
        private readonly SemaphoreSlim _writing = new(1, 1);
        private long _lastWrite = time.GetTimestamp();

        public TimeSpan Quiet => time.GetElapsedTime(Interlocked.Read(ref _lastWrite));

        public ValueTask ReserveAsync() => reserve();

        public async Task WriteAsync(PushFrame frame, CancellationToken cancellationToken)
        {
            await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Interlocked.Exchange(ref _lastWrite, time.GetTimestamp());
                await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writing.Release();
            }
        }

        public void Dispose() => _writing.Dispose();
    }

    private sealed class Link(ClusterMember peer)
    {
        public ClusterMember Peer { get; } = peer;

        public Lock Gate { get; } = new();

        public bool Connected { get; set; }

        // The node the peer welcomed this connection with: the replica id its own commits carry. Null while not connected.
        public ReplicaId? Origin { get; set; }

        public Dictionary<TenantContext, Cursor> Cursors { get; } = [];

        // Only the link's own loop reads and writes these.
        public Dictionary<TenantContext, (bool Placed, long ReadAt)> Placements { get; } = [];
    }

    private sealed class Cursor
    {
        // The incarnation of this machine's copy the cursor belongs to and the peer's last ack in it (null until read from the
        // source on this connection, or after the copy was replaced), the batch in flight and when it went.
        public Ulid? Incarnation { get; set; }

        public bool RequiresRefill { get; set; }

        public long? Acked { get; set; }

        public long? InFlight { get; set; }

        // The peer answered, but the single consumer may still be waiting on local durable ACK processing.
        public bool ResponseReceived { get; set; }

        // Whether the batch in flight is a refill.
        public bool Refilling { get; set; }

        // One contiguous run of live batches; a reconnect, refill or other unconfirming ACK ends it.
        public (Ulid Incarnation, long From)? Live { get; set; }

        // When the last frontier went (a TimeProvider timestamp; 0 before the first).
        public long FrontierAt { get; set; }

        public int InFlightGroups { get; set; }

        public long SentAt { get; set; }

        public long RetryAt { get; set; }

        // The highest change id read, since when a backlog has existed, and how long the last one took to clear.
        public long Head { get; set; }

        public long? BehindSince { get; set; }

        public double? Convergence { get; set; }
    }
}
