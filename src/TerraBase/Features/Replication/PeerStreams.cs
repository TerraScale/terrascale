using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
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
/// groups of every database the peer's region holds, up to <see cref="ReplicationProtocol.Window"/> batches per database in
/// flight (one with a protocol 1 peer), resumes from the peer's last ack after a reconnect, and heartbeats while idle. Its cursors and lags are the outbound replication status.
/// </summary>
internal sealed class PeerStreams : BackgroundService, IReplicationStatus, IRemoteDurability
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);
    // A refused batch retries after FirstRetry, doubling up to MaxRetry, each wait jittered.
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(10);
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
    private readonly IReplicaCopyFaults _faults;
    private readonly IRebuildStorage _storage;
    private readonly ITenantDirectory _tenants;
    private readonly LocalReplica _self;
    private readonly TerraBaseOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PeerStreams> _logger;
    private readonly Counter<long> _unreadable;
    private readonly Histogram<double> _remoteAckDelay;
    private readonly ReplicationTimings _timings;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Link> _links = [with(StringComparer.Ordinal)];

    private readonly Lazy<ICatalogGate> _catalogGate;

    public PeerStreams(
        IMembership membership, IChangeGroupSource source, RemoteAckWaiter acks, IReplicaStates states, ITenantDirectory tenants, LocalReplica self, TerraBaseOptions options,
        TimeProvider time, IMeterFactory meters, ILogger<PeerStreams> logger, Lazy<ICatalogGate> catalogGate, PeerCredential credential, IReplicaCopyFaults faults, IRebuildStorage storage,
        ReplicationTimings timings)
    {
        _credential = credential;
        _timings = timings;
        (_membership, _source, _acks, _states, _tenants, _self, _options, _time, _logger) =
            (membership, source, acks, states, tenants, self, options, time, logger);
        _catalogGate = catalogGate;
        _faults = faults;
        _storage = storage;
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.replication.send_lag", () => Lags(cursor => cursor.BehindSince), "s",
            "Age of the oldest change group not yet acknowledged by the peer region's machines.");
        _unreadable = meter.CreateCounter<long>("terrabase.replication.unreadable_reads", "{read}",
            "Reads of a database's change log that failed (a damaged row is refused), by the peer region the batch was for.");
        _remoteAckDelay = meter.CreateHistogram<double>("terrabase.replication.remote_ack_delay", "s",
            "From a change group's stamp on this machine to the peer region's durable ack of it, by the peer region.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = ReplicationTimings.Buckets });
        meter.CreateObservableGauge("terrabase.replication.ack_lag", () => Lags(cursor => cursor.Flights.Count == 0 ? null : cursor.Flights[0].SentAt), "s",
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

    public async ValueTask<DatabasePlacement?> PlacementAsync(TenantContext database, CancellationToken cancellationToken) =>
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
                        cursor.Flights.Sum(flight => flight.Groups), Diverged: false, Age(cursor.BehindSince), cursor.Convergence));
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
                _self.Presence.Listed(view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere)
                    .Where(member => member.Name != self).Select(member => (member.Name, member.Region)), _self.Clock.Tick().Milliseconds);
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

    // Heartbeats are answered even when a tenant frontier is refused, so silence for the progress timeout means a half-open
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
            Hello = new Hello
            {
                Node = _self.Id.Value,
                Hlc = _self.Clock.Tick().Value,
                Region = _options.Region,
                Machine = _membership.Self.Name,
                Protocol = ReplicationProtocol.Version,
            },
        }, cancellationToken).ConfigureAwait(false);
        if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false) || call.ResponseStream.Current.Welcome is not { Node: not 0 })
        {
            throw new IOException("the peer did not welcome the stream");
        }

        silent.CancelAfter(ReplicationProtocol.ProgressTimeout);

        var peer = new ReplicaId(call.ResponseStream.Current.Welcome.Node);
        _self.Presence.Heard(link.Peer.Name, peer, _self.Clock.Tick().Milliseconds);
        lock (link.Gate)
        {
            (link.Connected, link.Origin, link.Protocol) = (true, peer, ReplicationProtocol.Negotiate(call.ResponseStream.Current.Welcome.Protocol));
            foreach (var cursor in link.Cursors.Values)
            {
                (cursor.Incarnation, cursor.Acked, cursor.RequiresRefill, cursor.Live, cursor.Rewinding) = (null, null, false, null, false);
                cursor.Flights.Clear();
            }
        }

        welcomed();
        using var activity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Each response with when it arrived, for the ack step's timing.
        var responses = Channel.CreateBounded<(AckFrame Frame, long At)>(2 * MaxPendingResponses);
        var reading = responses.Reader.ReadAsync(activity.Token).AsTask();
        var pending = 0;
        ExceptionDispatchInfo? failure = null;
        var ledger = new Ledger();
        var positions = new Dictionary<TenantContext, Task<SourceCursor?>>();
        var positionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writer = new LinkWriter(call.RequestStream, _time, ReserveAsync, () => pending--);
        var receiving = ReceiveAsync();
        var heartbeats = HeartbeatAsync();
        var progress = ProgressAsync();
        var bookkeeping = BookkeepAsync();
        Task? changed = null;
        try
        {
            while (true)
            {
                activity.Token.ThrowIfCancellationRequested();
                if (positionReady.Task.IsCompleted)
                {
                    Volatile.Write(ref positionReady, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }
                // Asked for before the pass reads, so a commit during the pass wakes the next one.
                changed = changed is null || changed.IsCompleted ? _source.ChangedAsync(activity.Token) : changed;
                var pass = _timings.Now;
                _ = await ShipAsync(link, writer, positions, InitializeAsync, DrainAsync, activity.Token).ConfigureAwait(false);
                _ = _timings.Record(ReplicationTimings.SendPass, pass);

                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(activity.Token))
                {
                    _ = await Task.WhenAny(reading, changed, positionReady.Task, Task.Delay(Wake(link), _time, idle.Token)).ConfigureAwait(false);
                    await idle.CancelAsync().ConfigureAwait(false);
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
            await bookkeeping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await ((Task)reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await ((Task)Task.WhenAll(positions.Values)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        Task<SourceCursor?> InitializeAsync(TenantContext database)
        {
            var position = _source.AckedAsync(database, peer, activity.Token).AsTask();
            _ = position.ContinueWith(_ => Volatile.Read(ref positionReady).TrySetResult(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return position;
        }

        // Process each database's responses before shipping the next one: a large placement cannot fill the queue while
        // the pass waits on another database's writer. InFlight was published before this consumer sees a batch's ack.
        // Nothing here takes a database's writer: the ledger records what needs one.
        async ValueTask DrainAsync()
        {
            while (reading.IsCompleted)
            {
                var (response, at) = await reading.ConfigureAwait(false);
                if (response.Stable is { } report)
                {
                    var database = ChangeGroups.Tenant(report.Database);
                    var incarnation = ChangeGroups.RequiredIncarnation(report.Database);
                    lock (link.Gate)
                    {
                        if (link.Cursors.GetValueOrDefault(database) is { } cursor && cursor.Incarnation == incarnation)
                        {
                            cursor.RequiresRefill |= report.RefillRequired;
                            if (!report.RefillRequired)
                            {
                                cursor.Stable = new Hlc(Math.Max(cursor.Stable.Value, report.Stable));
                            }
                        }
                    }

                    if (report.RefillRequired)
                    {
                        reading = responses.Reader.ReadAsync(activity.Token).AsTask();
                        continue;
                    }

                    // A report for a frontier of a replaced copy says nothing of the copy in place: the source refuses it.
                    await ledger.AddAsync(
                        ChangeGroups.Tenant(report.Database), ChangeGroups.RequiredIncarnation(report.Database),
                        null, null, report.Stable, activity.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    if (pending == 0)
                    {
                        throw new InvalidDataException("the peer sent a response with no frame outstanding");
                    }

                    await AckedAsync(link, response.Ack, at, ledger, activity.Token).ConfigureAwait(false);
                    pending--;
                }

                reading = responses.Reader.ReadAsync(activity.Token).AsTask();
            }
        }

        // At most 32 batches/refills await durable processing, leaving room to read their replies and heartbeats
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
                if (link.Cursors.Values.Any(cursor => cursor.Flights.Any(flight => !flight.ResponseReceived
                    && _time.GetElapsedTime(flight.SentAt) >= (flight.Refill ? RefillTimeout : ReplicationProtocol.ProgressTimeout))))
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
                        if (frame.Stable is not null)
                        {
                            // Keep half the buffer available for mandatory ACKs; optional stable proofs may be omitted.
                            _ = responses.Reader.Count < MaxPendingResponses && responses.Writer.TryWrite((frame, _timings.Now));
                            continue;
                        }

                        if (frame.Ack is { } ack)
                        {
                            var database = ChangeGroups.Tenant(ack.Database);
                            lock (link.Gate)
                            {
                                var cursor = link.Cursors.GetValueOrDefault(database);
                                if (cursor?.Incarnation is { } pinned && ChangeGroups.RequiredIncarnation(ack.Database) == pinned && cursor.Answered(ack) is { } flight)
                                {
                                    // Receipt satisfies the peer's deadline. It never advances the durable cursor.
                                    flight.ResponseReceived = true;
                                }
                            }
                        }

                        await responses.Writer.WriteAsync((frame, _timings.Now), activity.Token).ConfigureAwait(false);
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

        // A busy writer delays resume cursors, retention and shape publication, never waits an ACK released.
        // Tenant bookkeeping failures affect only that copy; node faults fail the whole stream closed.
        async Task BookkeepAsync()
        {
            try
            {
                while (true)
                {
                    foreach (var ((database, incarnation), due) in await ledger.TakeAsync(activity.Token).ConfigureAwait(false))
                    {
                        try
                        {
                            // Prove before recording the ACK, which can trim the change log through this frontier (FR-25).
                            foreach (var (from, through) in due.Proofs)
                            {
                                try
                                {
                                    var held = await HeldOriginsAsync(database, activity.Token).ConfigureAwait(false);
                                    _ = await _source.ProveAsync(database, incarnation, from, through, held, activity.Token)
                                        .ConfigureAwait(false);
                                }
                                catch (Exception error) when (!activity.IsCancellationRequested)
                                {
                                    if (database == ChangeGroups.System
                                        || await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, activity.Token).ConfigureAwait(false))
                                    {
                                        throw;
                                    }

                                    _logger.ZLogWarning(error,
                                        $"Recording a remote durable proof for shape reads failed; the next ack retries");
                                }
                            }

                            var recorded = due.Acked is { } acked
                                ? await _source.AckAsync(database, incarnation, peer, acked, new Hlc(due.Stable), activity.Token)
                                    .ConfigureAwait(false)
                                : await _source.RecordStableAsync(database, incarnation, peer, new Hlc(due.Stable), activity.Token)
                                    .ConfigureAwait(false);
                            if (!recorded && MatchingCursor(database, incarnation) is { } cursor)
                            {
                                if (due.Acked is not null)
                                {
                                    // A newer in-flight batch keeps its scope; its ACK and the next source read recheck faults.
                                    Reset(link, cursor, incarnation);
                                }
                                else
                                {
                                    await HoldOrResetAsync(link, database, cursor, activity.Token, incarnation).ConfigureAwait(false);
                                }
                            }
                        }
                        catch (Exception error) when (!activity.IsCancellationRequested)
                        {
                            if (database == ChangeGroups.System
                                || await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, activity.Token).ConfigureAwait(false))
                            {
                                throw;
                            }

                            if (MatchingCursor(database, incarnation) is { } cursor)
                            {
                                if (due.Acked is not null)
                                {
                                    lock (link.Gate)
                                    {
                                        if (cursor.Incarnation == incarnation)
                                        {
                                            Reset(link, cursor, incarnation);
                                            cursor.RetryAt = _time.GetTimestamp() + (long)(MaxBackoff.TotalSeconds * _time.TimestampFrequency);
                                        }
                                    }
                                }
                                else
                                {
                                    await HoldOrResetAsync(link, database, cursor, activity.Token, incarnation).ConfigureAwait(false);
                                }
                            }

                            _logger.ZLogWarning(error, $"Peer bookkeeping for database {database.DatabaseId:@Database} from machine {link.Peer.Name:@Peer} failed; other databases continue");
                        }
                    }
                }
            }
            catch (Exception error)
            {
                await FailAsync(error).ConfigureAwait(false);
            }
        }

        Cursor? MatchingCursor(TenantContext database, Ulid incarnation)
        {
            lock (link.Gate)
            {
                var cursor = link.Cursors.GetValueOrDefault(database);
                return cursor?.Incarnation == incarnation ? cursor : null;
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
    private async ValueTask<bool> ShipAsync(
        Link link, LinkWriter stream, Dictionary<TenantContext, Task<SourceCursor?>> positions,
        Func<TenantContext, Task<SourceCursor?>> initialize, Func<ValueTask> drain, CancellationToken cancellationToken)
    {
        var sent = false;
        link.Seen.Clear();
        await using var databases = _source.ListDatabasesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            await drain().ConfigureAwait(false);
            if (!await databases.MoveNextAsync().ConfigureAwait(false))
            {
                break;
            }

            var database = databases.Current;
            link.Seen.Add(database);
            if (database == ChangeGroups.System && !_catalogGate.Value.Trusted
                || !await PlacedAsync(link, database, cancellationToken).ConfigureAwait(false))
            {
                if (positions.TryGetValue(database, out var skipped) && skipped.IsCompleted)
                {
                    await ((Task)skipped).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    positions.Remove(database);
                }

                continue;
            }

            Cursor cursor;
            lock (link.Gate)
            {
                cursor = link.Cursors.TryGetValue(database, out var known) ? known : link.Cursors[database] = new Cursor();
            }

            Ulid incarnation;
            long from;
            bool flying;
            SourceGroups read;
            bool retained;
            long head = 0;
            long refillResume = 0;
            Hlc horizon = default;
            var frontier = _self.Clock.Tick();
            try
            {
                SourceCursor? position;
                if (cursor is { Incarnation: { } pinned, Acked: { } resume })
                {
                    position = new SourceCursor(pinned, resume) { RequiresRefill = cursor.RequiresRefill, Stable = cursor.Stable };
                }
                else
                {
                    if (!positions.TryGetValue(database, out var initializing))
                    {
                        if (positions.Count == MaxPendingResponses)
                        {
                            continue;
                        }

                        // Initial retention registration may need this tenant's writer; other tenants keep shipping.
                        initializing = initialize(database);
                        positions.Add(database, initializing);
                        continue;
                    }

                    if (!initializing.IsCompleted)
                    {
                        continue;
                    }

                    positions.Remove(database);
                    position = await initializing.ConfigureAwait(false);
                }
                if (position is not (var found, var acked))
                {
                    await HoldOrResetAsync(link, database, cursor, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                incarnation = found;
                if (cursor.Incarnation is { } previous && previous != incarnation)
                {
                    Reset(link, cursor);
                }
                lock (link.Gate)
                {
                    (cursor.Incarnation, cursor.Acked, cursor.RequiresRefill, cursor.Stable) = (incarnation, acked, position.Value.RequiresRefill, position.Value.Stable);
                }

                // Past the window, a refill or refused batches still answering, or a retry wait, the database waits. Otherwise
                // the next batch follows the newest one in flight, or the ack.
                lock (link.Gate)
                {
                    cursor.Rewinding &= cursor.Flights.Count > 0;
                    if (cursor.Flights.Count >= (link.Protocol >= 2 ? ReplicationProtocol.Window : 1) || cursor.Rewinding
                        || cursor.Flights.Any(flight => flight.Refill) || _time.GetTimestamp() < cursor.RetryAt)
                    {
                        continue;
                    }

                    flying = cursor.Flights.Count > 0;
                    from = flying ? cursor.Flights[^1].Last : acked;
                }

                // Read before retention: a cut between them must never ship a batch past a hole.
                var readAt = _timings.Now;
                var page = await _source.ReadAsync(database, incarnation, from, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, cancellationToken).ConfigureAwait(false);
                if (page is not null && page.Incarnation != incarnation)
                {
                    Reset(link, cursor);
                    continue;
                }

                var retains = page is null ? null
                    : await _source.RetainsAsync(database, incarnation, from, cancellationToken).ConfigureAwait(false);
                if (page is null || retains is null)
                {
                    await HoldOrResetAsync(link, database, cursor, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                read = page;
                retained = retains.Value;
                if (page.Groups.Count > 0)
                {
                    _ = _timings.Record(ReplicationTimings.SendRead, readAt);
                }

                if ((!retained || cursor.RequiresRefill) && flying)
                {
                    // A refill starts only from the ack, once the batches in flight answered.
                    continue;
                }

                if (!retained || cursor.RequiresRefill)
                {
                    if (await _source.HeadAsync(database, incarnation, cancellationToken).ConfigureAwait(false) is not { } heldHead)
                    {
                        await HoldOrResetAsync(link, database, cursor, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    head = heldHead;
                    // A whole copied log resumes at the ack so post-restore writes still receive live durable acknowledgements.
                    refillResume = retained ? acked : head;
                    // A faulted frontier throws: no refill with a fabricated zero horizon can be sent.
                    horizon = await _states.HorizonAsync(database, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                if (database == ChangeGroups.System
                    || await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }

                _logger.ZLogError(error, $"The change log of database {database.DatabaseId:@Database} could not be read for machine {link.Peer.Name:@Peer} in {link.Peer.Region:@Region}; that database is held and read again");
                _unreadable.Add(1, new KeyValuePair<string, object?>("peer_region", link.Peer.Region));
                await HoldOrResetAsync(link, database, cursor, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var groups = read.Groups;
            if (!retained || cursor.RequiresRefill)
            {
                await stream.ReserveAsync().ConfigureAwait(false);
                lock (link.Gate)
                {
                    if (cursor.Incarnation != incarnation)
                    {
                        stream.Release();
                        continue;
                    }

                    cursor.BehindSince ??= _time.GetTimestamp();
                    cursor.Head = Math.Max(cursor.Head, head);
                    cursor.Flights.Add(new Flight(refillResume, from, 0, _time.GetTimestamp(), null, Refill: true));
                }
                await stream.WriteAsync(new PushFrame
                {
                    Refill = new Refill { Database = ChangeGroups.Wire(database, incarnation), ChangeId = checked((ulong)refillResume), Horizon = horizon.Value, Coverage = frontier.Value },
                }, cancellationToken).ConfigureAwait(false);

                sent = true;
                continue;
            }

            if (groups.Count == 0)
            {
                // A frontier claims every group below it held: only once nothing is in flight.
                if (flying)
                {
                    continue;
                }

                if (_time.GetElapsedTime(cursor.FrontierAt) >= FrontierEvery || cursor.FrontierAt == 0)
                {
                    await stream.WriteAsync(new PushFrame { Frontier = new Frontier { Database = ChangeGroups.Wire(database, incarnation), Hlc = frontier.Value, Expect = cursor.Stable.Value } }, cancellationToken)
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
                if (cursor.Incarnation != incarnation)
                {
                    stream.Release();
                    continue;
                }

                cursor.BehindSince ??= _time.GetTimestamp();
                cursor.Head = Math.Max(cursor.Head, last);
                cursor.Flights.Add(new Flight(last, from, groups.Count, _time.GetTimestamp(), Stamps(groups), Refill: false));
            }
            await stream.WriteAsync(new PushFrame
            {
                Batch = new Batch
                {
                    Database = ChangeGroups.Wire(database, incarnation),
                    LastChangeId = checked((ulong)last),
                    Groups = ChangeGroups.Encode(groups),
                    After = checked((ulong)from),
                    Expect = cursor.Stable.Value,
                },
            }, cancellationToken).ConfigureAwait(false);

            sent = true;
        }

        lock (link.Gate)
        {
            // Keep outstanding batches scoped until their ACK arrives, even after their database disappears.
            foreach (var (database, cursor) in link.Cursors)
            {
                if (!link.Seen.Contains(database) && cursor.Flights.Count == 0)
                {
                    link.Cursors.Remove(database);
                }
            }
        }

        foreach (var database in link.Placements.Keys)
        {
            if (!link.Seen.Contains(database))
            {
                link.Placements.Remove(database);
            }
        }

        foreach (var (database, position) in positions)
        {
            if (!link.Seen.Contains(database) && position.IsCompleted)
            {
                await ((Task)position).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                positions.Remove(database);
            }
        }

        return sent;
    }

    private async ValueTask AckedAsync(Link link, Ack? ack, long at, Ledger ledger, CancellationToken cancellationToken)
    {
        var database = ChangeGroups.Tenant(ack?.Database);
        Ulid incarnation;
        bool live;
        long from;
        long[]? stamps;
        lock (link.Gate)
        {
            var cursor = link.Cursors.GetValueOrDefault(database);
            // An ack covers every batch before it: it answers the oldest one in flight, never one a refusal before it doomed.
            if (cursor?.Incarnation is not { } pinned || ChangeGroups.RequiredIncarnation(ack!.Database) != pinned
                || cursor.Answered(ack) is not { } flight || (!ack.Refused && flight.Doomed))
            {
                throw new InvalidDataException("the peer acknowledged a batch that is not in flight");
            }

            incarnation = pinned;
            // Capture the original batch kind before clearing it: stored positions and uncertified refills never confirm.
            live = !flight.Refill && link.Peer.Region != _options.Region && link.Placements.GetValueOrDefault(database).Placed;
            from = cursor.Live is { } run && run.Incarnation == incarnation ? run.From : cursor.Acked ?? flight.Last;
            stamps = flight.Stamps;
            cursor.Flights.RemoveAt(0);
            if (ack.Refused)
            {
                // Catalog lag only retries; missing coverage requests a pinned refill even without a stored peer scope. The
                // peer refuses every batch after this one too: the database goes on from its ack once they all answered.
                cursor.RequiresRefill |= ack.RefillRequired;
                cursor.Flights.ForEach(later => later.Doomed = true);
                cursor.Rewinding = true;
                cursor.Backoff = TimeSpan.FromTicks(Math.Clamp(cursor.Backoff.Ticks * 2, FirstRetry.Ticks, MaxRetry.Ticks));
                cursor.RetryAt = _time.GetTimestamp() + (long)(Jittered(cursor.Backoff).TotalSeconds * _time.TimestampFrequency);
                return;
            }

            cursor.Stable = new Hlc(Math.Max(cursor.Stable.Value, ack.Stable));
            // The stream goes on from the peer's ack in memory; the ledger persists it behind the stream.
            (cursor.Acked, cursor.Live, cursor.RequiresRefill, cursor.Backoff) = (flight.Last, live ? (incarnation, from) : null, false, TimeSpan.Zero);
        }

        // The peer holds the range durably whatever this machine's bookkeeping does: a client wait needs only placement and
        // the copy the batch came from still in place (HeadAsync reads its incarnation without the writer).
        var placed = live && (await PlacementAsync(database, cancellationToken).ConfigureAwait(false))?.Includes(link.Peer.Region) == true;
        long? head = null;
        if (placed)
        {
            try
            {
                head = await _source.HeadAsync(database, incarnation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                if (database == ChangeGroups.System
                    || await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }

                Cursor? cursor;
                lock (link.Gate)
                {
                    cursor = link.Cursors.GetValueOrDefault(database);
                    if (cursor?.Incarnation == incarnation)
                    {
                        Reset(link, cursor);
                        cursor.RetryAt = _time.GetTimestamp() + (long)(MaxBackoff.TotalSeconds * _time.TimestampFrequency);
                    }
                }

                _logger.ZLogWarning(error, $"The acknowledgement of database {database.DatabaseId:@Database} from machine {link.Peer.Name:@Peer} could not be validated; other databases continue");
            }
        }

        if (head is not null)
        {
            _acks.Acked(database, incarnation, from, (long)ack.ChangeId);
        }

        _ = _timings.Record(ReplicationTimings.SendAck, at);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var region = new KeyValuePair<string, object?>("peer_region", link.Peer.Region);
        foreach (var stamp in stamps ?? [])
        {
            _remoteAckDelay.Record(Math.Max(0, now - stamp) / 1000.0, region);
        }

        await ledger.AddAsync(
            database, incarnation, head is not null ? (from, (long)ack.ChangeId) : null, (long)ack.ChangeId, ack.Stable, cancellationToken)
            .ConfigureAwait(false);
    }

    // RefusedAsync also covers replacement; only the same observed, refused tenant copy retains its durable ACK.
    private async ValueTask HoldOrResetAsync(Link link, TenantContext database, Cursor cursor, CancellationToken cancellationToken, Ulid? expected = null)
    {
        if (await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false))
        {
            throw new ReplicaCopyFaultedException("storage failed closed; this node cannot supply replication data");
        }

        var knownTenantRefusal = false;
        try
        {
            // A reconnect may meet a known fault before it can read a cursor: discover only its scope, never an ACK.
            var observed = database == ChangeGroups.System ? null
                : cursor.Incarnation ?? await _storage.IncarnationAsync(database, cancellationToken).ConfigureAwait(false);
            knownTenantRefusal = database != ChangeGroups.System && observed is { } incarnation
                && await _storage.IncarnationAsync(database, cancellationToken).ConfigureAwait(false) == incarnation
                && await _faults.RefusedAsync(database, incarnation, cancellationToken).ConfigureAwait(false)
                && await _storage.IncarnationAsync(database, cancellationToken).ConfigureAwait(false) == incarnation;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            if (database == ChangeGroups.System
                || await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            _logger.ZLogWarning(error, $"The scope of database {database.DatabaseId:@Database} could not be checked; its cursor remains neutral and other databases continue");
        }

        if (await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false))
        {
            throw new ReplicaCopyFaultedException("storage failed closed; this node cannot supply replication data");
        }

        if (!knownTenantRefusal)
        {
            Reset(link, cursor, expected);
            return;
        }

        lock (link.Gate)
        {
            if (expected is not null && cursor.Incarnation != expected)
            {
                return;
            }

            cursor.BehindSince ??= _time.GetTimestamp();
            cursor.RetryAt = _time.GetTimestamp() + (long)(MaxBackoff.TotalSeconds * _time.TimestampFrequency);
        }
    }

    // Forgets a cursor's incarnation and position: the next pass reads both again from the copy in place.
    private static void Reset(Link link, Cursor cursor, Ulid? expected = null)
    {
        lock (link.Gate)
        {
            // A late optional stable report cannot erase the scope needed to validate an outstanding batch's ACK.
            if (cursor.Flights.Count > 0 || (expected is not null && cursor.Incarnation != expected))
            {
                return;
            }

            (cursor.Incarnation, cursor.Acked, cursor.Head, cursor.RequiresRefill, cursor.Live, cursor.Stable) = (null, null, 0, false, null, default);
            (cursor.FrontierAt, cursor.BehindSince, cursor.Convergence) = (0, null, null);
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

    // Half to all of the delay, so retries of many databases or links spread out.
    private static TimeSpan Jittered(TimeSpan delay) => delay * (RandomNumberGenerator.GetInt32(500, 1001) / 1000.0);

    // The loop's idle wait: at most Idle, less when a database's retry is due sooner.
    private TimeSpan Wake(Link link)
    {
        lock (link.Gate)
        {
            var now = _time.GetTimestamp();
            var due = link.Cursors.Values.Select(cursor => cursor.RetryAt).Where(at => at > now).DefaultIfEmpty(long.MaxValue).Min();
            return due == long.MaxValue ? Idle : TimeSpan.FromTicks(Math.Min(Idle.Ticks, _time.GetElapsedTime(now, due).Ticks));
        }
    }

    // Each group's newest stamp, in Unix milliseconds: when it was written on this machine.
    private static long[] Stamps(IReadOnlyList<OriginGroup> groups)
    {
        var stamps = new long[groups.Count];
        for (var index = 0; index < groups.Count; index++)
        {
            foreach (var delta in groups[index].Deltas)
            {
                stamps[index] = Math.Max(stamps[index], delta.Stamp.Milliseconds);
            }
        }

        return stamps;
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
    private sealed class LinkWriter(IClientStreamWriter<PushFrame> stream, TimeProvider time, Func<ValueTask> reserve, Action release) : IDisposable
    {
        private readonly SemaphoreSlim _writing = new(1, 1);
        private long _lastWrite = time.GetTimestamp();

        public TimeSpan Quiet => time.GetElapsedTime(Interlocked.Read(ref _lastWrite));

        public ValueTask ReserveAsync() => reserve();

        public void Release() => release();

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

    // One stream's responses that need a database's writer, coalesced per copy: the live ranges to prove, the newest ack and
    // the highest stable stamp. Pending and active batches each hold at most MaxDue copies of at most
    // RemoteAckWaiter.MaxRanges ranges; the consumer waits for capacity rather than dropping a proof.
    private sealed class Ledger
    {
        private const int MaxDue = 1024;

        private readonly Lock _gate = new();
        private Dictionary<(TenantContext Database, Ulid Incarnation), Due> _due = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask AddAsync(
            TenantContext database, Ulid incarnation, (long From, long To)? proof,
            long? acked, ulong stable, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if ((_due.TryGetValue((database, incarnation), out var due) || _due.Count < MaxDue)
                        && (due is null || due.CanKeep(proof)))
                    {
                        due ??= _due[(database, incarnation)] = new Due();
                        if (proof is { } range)
                        {
                            RemoteAckWaiter.Keep(due.Proofs, range.From, range.To);
                        }

                        (due.Acked, due.Stable) = (acked ?? due.Acked, Math.Max(due.Stable, stable));
                        Signal();
                        return;
                    }

                    changed = _changed.Task;
                }

                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask<Dictionary<(TenantContext Database, Ulid Incarnation), Due>> TakeAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_due.Count > 0)
                    {
                        var taken = _due;
                        _due = [];
                        Signal();
                        return taken;
                    }

                    changed = _changed.Task;
                }

                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // The caller holds _gate.
        private void Signal()
        {
            _changed.TrySetResult();
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class Due
    {
        public List<(long From, long To)> Proofs { get; } = [];

        public bool CanKeep((long From, long To)? proof) =>
            proof is not { } range || Proofs.Count < RemoteAckWaiter.MaxRanges
            || Proofs.Any(known => range.From <= known.To && known.From <= range.To);

        // The newest ack of the copy, or null when only a stable report is due.
        public long? Acked { get; set; }

        public ulong Stable { get; set; }
    }

    private sealed class Link(ClusterMember peer)
    {
        public ClusterMember Peer { get; } = peer;

        public Lock Gate { get; } = new();

        public bool Connected { get; set; }

        // The node the peer welcomed this connection with: the replica id its own commits carry. Null while not connected.
        public ReplicaId? Origin { get; set; }

        // The protocol this connection uses (ReplicationProtocol.Negotiate of the peer's Welcome).
        public uint Protocol { get; set; }

        public Dictionary<TenantContext, Cursor> Cursors { get; } = [];

        // Only the link's own loop reads and writes these.
        public Dictionary<TenantContext, (bool Placed, long ReadAt)> Placements { get; } = [];

        public HashSet<TenantContext> Seen { get; } = [];
    }

    // One batch or refill in flight: the change id it ends at and the one it follows, its groups, when it went, its groups'
    // stamps (for the remote ack delay; null for a refill), whether the peer answered, and whether a refusal of an earlier
    // batch doomed it (the peer refuses it too).
    private sealed record Flight(long Last, long After, int Groups, long SentAt, long[]? Stamps, bool Refill)
    {
        public bool ResponseReceived { get; set; }

        public bool Doomed { get; set; }
    }

    private sealed class Cursor
    {
        // The incarnation of this machine's copy the cursor belongs to and the peer's last ack in it (null until read from the
        // source on this connection, or after the copy was replaced), the batch in flight and when it went.
        public Ulid? Incarnation { get; set; }

        public bool RequiresRefill { get; set; }

        public Hlc Stable { get; set; }

        public long? Acked { get; set; }

        // The batches (or the one refill) sent and not answered yet, oldest first.
        public List<Flight> Flights { get; } = [];

        // A batch was refused: the database waits until every batch in flight answered, then resends from the ack.
        public bool Rewinding { get; set; }

        // The retry delay after the last refusal (zero after an applied ack).
        public TimeSpan Backoff { get; set; }

        // The flight an answer is for: the oldest, as the peer answers a database's batches in order; an applied ack names it.
        public Flight? Answered(Ack ack) =>
            Flights.FirstOrDefault() is { } oldest && (ack.Refused || (ulong)oldest.Last == ack.ChangeId) ? oldest : null;

        // One contiguous run of live batches; a reconnect, refill or other unconfirming ACK ends it.
        public (Ulid Incarnation, long From)? Live { get; set; }

        // When the last frontier went (a TimeProvider timestamp; 0 before the first).
        public long FrontierAt { get; set; }

        public long RetryAt { get; set; }

        // The highest change id read, since when a backlog has existed, and how long the last one took to clear.
        public long Head { get; set; }

        public long? BehindSince { get; set; }

        public double? Convergence { get; set; }
    }
}
