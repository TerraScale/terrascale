using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Engine.Turso;

/// <summary>
/// The CDC tailer (docs/terrabase/multi-region-replication.md section 7, slice R3): this machine's change groups, rebuilt
/// on demand from each database file's <c>turso_cdc</c>, which is the outbox. A group is one Turso transaction's state rows
/// of this machine's own writes; joined rows carry their origin and are never shipped back.
/// </summary>
internal sealed partial class TursoChangeGroupSource : BackgroundService, IChangeGroupSource, IReplicaStates, IReplicaSnapshotSource
{
    // Per database file (incarnation), its ranges (from, to] of change ids proven to hold no local group, disjoint and with
    // adjacent or overlapping ranges merged: a read from inside one starts at its end (a replica that mostly joins remote
    // writes is not rescanned on every pass), and RetainsAsync checks retention at its end. Ranges ending below the oldest
    // kept row are pruned on each retention pass, so a file holds about one range per local group still retained.
    private const int MaxQuietRanges = 4096;
    private readonly Lock _gate = new();
    private readonly Dictionary<Ulid, SortedList<long, long>> _quiet = [];
    private readonly ILogger<TursoChangeGroupSource> _logger;
    private readonly TursoStorageEngine _engine;

    // Resolved per pass: the peer streams behind it read this source, so it cannot be a constructor dependency.
    private readonly Func<IRemoteDurability?>? _durability;

    // Per database file, the CDC rows its last acknowledgement left; and when its oldest local commit that some peer has
    // not acknowledged committed (Unix seconds), with when that was last looked at (host-clock ticks).
    private readonly ConcurrentDictionary<Ulid, long> _backlog = new();
    private readonly ConcurrentDictionary<Ulid, (long? Committed, long CheckedAt)> _tail = new();

    // Databases a peer stream reached since the start: their tail lag is looked at every few seconds, so it keeps growing
    // while a peer is away and acknowledges nothing.
    private readonly ConcurrentDictionary<Ulid, TenantContext> _tracked = new();
    // When each database's far-behind peers were last measured (UTC ticks), and FarBehindEvery between measurements.
    private readonly ConcurrentDictionary<Ulid, long> _measured = new();
    private static readonly TimeSpan FarBehindEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TailRefresh = TimeSpan.FromSeconds(5);

    // Bounded retention batches one pass deletes after its own (each at most 16,384 rows): 4 million rows, then the next pass.
    private const int MaxTrimBatches = 256;

    public TursoChangeGroupSource(TursoStorageEngine engine, IMeterFactory meters, ILogger<TursoChangeGroupSource> logger, Func<IRemoteDurability?>? durability = null)
    {
        _engine = engine;
        _durability = durability;
        _logger = logger;
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.replication.cdc_backlog", Backlog, null,
            "CDC rows kept because some peer has not acknowledged them, as of each database's last acknowledgement.");
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.replication.tail_lag", TailLag, "s",
            "Age of the oldest local commit that a peer heard from in the last 24 h has not acknowledged, over every database.");
    }

    // Startup, catalog changes and notification overflow rediscover files, including copies not opened since restart.
    public async IAsyncEnumerable<TenantContext> ListDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_engine.SystemDatabaseExists)
        {
            yield return ChangeGroups.System;
        }

        await foreach (var database in _engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return database;
        }
    }

    // The store of a database the peer streams name: the system database (ChangeGroups.System) or a client one.
    private async ValueTask<TursoDatabaseStore?> OpenAsync(TenantContext database, CancellationToken cancellationToken) =>
        database == ChangeGroups.System
            ? (TursoDatabaseStore)await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)
            : (TursoDatabaseStore?)await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false);

    // A transaction of the database's copy in place, when that copy is `incarnation`: checked against the file's persisted
    // incarnation inside the transaction (for a write, once it holds the writer), so a copy, restore or rebuild that
    // reopened the file meanwhile is refused. Null, with nothing begun, otherwise.
    private async ValueTask<(TursoDatabaseStore Store, TursoStorageTransaction Transaction)?> BeginAsync(
        TenantContext database, Ulid incarnation, bool write, CancellationToken cancellationToken)
    {
        if (incarnation == Ulid.Empty)
        {
            throw new ArgumentException("an incarnation is named", nameof(incarnation));
        }

        if (await OpenAsync(database, cancellationToken).ConfigureAwait(false) is not { } store || store.Incarnation != incarnation)
        {
            return null;
        }

        var transaction = (TursoStorageTransaction)await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
        try
        {
            if (await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false) == incarnation)
            {
                return (store, transaction);
            }
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await transaction.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    public async ValueTask<SourceGroups?> ReadAsync(
        TenantContext database, Ulid incarnation, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken)
    {
        var start = QuietEnd(incarnation, after);
        if (await BeginAsync(database, incarnation, write: false, cancellationToken).ConfigureAwait(false) is not (_, var transaction))
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            var (groups, quiet, _) = await transaction.ReadChangeGroupsAsync(start, maxGroups, maxBytes, _engine.ReplicaId, cancellationToken).ConfigureAwait(false);
            // Only a read over every row past its start proves its range quiet: past a cut, the deleted rows may have held
            // local groups (the range would then let RetainsAsync skip a refill the peer needs).
            if (quiet > after && await transaction.RetainsAsync(start, cancellationToken).ConfigureAwait(false))
            {
                Remember(incarnation, after, quiet);
            }

            // The incarnation this transaction read from: BeginAsync checked it against the persisted one.
            return new SourceGroups(incarnation, groups);
        }
    }

    public async ValueTask<SourceCursor?> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken)
    {
        // Null also when the copy is replaced between these steps: the caller asks again on its next pass.
        if (await OpenAsync(database, cancellationToken).ConfigureAwait(false) is not { } store
            || await BeginAsync(database, store.Incarnation, write: false, cancellationToken).ConfigureAwait(false) is not (_, var transaction))
        {
            return null;
        }

        var incarnation = store.Incarnation;
        _tracked[incarnation] = database;
        await using (transaction.ConfigureAwait(false))
        {
            if (await transaction.GetAckedAsync(peer, incarnation, cancellationToken).ConfigureAwait(false) is { } cursor)
            {
                return cursor;
            }
        }

        // A peer's stream reaches the database for the first time, or this file is a restore or a copy: from now on its
        // unshipped rows stay until it acks them.
        return await RecordAsync(database, incarnation, peer, 0, default, cancellationToken).ConfigureAwait(false) ? new SourceCursor(incarnation, 0) : null;
    }

    public ValueTask<bool> AckAsync(TenantContext database, Ulid incarnation, ReplicaId peer, long changeId, Hlc stable, CancellationToken cancellationToken) =>
        RecordAsync(database, incarnation, peer, changeId, stable, cancellationToken);

    public async ValueTask<bool> ProveAsync(TenantContext database, Ulid incarnation, long from, long through, IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, incarnation, write: true, cancellationToken).ConfigureAwait(false) is not (var store, var transaction))
        {
            return false;
        }

        bool moved;
        await using (transaction.ConfigureAwait(false))
        {
            moved = await transaction.AdvanceShapeFrontierAsync(_engine.ReplicaId, (from, through), held, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (moved)
        {
            // Wakes shape sealing and live reads waiting for the frontier.
            store.MoveCommitCount();
        }

        return true;
    }

    public async ValueTask<bool> RecordStableAsync(TenantContext database, Ulid incarnation, ReplicaId peer, Hlc stable, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, incarnation, write: true, cancellationToken).ConfigureAwait(false) is not (_, var transaction))
        {
            return false;
        }

        await using (transaction.ConfigureAwait(false))
        {
            await transaction.RecordStableAsync(peer, stable, _engine.HostTime.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    public ValueTask<Hlc> StableAsync(TenantContext database, CancellationToken cancellationToken) =>
        ReadAsync(database, horizon: false, cancellationToken);

    public ValueTask<Hlc> HorizonAsync(TenantContext database, CancellationToken cancellationToken) =>
        ReadAsync(database, horizon: true, cancellationToken);

    public async ValueTask<int?> ForgetAsync(
        TenantContext database, Ulid incarnation, IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken)
    {
        // Every write transaction is durable (FULL); the incarnation is checked under the writer like an ack's.
        if (await BeginAsync(database, incarnation, write: true, cancellationToken).ConfigureAwait(false) is not (_, var transaction))
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            if (keys.Count == 0)
            {
                return 0;
            }

            if (!await transaction.CanForgetAsync(keys, horizon, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var forgotten = await transaction.ForgetAsync(keys, horizon, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return forgotten;
        }
    }

    // A stamp read on a read transaction, at this machine's clock; 0 when this machine does not hold the database.
    private async ValueTask<Hlc> ReadAsync(TenantContext database, bool horizon, CancellationToken cancellationToken)
    {
        var durability = _durability?.Invoke();
        var placement = database != ChangeGroups.System && durability is { Confirms: true }
            ? await durability.PlacementAsync(database, cancellationToken).ConfigureAwait(false) : null;
        if (database != ChangeGroups.System && durability is { Confirms: true } && placement is null)
        {
            return default;
        }

        if (await OpenAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return default;
        }

        var transaction = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var now = _engine.Member.Clock.Tick();
            return horizon
                ? await transaction.GetHorizonAsync(now, cancellationToken, placement).ConfigureAwait(false)
                : await transaction.GetStableAsync(now, cancellationToken, placement).ConfigureAwait(false);
        }
    }

    public Task ChangedAsync(CancellationToken cancellationToken) => _engine.NextCommitAsync(cancellationToken);

    public IDisposable Subscribe(Action<TenantContext> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var subscription = new Subscription(_engine, changed);
        _engine.ReplicationChanged += changed;
        return subscription;
    }

    private sealed class Subscription(TursoStorageEngine engine, Action<TenantContext> changed) : IDisposable
    {
        private Action<TenantContext>? _changed = changed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _changed, null) is { } callback)
            {
                engine.ReplicationChanged -= callback;
            }
        }
    }

    // Every row the peer lacks past `after` is still here: the rows past the end of the known quiet ranges from `after` are
    // all kept. Those ranges hold no local group, so deleted rows inside them are none a peer lacks: a live stream whose
    // in-memory ack stayed below the acks retention raised over joined rows is not told to refill.
    public async ValueTask<bool?> RetainsAsync(TenantContext database, Ulid incarnation, long after, CancellationToken cancellationToken)
    {
        var start = QuietEnd(incarnation, after);
        if (await BeginAsync(database, incarnation, write: false, cancellationToken).ConfigureAwait(false) is not (_, var transaction))
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.RetainsAsync(start, cancellationToken).ConfigureAwait(false);
        }
    }

    // The end of the remembered range holding `after`, or `after`: every change id in between holds no local group.
    private long QuietEnd(Ulid incarnation, long after)
    {
        lock (_gate)
        {
            if (!_quiet.TryGetValue(incarnation, out var ranges))
            {
                return after;
            }

            foreach (var (from, to) in ranges)
            {
                if (from > after)
                {
                    break;
                }

                if (after <= to)
                {
                    return to;
                }
            }

            return after;
        }
    }

    // A range (from, to] of the file that holds no local group, proven by a read over all of its rows; merged with every
    // remembered range it overlaps or touches.
    internal void Remember(Ulid incarnation, long from, long to)
    {
        lock (_gate)
        {
            if (!_quiet.TryGetValue(incarnation, out var ranges))
            {
                _quiet[incarnation] = ranges = [];
            }

            foreach (var (start, end) in ranges.Where(range => range.Key <= to && range.Value >= from).ToList())
            {
                (from, to) = (Math.Min(from, start), Math.Max(to, end));
                _ = ranges.Remove(start);
            }

            ranges[from] = to;
            // shortcut: past MaxQuietRanges (local groups interleaved with joined rows that retention has not cut yet) the
            // lowest ranges go; a live stream below them may refill once. Raise it if retention windows get that long.
            while (ranges.Count > MaxQuietRanges)
            {
                ranges.RemoveAt(0);
            }
        }
    }

    // Drops the ranges that end before the oldest kept change id: retention cut past them, so they can no longer spare a
    // refill (the check at their end fails) or a read.
    private void Prune(Ulid incarnation, long oldest)
    {
        lock (_gate)
        {
            if (_quiet.TryGetValue(incarnation, out var ranges))
            {
                while (ranges.Count > 0 && ranges.Values[0] < oldest - 1)
                {
                    ranges.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>
    /// For tests: the 5 s retention pass stops running over every database until a stream reaches it again
    /// (<see cref="AckedAsync"/>), so only the test's own passes (<see cref="TrimAsync"/>) raise acks.
    /// </summary>
    internal void Untrack() => _tracked.Clear();

    /// <summary>The remembered quiet ranges of a database file, for tests.</summary>
    internal int QuietRanges(Ulid incarnation)
    {
        lock (_gate)
        {
            return _quiet.TryGetValue(incarnation, out var ranges) ? ranges.Count : 0;
        }
    }

    public async ValueTask<long?> HeadAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, incarnation, write: false, cancellationToken).ConfigureAwait(false) is not (_, var transaction))
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<long?> RetainedAfterAsync(TenantContext database, Ulid incarnation, long? through, long head, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, incarnation, write: false, cancellationToken).ConfigureAwait(false) is not (_, var transaction))
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.GetRetainedAfterAsync(through, head, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<ReplicaDigest?> DigestAsync(TenantContext database, Ulid? expected, CancellationToken cancellationToken)
    {
        // Discovery pins the copy in place first; the transaction then checks it is still that copy.
        var incarnation = expected
            ?? (await OpenAsync(database, cancellationToken).ConfigureAwait(false) is { } store ? (Ulid?)store.Incarnation : null);
        if (incarnation is not { } named
            || await BeginAsync(database, named, write: false, cancellationToken).ConfigureAwait(false) is not (var opened, var transaction))
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            // One snapshot: the head is the change id the digests cover, and the feed head the seq they cover.
            var buckets = await transaction.ReadDigestsAsync(cancellationToken).ConfigureAwait(false);
            var head = await transaction.GetHeadAsync(cancellationToken).ConfigureAwait(false);
            var feed = opened.IsSystem ? 0 : await transaction.ReadShapeFrontierAsync(remote: false, cancellationToken).ConfigureAwait(false);
            var collectedBefore = await transaction.GetCollectedBeforeAsync(cancellationToken).ConfigureAwait(false);
            return new ReplicaDigest(named, head, buckets, feed) { CollectedBefore = collectedBefore };
        }
    }

    public async ValueTask<bool> MatchedAsync(TenantContext database, ReplicaDigest digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (digest.Head <= 0 || await BeginAsync(database, digest.Incarnation, write: false, cancellationToken).ConfigureAwait(false) is not (var store, var read))
        {
            return false;
        }

        // Read first: a frontier already at the digest's feed head, the usual case, takes no writer.
        await using (read.ConfigureAwait(false))
        {
            if (store.IsSystem || !store.RequiresRemoteProof
                || await read.ReadShapeFrontierAsync(remote: true, cancellationToken).ConfigureAwait(false) >= digest.Feed)
            {
                return false;
            }
        }

        if (await BeginAsync(database, digest.Incarnation, write: true, cancellationToken).ConfigureAwait(false) is not (_, var transaction))
        {
            return false;
        }

        bool moved;
        await using (transaction.ConfigureAwait(false))
        {
            moved = await transaction.RebaseShapeFrontierAsync(digest.Head, digest.Feed, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (moved)
        {
            // Wakes shape sealing and live reads waiting for the frontier.
            store.MoveCommitCount();
        }

        return moved;
    }

    public async ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)?> ReadBucketsAsync(
        TenantContext database, Ulid incarnation, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, incarnation, write: false, cancellationToken).ConfigureAwait(false) is not (_, var transaction))
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.ReadBucketStatesAsync(buckets, after, maxStates, maxBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TailRefresh, _engine.HostTime);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (var (incarnation, database) in _tracked)
            {
                try
                {
                    if (await OpenAsync(database, stoppingToken).ConfigureAwait(false) is not { } store || store.Incarnation != incarnation)
                    {
                        _ = _tracked.TryRemove(incarnation, out _);
                        _ = _tail.TryRemove(incarnation, out _);
                        _ = _backlog.TryRemove(incarnation, out _);
                        _ = _measured.TryRemove(incarnation, out _);
                        lock (_gate)
                        {
                            _ = _quiet.Remove(incarnation);
                        }

                        continue;
                    }

                    await TrimAsync(store, stoppingToken).ConfigureAwait(false);
                }
                catch (ReplicaCopyFaultedException)
                {
                    // A refused copy (faulted, under rebuild, or the node failed closed) takes no retention write; its rebuild
                    // replaces it. Also while stopping, when the catch below would let the refusal end the host.
                }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // The retention pass (ack raises, trim, the tail gauge): the next tick tries again. Logged, so a pass that
                    // keeps failing (and the CDC that would then grow) shows.
                    _logger.ZLogWarning(error, $"The CDC retention pass over database {database.DatabaseId:@Database} failed; the next pass in {TailRefresh:@Interval} retries");
                }
            }
        }
    }

    // One retention pass over a database. Retention also runs without acks: a silent (24 h) or far-behind (2^20 change ids or
    // 1 GiB) peer stops holding rows even when no other peer acknowledges anything, and no ack holds a row past 24 h after it
    // committed (a peer still heard from then refills); and acks rise over joined rows (Tail.cs), whose ranges are remembered
    // as quiet.
    internal async Task TrimAsync(TursoDatabaseStore store, CancellationToken cancellationToken)
    {
        long? floor;
        var durability = _durability?.Invoke();
        var placement = store.Tenant is { } placed && durability is { Confirms: true }
            ? await durability.PlacementAsync(placed, cancellationToken).ConfigureAwait(false) : null;
        // Read before the writer is taken: joined commits of a held origin pass the frontier before the cut, so a replica that
        // only joins remote writes is not held for 24 h waiting for an ack it never gets.
        var held = store.RequiresRemoteProof && store.Tenant is { } tenant && durability is not null
            ? await durability.HeldOriginsAsync(tenant, cancellationToken).ConfigureAwait(false)
            : FrozenSet<ReplicaId>.Empty;
        // Also before the writer, and at most once a minute: measuring a backlog reads all of it (TS-982).
        IReadOnlySet<long> farBehind = FrozenSet<long>.Empty;
        var measuredAt = _engine.HostTime.GetUtcNow().UtcTicks;
        if (!_measured.TryGetValue(store.Incarnation, out var last) || measuredAt - last >= FarBehindEvery.Ticks)
        {
            _measured[store.Incarnation] = measuredAt;
            var reader = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                farBehind = await reader.FarBehindAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        bool more;
        long? below;
        DateTimeOffset now;
        Ulid incarnation;
        var transaction = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // The file this transaction holds, which a reopen may have made another than the store's cached one.
            incarnation = await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false);
            // Before the cut: commits that need no new proof pass the shape frontier while capture still holds them, joined
            // ones of an origin held now included.
            var moved = store.RequiresRemoteProof && await transaction.AdvanceShapeFrontierAsync(_engine.ReplicaId, null, held, cancellationToken).ConfigureAwait(false);
            now = _engine.HostTime.GetUtcNow();
            // The cut keeps what the frontier has not walked yet, for a bounded time (a pass's budget, or a proof to come).
            below = await transaction.ShapeFrontierHoldAsync(now.ToUnixTimeSeconds(), cancellationToken).ConfigureAwait(false) is { } hold
                ? Math.Min(store.BackupAnchor ?? long.MaxValue, hold)
                : store.BackupAnchor;
            (var left, floor, var raised, var oldest, more) = await transaction.TrimAsync(below, now, _engine.ReplicaId, farBehind, cancellationToken).ConfigureAwait(false);
            // Before the commit wakes a stream: its RetainsAsync then already sees the ranges the raises proved quiet (which
            // hold whether or not the commit lands; the rows are unchanged until a later cut).
            foreach (var (from, to) in raised)
            {
                Remember(incarnation, from, to);
            }

            var horizon = store.IsSystem || durability is not { Confirms: true } || placement is not null
                ? await transaction.GetHorizonAsync(_engine.Member.Clock.Tick(), cancellationToken, placement).ConfigureAwait(false) : default;
            _ = await transaction.CollectAsync(horizon, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _backlog[incarnation] = left;
            Prune(incarnation, oldest);
            if (moved)
            {
                // Wakes shape sealing and live reads waiting for the frontier.
                store.MoveCommitCount();
            }
        }

        // The rest of a long releasable backlog goes in further bounded batches, each committed (its pages released) before the
        // next takes the writer; a pass stops after MaxTrimBatches and the next one continues.
        for (var batch = 0; more && batch < MaxTrimBatches; batch++)
        {
            var next = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
            await using (next.ConfigureAwait(false))
            {
                if (await next.GetIncarnationAsync(cancellationToken).ConfigureAwait(false) != incarnation)
                {
                    break;
                }

                (var left, more) = await next.TrimBatchAsync(below, now, cancellationToken).ConfigureAwait(false);
                _ = await next.CommitAsync(cancellationToken).ConfigureAwait(false);
                _backlog[incarnation] = left;
            }
        }

        await TailAsync(store, floor, cancellationToken).ConfigureAwait(false);
    }

    // Records an ack of `incarnation` under the writer; false, with nothing written, when the copy in place is another.
    private async ValueTask<bool> RecordAsync(TenantContext database, Ulid incarnation, ReplicaId peer, long changeId, Hlc stable, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, incarnation, write: true, cancellationToken).ConfigureAwait(false) is not (var store, var transaction))
        {
            return false;
        }

        long? floor;
        await using (transaction.ConfigureAwait(false))
        {
            if (await transaction.RecordAckedAsync(peer, incarnation, changeId, stable, store.BackupAnchor, _engine.HostTime.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false) is not (var left, var lowest))
            {
                return false;
            }

            floor = lowest;
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _backlog[incarnation] = left;
        }

        // After the writer is released: the gauge's read must not hold it.
        await TailAsync(store, floor, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // The oldest local group above the slowest live peer's ack, looked at no more than once a second per database. Only a
    // gauge: it never throws into an ack. A read that fails (a damaged change log row, a busy or closed store) keeps the
    // last value; the held database shows in send_lag and unreadable_reads instead.
    private async ValueTask TailAsync(TursoDatabaseStore store, long? floor, CancellationToken cancellationToken)
    {
        var now = _engine.HostTime.GetUtcNow();
        var seen = _tail.TryGetValue(store.Incarnation, out var known);
        if (seen && now.UtcTicks - known.CheckedAt < TimeSpan.TicksPerSecond)
        {
            return;
        }

        long? committed = null;
        if (floor is { } after)
        {
            try
            {
                var transaction = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    (_, _, committed) = await transaction.ReadChangeGroupsAsync(after, 1, 1, _engine.ReplicaId, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                committed = seen ? known.Committed : null;
            }
        }

        _tail[store.Incarnation] = (committed, now.UtcTicks);
    }

    private long Backlog() => _backlog.Values.Sum();

    private double TailLag()
    {
        var now = _engine.HostTime.GetUtcNow().ToUnixTimeSeconds();
        return _tail.Values.Select(tail => tail.Committed is { } committed ? Math.Max(0, now - committed) : 0).DefaultIfEmpty(0).Max();
    }
}
