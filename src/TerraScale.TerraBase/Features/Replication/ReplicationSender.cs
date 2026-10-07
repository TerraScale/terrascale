using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// Sends this node's captured change groups to every peer in <see cref="TerraBaseOptions.PeerAddresses"/>
/// (full mesh: each node sends only the groups it originated). Per peer, a database with new commits gets
/// one Sync stream that resumes after the peer's applied prefix from <c>Welcome</c>, sends groups in order
/// within the in-flight window, and closes once every group is durably acknowledged. Failures retry with
/// capped exponential backoff and jitter; the next stream resumes from the peer's prefix again. The durable
/// lag gauge counts committed groups that some peer has not durably acknowledged yet, and a client write waits for
/// those acknowledgments before it answers (<see cref="IReplicationWait"/>).
/// </summary>
/// <remarks>
/// ponytail: one database at a time per peer; run several streams per peer if many databases lag at once.
/// </remarks>
internal sealed partial class ReplicationSender(
    IStorageEngine engine, ITenantDirectory tenants, PeerCredentials credentials, TerraBaseOptions options, ReplicationMetrics metrics, ILogger<ReplicationSender> logger,
    TimeProvider time)
    : BackgroundService, IReplicationWait, IReplicationStatus, IReplicationPressure
{
    // Groups one stream sends before the database goes to the back of the queue, so a database that is
    // written as fast as it replicates cannot starve the others.
    private const int MaxGroupsPerTurn = 256;
    internal const int MaxObservedDatabases = 128;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(5);

    // All sender state shares this short lock, so deleting an incarnation also discards pending work and
    // acknowledgments; a late stream or notification cannot revive it.
    private readonly Lock _state = new();
    private readonly Dictionary<DatabaseIncarnation, long> _heads = [];
    private readonly Dictionary<DatabaseIncarnation, int> _slots = [];
    // Never decreases: retired slots keep emitting zeroes without creating series for unused slots.
    private int _allocatedSlotCount;
    private Peer[] _peers = [];
    private int _initialized;
    private int _disposed;

    // Completed and replaced, under _state, whenever a peer's acknowledged prefix or failure changes; waiting writes check again.
    // ponytail: one signal wakes every waiting write; per-database signals if many concurrent writers wake each other.
    private TaskCompletionSource _progress = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct DatabaseIncarnation(string Organization, string Project, string Database, Guid Origin)
    {
        public TenantContext Tenant => new(Organization, PlanTier.Hobby, Project, Database);
    }

    public long? SenderDurableLagSeq
    {
        get
        {
            lock (_state)
            {
                return Volatile.Read(ref _initialized) == 0 ? null : DurableLag();
            }
        }
    }

    private long DurableLag()
    {
        lock (_state)
        {
            var total = 0L;
            foreach (var peer in _peers)
            {
                total = SaturatingAdd(total, peer.Lag());
            }

            return total;
        }
    }

    private static long SaturatingAdd(long total, long value) => total > long.MaxValue - value ? long.MaxValue : total + value;

    public ReplicationStatus Snapshot()
    {
        lock (_state)
        {
            return new(true, Volatile.Read(ref _initialized) == 1, _heads.Count, _heads.Count - _slots.Count,
                [.. _peers.Select(peer => peer.Snapshot())]);
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        metrics.ObserveStatus(
            () => DatabaseMeasurements(status => status.Lag),
            () => DatabaseMeasurements(status => status.Diverged ? 1 : 0),
            () => DatabaseMeasurements(status => status.CatchupSeconds),
            PeerLagMeasurements, OmittedDatabases);
        metrics.ObserveDurableLag(DurableLag, () => Volatile.Read(ref _initialized));
        // Here, not in ExecuteAsync (which runs later on the thread pool): hosted services start before the server listens,
        // so every client write commits after its peers exist and its head is tracked, and write-wait covers it.
        var peers = new List<Peer>();
        try
        {
            foreach (var (address, number) in options.PeerAddresses.Select((address, number) => (address, number)))
            {
                peers.Add(CreatePeer(number, address));
            }
        }
        catch
        {
            peers.ForEach(peer => peer.Dispose());
            throw;
        }

        Volatile.Write(ref _peers, [.. peers]);
        // Subscribe before listing, so no commit falls between the two.
        // Deletions are observed before commits, so a commit during subscription cannot leave retired state.
        engine.DatabaseDeleted += OnDeleted;
        engine.ChangesCommitted += OnCommitted;
        return base.StartAsync(cancellationToken);
    }

    // The container disposes it once per registration (the sender, its interfaces and IHostedService).
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        engine.ChangesCommitted -= OnCommitted;
        engine.DatabaseDeleted -= OnDeleted;
        foreach (var peer in Volatile.Read(ref _peers))
        {
            peer.Dispose();
        }

        base.Dispose();
    }

    private void OnCommitted(object? sender, ChangeGroupCommitted committed)
    {
        lock (_state)
        {
            // A commit on an unlinked file may finish after deletion; it belongs to the closed incarnation.
            if (committed.Database.IsClosed)
            {
                return;
            }

            var identity = RaiseHead(committed.Database, committed.Sequence);
            foreach (var peer in _peers)
            {
                peer.MarkDirty(identity);
            }
        }
    }

    private void OnDeleted(object? sender, IDatabaseStore store) => Drop(Identity(store));

    // Discards an incarnation's head, pending work and acknowledgments for every peer.
    private void Drop(DatabaseIncarnation identity)
    {
        lock (_state)
        {
            _heads.Remove(identity);
            _slots.Remove(identity);
            foreach (var peer in _peers)
            {
                peer.Forget(identity);
            }
        }
    }

    public async ValueTask WaitAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        if (options.WriteWait <= TimeSpan.Zero
            || await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return;
        }

        // The caller's commit raised the head before it returned, so this head covers its group.
        var identity = Identity(store);
        long head;
        lock (_state)
        {
            if (!_heads.TryGetValue(identity, out head))
            {
                return;
            }
        }

        using var expiry = new CancellationTokenSource(options.WriteWait, time);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        while (true)
        {
            Task progress;
            lock (_state)
            {
                var peers = Volatile.Read(ref _peers);
                if (peers.All(peer => peer.Covers(identity, head)))
                {
                    // Degraded: a failing peer was skipped, so not every member holds the write.
                    metrics.WriteWaited(time.GetElapsedTime(started), peers.All(peer => peer.Acknowledges(identity, head)) ? "acknowledged" : "degraded");
                    return;
                }

                progress = _progress.Task;
            }

            try
            {
                await progress.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (expiry.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The write is committed and keeps replicating; failing it now would invite a retry of a write that succeeded.
                metrics.WriteWaited(time.GetElapsedTime(started), "timeout");
                return;
            }
        }
    }

    private void Progressed()
    {
        lock (_state)
        {
            _progress.SetResult();
            _progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var peers = Volatile.Read(ref _peers).ToList();
        try
        {
            // SQLite async APIs can block their caller. Start every peer independently before discovery
            // so healthy discovered databases and new commits can progress during another database's open.
            var workers = peers.Select(peer => Task.Run(() => RunAsync(peer, stoppingToken))).ToArray();
            await Task.WhenAll(workers.Append(ReconcileAsync(peers, stoppingToken))).ConfigureAwait(false);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: an interrupted stream ends as RpcException(Cancelled), not only OperationCanceledException.
        }
    }

    private async Task DiscoverAsync(List<Peer> peers, CancellationToken stoppingToken)
    {
        var complete = true;
        try
        {
            await foreach (var database in engine.ListDatabasesAsync(stoppingToken).ConfigureAwait(false))
            {
                using var discovery = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                discovery.CancelAfter(ReplicationProtocol.ProgressTimeout);
                try
                {
                    // ponytail: one catalog read per database per pass; list the replicated databases once if catalogs grow large.
                    if (await tenants.ReplicatesAsync(database, discovery.Token).ConfigureAwait(false) is false
                        || await engine.OpenDatabaseAsync(database, discovery.Token).ConfigureAwait(false) is not { } store)
                    {
                        continue;
                    }

                    long head;
                    var transaction = await store.BeginTransactionAsync(write: false, discovery.Token).ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        head = await transaction.GetLastChangeSequenceAsync(discovery.Token).ConfigureAwait(false);
                    }

                    lock (_state)
                    {
                        if (!store.IsClosed)
                        {
                            var identity = RaiseHead(store, head);
                            peers.ForEach(peer => peer.MarkDirty(identity));
                        }
                    }
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    complete = false;
                    LogDiscoveryFailed(logger, error);
                }
            }
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            complete = false;
            LogDiscoveryFailed(logger, error);
        }

        // A failed database stays in durable discovery, without an unbounded in-memory retry queue.
        metrics.Discovered();
        lock (_state)
        {
            Volatile.Write(ref _initialized, complete ? 1 : 0);
        }
    }

    private async Task ReconcileAsync(List<Peer> peers, CancellationToken stoppingToken)
    {
        await DiscoverAsync(peers, stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(ReconciliationInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await DiscoverAsync(peers, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Peer peer, CancellationToken stoppingToken)
    {
        while (true)
        {
            var work = peer.TakeDirty(out var retryDelay);
            if (work.Count == 0)
            {
                using var retry = new CancellationTokenSource(retryDelay, time);
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, retry.Token);
                try
                {
                    await peer.Signal.Reader.ReadAsync(wake.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // A database's backoff elapsed; a new commit can wake this wait sooner.
                }

                continue;
            }

            foreach (var database in work)
            {
                try
                {
                    // A new attempt must re-prove cached progress, even if credentials or the connection fail.
                    peer.Unverified(database);
                    peer.Refresh(credentials.Capture());
                    var more = await SyncAsync(peer, new V1.Replication.ReplicationClient(peer.Channel), database, stoppingToken).ConfigureAwait(false);
                    peer.Succeeded(database);
                    if (more)
                    {
                        peer.MarkDirty(database);
                    }
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    if (peer.Retry(database) is { } backoff)
                    {
                        Progressed();
                        LogSyncFailed(logger, error, peer.Number, 1, backoff.TotalMilliseconds);
                    }
                }
            }
        }
    }

    // Returns whether the turn ended with groups possibly left to send.
    private async Task<bool> SyncAsync(Peer peer, V1.Replication.ReplicationClient client, DatabaseIncarnation identity, CancellationToken stoppingToken)
    {
        // Include local snapshot/proof acquisition in the attempt budget, not only network waits.
        using var progress = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
        var database = identity.Tenant;
        // Peers refuse a database their catalog does not register, so send none of a database that is neither registered nor being
        // created (backup snapshots and restore staging are primary-local storage): its groups would count as lag forever.
        // Check before opening the file too: a queued commit must not reopen an ineligible snapshot or staging database.
        if (await tenants.ReplicatesAsync(database, progress.Token).ConfigureAwait(false) is false)
        {
            Drop(identity);
            Progressed();
            return false;
        }

        if (await engine.OpenDatabaseAsync(database, progress.Token).ConfigureAwait(false) is not { } store || store.Incarnation != identity.Origin)
        {
            return false;
        }

        // Capture a fixed head, then end the SQLite snapshot before any handshake or acknowledgment wait.
        long head;
        ReadOnlyMemory<byte> origin;
        var snapshot = await store.BeginTransactionAsync(write: false, progress.Token).ConfigureAwait(false);
        await using (snapshot.ConfigureAwait(false))
        {
            origin = await snapshot.GetOriginAsync(progress.Token).ConfigureAwait(false);
            head = await snapshot.GetLastChangeSequenceAsync(progress.Token).ConfigureAwait(false);
        }

        // The deadline covers the handshake and then every wait for an acknowledgment. Only a valid Welcome
        // and acknowledgments that advance the durable prefix extend it.
        using var call = client.Sync(cancellationToken: progress.Token);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(database, origin, options.Region) }, progress.Token).ConfigureAwait(false);
        var opening = await NextAsync(call, progress.Token).ConfigureAwait(false);
        var welcome = opening.Welcome
            ?? throw new InvalidDataException("the peer did not answer Hello with Welcome");
        // Fail closed on anything but what Hello offered: protocol 1.0, format and merge policy 1, LZ4, this
        // database in this epoch, and the minimum of both sides' limits, which both sides then enforce.
        if (!ReplicationProtocol.Accepts(welcome, database))
        {
            throw new InvalidDataException("the peer's Welcome does not match the Hello it answers");
        }

        var limits = welcome.Limits;
        CheckResponse(opening, limits);
        var peerMember = welcome.LocalIdentity;
        var receiverIncarnation = new Guid(peerMember.MemberIncarnation.Span, bigEndian: true);

        progress.CancelAfter(ReplicationProtocol.ProgressTimeout);

        // Resume after the peer's durable prefix of this origin; a missing frontier means none.
        var frontier = welcome.Frontiers.FirstOrDefault(frontier => frontier.Origin?.Incarnation.Span.SequenceEqual(origin.Span) == true);
        if (frontier is not null && frontier.Origin?.ReplicaId.Span.SequenceEqual(origin.Span) != true)
        {
            throw new InvalidDataException("the peer's frontier does not match this stream's origin");
        }

        var durableSequence = frontier?.DurableSequence ?? 0;
        if (durableSequence > long.MaxValue)
        {
            throw new InvalidDataException("the peer reports a durable prefix beyond any sequence");
        }

        var acknowledged = (long)durableSequence;
        // A peer ahead of this database's own log means the file went back in time (for example, a restore
        // from a snapshot); new groups would reuse numbers the peer already applied, so send nothing.
        if (acknowledged > head)
        {
            metrics.DetectedDivergence("peer_ahead");
            peer.Diverged(identity);
            throw new InvalidDataException("the peer has applied change groups this database never committed");
        }

        ReadOnlyMemory<byte> acknowledgedProof;
        await using (var proof = await store.BeginTransactionAsync(write: false, progress.Token).ConfigureAwait(false))
        {
            acknowledgedProof = await proof.GetChangeDigestAsync(acknowledged, progress.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("the peer reports a prefix missing from this journal");
        }

        var offeredProof = frontier?.JournalDigest.Memory ?? JournalDigest.Initial(origin.Span);
        if (!offeredProof.Span.SequenceEqual(acknowledgedProof.Span))
        {
            metrics.DetectedDivergence("welcome_digest");
            peer.Diverged(identity);
            metrics.PrefixMismatch();
            throw new InvalidDataException("the peer's durable prefix belongs to a different journal; restore recovery is required");
        }

        peer.Welcomed(identity, receiverIncarnation, acknowledged);
        Progressed();
        LogPeerRegion(logger, peer.Number, options.Region, peerMember.Region is "" ? "unknown" : peerMember.Region);
        var inFlight = new Queue<(long Sequence, uint Bytes, ReadOnlyMemory<byte> Digest)>();
        ulong inFlightBytes = 0;
        var next = acknowledged;
        SyncRequest? pending = null;
        var budget = MaxGroupsPerTurn;
        // Keep exactly one response read active while writes are in progress. State is still owned by
        // this async flow: processing a completed read never races queue or byte-credit mutations.
        var reading = call.ResponseStream.MoveNext(progress.Token);
        while (true)
        {
            // Backpressure: at most the negotiated number of groups and bytes await a DurableAck.
            while (inFlight.Count < limits.MaxInFlightBatches)
            {
                if (reading.IsCompleted)
                {
                    await ReceiveAsync().ConfigureAwait(false);
                }

                if (pending is null && budget > 0 && next < head)
                {
                    IReadOnlyList<DocumentChange> changes;
                    ReadOnlyMemory<byte> preceding;
                    // One group is bounded by the journal contract (count and content bytes). Materialize it,
                    // return the session, and only then compress or wait on the network.
                    var read = await store.BeginTransactionAsync(write: false, progress.Token).ConfigureAwait(false);
                    await using (read.ConfigureAwait(false))
                    {
                        preceding = await read.GetChangeDigestAsync(next, progress.Token).ConfigureAwait(false)
                            ?? throw new InvalidDataException("the local journal prefix proof is missing");
                        changes = await read.ReadChangesAsync(next + 1, progress.Token).ConfigureAwait(false)
                            ?? throw new InvalidDataException("the local journal has a gap before its committed head");
                    }

                    pending = new SyncRequest { CommittedGroup = ReplicationProtocol.CreateBatch(database, origin, next + 1, changes, preceding) };
                    budget--;
                    // The receiver runs the same check before it decodes the group.
                    ReplicationProtocol.CheckLimits(pending, limits, ChangeGroup.ContentBytes(changes));
                }

                var batch = pending?.CommittedGroup;
                if (batch is null || inFlightBytes + batch.EncodedApplicationBytes > limits.ByteCredit)
                {
                    break;
                }

                var writing = call.RequestStream.WriteAsync(pending, progress.Token);
                inFlight.Enqueue((++next, batch.EncodedApplicationBytes, batch.JournalDigest.Memory));
                inFlightBytes += batch.EncodedApplicationBytes;
                while (!writing.IsCompleted)
                {
                    await Task.WhenAny(writing, reading).ConfigureAwait(false);
                    if (reading.IsCompleted)
                    {
                        await ReceiveAsync().ConfigureAwait(false);
                    }
                }

                await writing.ConfigureAwait(false);
                metrics.Sent(batch);
                pending = null;
            }

            if (inFlight.Count == 0)
            {
                break;
            }

            if (pending?.CommittedGroup is { } waiting && inFlightBytes + waiting.EncodedApplicationBytes > limits.ByteCredit)
            {
                metrics.CreditWait();
            }

            await ReceiveAsync().ConfigureAwait(false);
        }

        // Caught up or out of budget: finish writes, then join the already-active response read and drain.
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        while (await ReceiveAsync(completing: true).ConfigureAwait(false))
        {
        }

        return next < head;

        async Task<bool> ReceiveAsync(bool completing = false)
        {
            if (!await reading.ConfigureAwait(false))
            {
                return completing ? false : throw new IOException("the peer ended the Sync stream early");
            }

            var response = call.ResponseStream.Current;
            CheckResponse(response, limits);
            ThrowIfError(response);
            if (response.Durable is { } durable)
            {
                // Before anything is released: the acknowledgment must come from the peer that sent Welcome, in this
                // epoch, for this stream's database and origin, with a prefix between the last one and the last group sent.
                var context = durable.Context;
                var id = context?.EventId;
                if (context is null || !peerMember.Equals(context.AcknowledgingPeer) || context.MembershipEpoch != ReplicationProtocol.MembershipEpoch
                    || !context.ConfigurationId.IsEmpty || id is null || !ReplicationProtocol.Matches(id, database)
                    || id.ShardId != 0 || id.Origin?.ReplicaId.Span.SequenceEqual(origin.Span) != true
                    || id.Origin?.Incarnation.Span.SequenceEqual(origin.Span) != true
                    || durable.DurablePrefix < (ulong)acknowledged || durable.DurablePrefix > (ulong)next)
                {
                    throw new InvalidDataException("the peer sent an acknowledgment for another peer, epoch, database or origin, or beyond what this stream sent");
                }

                var prefix = (long)durable.DurablePrefix;
                var proof = prefix == acknowledged ? acknowledgedProof : inFlight.First(group => group.Sequence == prefix).Digest;
                if (!durable.JournalDigest.Span.SequenceEqual(proof.Span))
                {
                    metrics.DetectedDivergence("ack_digest");
                    peer.Diverged(identity);
                    metrics.PrefixMismatch();
                    throw new InvalidDataException("the peer acknowledged a different journal prefix");
                }

                acknowledged = prefix;
                acknowledgedProof = proof;
                peer.Acknowledged(identity, receiverIncarnation, acknowledged);
                Progressed();
                var released = false;
                while (inFlight.Count > 0 && inFlight.Peek().Sequence <= acknowledged)
                {
                    inFlightBytes -= inFlight.Dequeue().Bytes;
                    released = true;
                }

                // Only an acknowledgment that advances the durable prefix extends the deadline; stale ones do not.
                if (released)
                {
                    progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
                }
            }

            reading = call.ResponseStream.MoveNext(progress.Token);
            return true;
        }
    }

    private static async Task<SyncResponse> NextAsync(AsyncDuplexStreamingCall<SyncRequest, SyncResponse> call, CancellationToken cancellationToken, TransferLimits? limits = null)
    {
        if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("the peer ended the Sync stream early");
        }

        CheckResponse(call.ResponseStream.Current, limits);
        ThrowIfError(call.ResponseStream.Current);
        return call.ResponseStream.Current;
    }

    private static void CheckResponse(SyncResponse response, TransferLimits? limits)
    {
        if (response.CalculateSize() > (limits?.MaxGrpcMessageBytes ?? ReplicationProtocol.MaxMessageBytes))
        {
            throw new InvalidDataException("the peer exceeded the negotiated gRPC response size");
        }
    }

    private static void ThrowIfError(SyncResponse response)
    {
        if (response.Error is { } error)
        {
            throw new IOException($"the peer ended the Sync stream with {error.Code}");
        }
    }

    private Peer CreatePeer(int number, Uri address)
    {
        // A channel pins one credential generation, including the full client chain. The single peer
        // worker replaces it between streams when rotation publishes a new context.
        GrpcChannel CreateChannel(PeerCredentials.Lease snapshot)
        {
            return PeerChannel.Create(address, snapshot);
        }

        var initial = credentials.Capture();
        try
        {
            return new Peer(number, CreateChannel(initial), initial, CreateChannel, _state, _heads, _slots, time, metrics);
        }
        catch
        {
            initial.Dispose();
            throw;
        }
    }

    // Logs the peer's position in peer-addresses, never its address.
    [LoggerMessage(LogLevel.Warning, "Replication of {Databases} databases to peer #{Peer} failed; retrying within {BackoffMilliseconds} ms")]
    private static partial void LogSyncFailed(ILogger logger, Exception error, int peer, int databases, double backoffMilliseconds);

    [LoggerMessage(LogLevel.Warning, "Replication discovery failed; retrying on the next reconciliation")]
    private static partial void LogDiscoveryFailed(ILogger logger, Exception error);

    [LoggerMessage(LogLevel.Information, "Replication sending from region {LocalRegion} to peer #{Peer} in region {PeerRegion}")]
    private static partial void LogPeerRegion(ILogger logger, int peer, string localRegion, string peerRegion);

    private static DatabaseIncarnation Identity(IDatabaseStore store)
    {
        var tenant = store.Tenant ?? throw new InvalidOperationException("system databases cannot replicate");
        return new DatabaseIncarnation(tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId!, store.Incarnation);
    }

    // Caller holds _state; the lifetime check and deletion notification use the same lock.
    private DatabaseIncarnation RaiseHead(IDatabaseStore store, long sequence)
    {
        var identity = Identity(store);
        if (!_slots.ContainsKey(identity) && _slots.Count < MaxObservedDatabases)
        {
            var used = _slots.Values.ToHashSet();
            var slot = Enumerable.Range(0, MaxObservedDatabases).First(slot => !used.Contains(slot));
            _slots.Add(identity, slot);
            _allocatedSlotCount = Math.Max(_allocatedSlotCount, slot + 1);
        }

        _heads[identity] = Math.Max(_heads.GetValueOrDefault(identity), sequence);
        return identity;
    }

    private int OmittedDatabases()
    {
        lock (_state)
        {
            return _heads.Count - _slots.Count;
        }
    }

    private Measurement<long>[] PeerLagMeasurements()
    {
        lock (_state)
        {
            return [.. _peers.Select(peer => new Measurement<long>(peer.Lag(), new KeyValuePair<string, object?>("peer", peer.Number)))];
        }
    }

    private Measurement<double>[] DatabaseMeasurements(Func<(long Lag, bool Diverged, double CatchupSeconds), double> value)
    {
        lock (_state)
        {
            return [.. _peers.SelectMany(peer => peer.DatabaseMeasurements(value, _allocatedSlotCount))];
        }
    }

    private sealed class Peer(int number, GrpcChannel channel, PeerCredentials.Lease credentials, Func<PeerCredentials.Lease, GrpcChannel> createChannel, Lock state, Dictionary<DatabaseIncarnation, long> heads, Dictionary<DatabaseIncarnation, int> slots, TimeProvider time, ReplicationMetrics metrics) : IDisposable
    {
        private readonly HashSet<DatabaseIncarnation> _dirty = [];
        // Bounded by the sender's 128 slots; removed with the database incarnation.
        private readonly Dictionary<DatabaseIncarnation, Observation> _observations = [];

        private sealed class Observation
        {
            public long? Started { get; set; }
            public double? ConvergenceSeconds { get; set; }
        }

        private Observation? Observe(DatabaseIncarnation database)
        {
            if (!slots.ContainsKey(database))
            {
                return null;
            }

            if (!_observations.TryGetValue(database, out var observation))
            {
                observation = new Observation();
                _observations.Add(database, observation);
            }

            return observation;
        }

        private void CatchingUp(DatabaseIncarnation database, bool failedVerification = false)
        {
            if (Observe(database) is { } observation)
            {
                // Lag/waits discard unverified ACKs. Discovery must not mistake that temporary
                // invalidation for new backlog while a healthy handshake is still in progress.
                var (_, acknowledged, lastVerified, diverged) = _acknowledged.GetValueOrDefault(database);
                var prefix = failedVerification || diverged ? acknowledged : lastVerified;
                if (heads.GetValueOrDefault(database) > prefix)
                {
                    observation.Started ??= time.GetTimestamp();
                }
            }
        }

        private void Verified(DatabaseIncarnation database, long prefix)
        {
            // Conflict state outlives metric omission; only a validated prefix covering the latest head clears it.
            var current = _acknowledged[database];
            _acknowledged[database] = (current.Receiver, current.Prefix, current.LastVerified,
                current.Diverged && prefix < heads.GetValueOrDefault(database));
            if (Observe(database) is not { } observation)
            {
                return;
            }

            CatchingUp(database);
            if (prefix < heads.GetValueOrDefault(database))
            {
                return;
            }

            if (observation.Started is { } started)
            {
                var elapsed = time.GetElapsedTime(started).TotalSeconds;
                observation.ConvergenceSeconds = elapsed;
                observation.Started = null;
                metrics.Converged(number, slots[database], elapsed);
            }
        }

        public void Diverged(DatabaseIncarnation database)
        {
            lock (state)
            {
                if (heads.ContainsKey(database))
                {
                    var current = _acknowledged.GetValueOrDefault(database);
                    _acknowledged[database] = (current.Receiver, current.Prefix, current.LastVerified, true);
                    CatchingUp(database, failedVerification: true);
                }
            }
        }

        public ReplicationPeerStatus Snapshot()
        {
            lock (state)
            {
                var databases = slots.Select(entry =>
                {
                    var database = entry.Key;
                    var head = heads.GetValueOrDefault(database);
                    var acknowledged = _acknowledged.GetValueOrDefault(database);
                    var observation = _observations.GetValueOrDefault(database);
                    return new ReplicationDatabaseStatus(entry.Value, database.Organization, database.Project, database.Database,
                        database.Origin, head, acknowledged.Prefix, Math.Max(0, head - acknowledged.Prefix), acknowledged.Diverged,
                        observation?.Started is { } started ? time.GetElapsedTime(started).TotalSeconds : 0,
                        observation?.ConvergenceSeconds);
                }).OrderBy(database => database.Slot).ToArray();
                return new(number, Lag(), databases);
            }
        }

        // Caller holds state. Gauge detail never scans all heads or materializes tenant identities.
        public Measurement<double>[] DatabaseMeasurements(Func<(long Lag, bool Diverged, double CatchupSeconds), double> value, int allocatedSlotCount)
        {
            var measurements = new Measurement<double>[allocatedSlotCount];
            for (var slot = 0; slot < measurements.Length; slot++)
            {
                // Explicit zeroes retire deleted slots in exporters that retain the last gauge sample.
                measurements[slot] = Measure(slot, 0);
            }

            foreach (var (database, slot) in slots)
            {
                var observation = _observations.GetValueOrDefault(database);
                var acknowledged = _acknowledged.GetValueOrDefault(database);
                var lag = Math.Max(0, heads.GetValueOrDefault(database) - acknowledged.Prefix);
                var catchup = observation?.Started is { } started ? time.GetElapsedTime(started).TotalSeconds : 0;
                measurements[slot] = Measure(slot, value((lag, acknowledged.Diverged, catchup)));
            }

            return measurements;

            Measurement<double> Measure(int slot, double sample)
            {
                return new(sample, new KeyValuePair<string, object?>("peer", number), new KeyValuePair<string, object?>("database", slot));
            }
        }

        private readonly Dictionary<DatabaseIncarnation, (int Failures, long Started, TimeSpan Delay)> _retries = [];
        // Only the latest receiving incarnation is retained; Welcome retires its predecessor's prefix.
        // LastVerified survives handshakes and slot promotion; Prefix/Receiver remain trusted only after validation.
        // Unresolved conflicts are retained here even when the database has no observation slot.
        private readonly Dictionary<DatabaseIncarnation, (Guid? Receiver, long Prefix, long LastVerified, bool Diverged)> _acknowledged = [];

        public int Number => number;

        private PeerCredentials.Lease _credentials = credentials;

        public GrpcChannel Channel { get; private set; } = channel;

        // Holds at most one pending wake-up; incarnations accumulate in the dirty set.
        public Channel<bool> Signal { get; } = System.Threading.Channels.Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        public void Dispose()
        {
            Channel.Dispose();
            _credentials.Dispose();
        }

        public void Refresh(PeerCredentials.Lease next)
        {
            if (ReferenceEquals(next.CertificateContext, _credentials.CertificateContext))
            {
                next.Dispose();
                return;
            }

            GrpcChannel replacement;
            try
            {
                replacement = createChannel(next);
            }
            catch
            {
                next.Dispose();
                throw;
            }

            Channel.Dispose();
            _credentials.Dispose();
            Channel = replacement;
            _credentials = next;
        }

        public void MarkDirty(DatabaseIncarnation database)
        {
            lock (state)
            {
                if (!heads.ContainsKey(database))
                {
                    return;
                }

                _dirty.Add(database);
                CatchingUp(database);
            }

            Signal.Writer.TryWrite(true);
        }

        public void Forget(DatabaseIncarnation database)
        {
            lock (state)
            {
                _dirty.Remove(database);
                _retries.Remove(database);
                _acknowledged.Remove(database);
                _observations.Remove(database);
            }
        }

        public void Welcomed(DatabaseIncarnation database, Guid receiver, long prefix)
        {
            lock (state)
            {
                if (heads.ContainsKey(database))
                {
                    // Welcome can report a lower prefix after the receiving database is recreated.
                    _acknowledged[database] = (receiver, prefix, prefix, _acknowledged.GetValueOrDefault(database).Diverged);
                    Verified(database, prefix);
                }
            }
        }

        public void Unverified(DatabaseIncarnation database)
        {
            lock (state)
            {
                if (_acknowledged.TryGetValue(database, out var current))
                {
                    _acknowledged[database] = (null, 0, current.LastVerified, current.Diverged);
                }
            }
        }

        public void Acknowledged(DatabaseIncarnation database, Guid receiver, long prefix)
        {
            lock (state)
            {
                if (heads.ContainsKey(database) && _acknowledged.TryGetValue(database, out var current) && current.Receiver == receiver)
                {
                    _acknowledged[database] = (receiver, prefix, prefix, current.Diverged);
                    Verified(database, prefix);
                }
            }
        }

        // Whether a write that waits for this database's group head need not wait for this peer: the peer durably
        // acknowledged the head, or its stream for the database is failing and in backoff.
        public bool Covers(DatabaseIncarnation database, long head)
        {
            lock (state)
            {
                return _retries.ContainsKey(database) || Acknowledges(database, head);
            }
        }

        public bool Acknowledges(DatabaseIncarnation database, long head)
        {
            lock (state)
            {
                return _acknowledged.GetValueOrDefault(database).Prefix >= head;
            }
        }

        public long Lag()
        {
            lock (state)
            {
                var total = 0L;
                foreach (var head in heads)
                {
                    total = SaturatingAdd(total, Math.Max(0, head.Value - _acknowledged.GetValueOrDefault(head.Key).Prefix));
                }

                return total;
            }
        }

        public void Succeeded(DatabaseIncarnation database)
        {
            lock (state)
            {
                _retries.Remove(database);
            }
        }

        public TimeSpan? Retry(DatabaseIncarnation database)
        {
            lock (state)
            {
                if (!heads.ContainsKey(database))
                {
                    return null;
                }

                CatchingUp(database, failedVerification: true);
                var failures = _retries.GetValueOrDefault(database).Failures;
                // Full jitter applies independently to each incarnation; cap the exponent as well as the delay.
                var ceiling = Math.Min(MaxBackoff.TotalMilliseconds, 100 * Math.Pow(2, failures));
                var delay = TimeSpan.FromMilliseconds(ceiling * Random.Shared.NextDouble());
                _retries[database] = (Math.Min(failures + 1, 7), time.GetTimestamp(), delay);
                _dirty.Add(database);
                return delay;
            }
        }

        public List<DatabaseIncarnation> TakeDirty(out TimeSpan retryDelay)
        {
            lock (state)
            {
                var work = new List<DatabaseIncarnation>();
                retryDelay = Timeout.InfiniteTimeSpan;
                foreach (var database in _dirty)
                {
                    var remaining = _retries.TryGetValue(database, out var retry)
                        ? retry.Delay - time.GetElapsedTime(retry.Started)
                        : TimeSpan.Zero;
                    if (remaining <= TimeSpan.Zero)
                    {
                        work.Add(database);
                    }
                    else if (retryDelay == Timeout.InfiniteTimeSpan || remaining < retryDelay)
                    {
                        retryDelay = remaining;
                    }
                }

                work.ForEach(database => _dirty.Remove(database));
                return work;
            }
        }
    }
}
