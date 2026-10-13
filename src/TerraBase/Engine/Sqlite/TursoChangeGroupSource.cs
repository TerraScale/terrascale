using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// The CDC tailer (docs/terrabase/multi-region-replication.md section 7, slice R3): this machine's change groups, rebuilt
/// on demand from each database file's <c>turso_cdc</c>, which is the outbox. A group is one Turso transaction's state rows
/// of this machine's own writes; joined rows carry their origin and are never shipped back.
/// </summary>
internal sealed class TursoChangeGroupSource : BackgroundService, IChangeGroupSource, IReplicaStates
{
    // Per database file (incarnation), its ranges (from, to] of change ids proven to hold no local group, disjoint and with
    // adjacent or overlapping ranges merged: a read from inside one starts at its end (a replica that mostly joins remote
    // writes is not rescanned on every pass), and RetainsAsync checks retention at its end. Ranges ending below the oldest
    // kept row are pruned on each retention pass, so a file holds about one range per local group still retained.
    private const int MaxQuietRanges = 4096;
    private readonly Lock _gate = new();
    private readonly Dictionary<Ulid, SortedList<long, long>> _quiet = [];
    private readonly ILogger<TursoChangeGroupSource> _logger;
    private readonly SqliteStorageEngine _engine;

    // Per database file, the CDC rows its last acknowledgement left; and when its oldest local commit that some peer has
    // not acknowledged committed (Unix seconds), with when that was last looked at (host-clock ticks).
    private readonly ConcurrentDictionary<Ulid, long> _backlog = new();
    private readonly ConcurrentDictionary<Ulid, (long? Committed, long CheckedAt)> _tail = new();

    // Databases a peer stream reached since the start: their tail lag is looked at every few seconds, so it keeps growing
    // while a peer is away and acknowledges nothing.
    private readonly ConcurrentDictionary<Ulid, TenantContext> _tracked = new();
    private static readonly TimeSpan TailRefresh = TimeSpan.FromSeconds(5);

    public TursoChangeGroupSource(SqliteStorageEngine engine, IMeterFactory meters, ILogger<TursoChangeGroupSource> logger)
    {
        _engine = engine;
        _logger = logger;
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.replication.cdc_backlog", Backlog, null,
            "CDC rows kept because some peer has not acknowledged them, as of each database's last acknowledgement.");
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.replication.tail_lag", TailLag, "s",
            "Age of the oldest local commit that a peer heard from in the last 24 h has not acknowledged, over every database.");
    }

    // shortcut: lists every database file on each pass, so a database with unsent groups ships after a restart even when
    // nothing opened it; keep a set of databases with unacknowledged groups once machines hold many databases.
    public IAsyncEnumerable<TenantContext> ListDatabasesAsync(CancellationToken cancellationToken) => _engine.ListDatabasesAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<OriginGroup>> ReadAsync(TenantContext database, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken)
    {
        if (await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not SqliteDatabaseStore store)
        {
            return [];
        }

        var start = QuietEnd(store.Incarnation, after);
        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var (groups, quiet, _) = await transaction.ReadChangeGroupsAsync(start, maxGroups, maxBytes, _engine.ReplicaId, cancellationToken).ConfigureAwait(false);
            // Only a read over every row past its start proves its range quiet: past a cut, the deleted rows may have held
            // local groups (the range would then let RetainsAsync skip a refill the peer needs).
            if (quiet > after && await transaction.RetainsAsync(start, cancellationToken).ConfigureAwait(false))
            {
                Remember(store.Incarnation, after, quiet);
            }

            return groups;
        }
    }

    public async ValueTask<long> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken)
    {
        if (await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not SqliteDatabaseStore store)
        {
            return 0;
        }

        _tracked[store.Incarnation] = database;
        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            if (await transaction.GetAckedAsync(peer, cancellationToken).ConfigureAwait(false) is { } acked)
            {
                return acked;
            }
        }

        // A peer's stream reaches the database for the first time (a copy or restore starts with every ack at 0): from now on
        // its unshipped rows stay until it acks them.
        await RecordAsync(store, peer, 0, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public async ValueTask AckAsync(TenantContext database, ReplicaId peer, long changeId, CancellationToken cancellationToken)
    {
        if (await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is SqliteDatabaseStore store)
        {
            await RecordAsync(store, peer, changeId, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ChangedAsync(CancellationToken cancellationToken) => _engine.NextCommitAsync(cancellationToken);

    // Every row the peer lacks past `after` is still here: the rows past the end of the known quiet ranges from `after` are
    // all kept. Those ranges hold no local group, so deleted rows inside them are none a peer lacks: a live stream whose
    // in-memory ack stayed below the acks retention raised over joined rows is not told to refill.
    public async ValueTask<bool> RetainsAsync(TenantContext database, long after, CancellationToken cancellationToken)
    {
        if (await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not SqliteDatabaseStore store)
        {
            return true;
        }

        var start = QuietEnd(store.Incarnation, after);
        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<long> HeadAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not SqliteDatabaseStore store)
        {
            return 0;
        }

        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<byte[]?> DigestAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not SqliteDatabaseStore store)
        {
            return null;
        }

        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.ReadDigestsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)> ReadBucketsAsync(
        TenantContext database, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken)
    {
        if (await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not SqliteDatabaseStore store)
        {
            return ([], null);
        }

        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
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
                    if (await _engine.OpenDatabaseAsync(database, stoppingToken).ConfigureAwait(false) is not SqliteDatabaseStore store || store.Incarnation != incarnation)
                    {
                        _ = _tracked.TryRemove(incarnation, out _);
                        _ = _tail.TryRemove(incarnation, out _);
                        lock (_gate)
                        {
                            _ = _quiet.Remove(incarnation);
                        }

                        continue;
                    }

                    await TrimAsync(store, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    // The retention pass (ack raises, trim, the tail gauge): the next tick tries again. Logged, so a pass that
                    // keeps failing (and the CDC that would then grow) shows.
                    _logger.ZLogWarning(error, $"The CDC retention pass over database {database.DatabaseId:@Database} failed; the next pass in {TailRefresh:@Interval} retries");
                }
            }
        }
    }

    // One retention pass over a database. Retention also runs without acks: a silent (24 h) or far-behind (1 GiB) peer stops
    // holding rows even when no other peer acknowledges anything; and acks rise over joined rows (Tail.cs), whose ranges are
    // remembered as quiet.
    internal async Task TrimAsync(SqliteDatabaseStore store, CancellationToken cancellationToken)
    {
        long? floor;
        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            (var left, floor, var raised, var oldest) = await transaction.TrimAsync(store.BackupAnchor, _engine.HostTime.GetUtcNow(), _engine.ReplicaId, cancellationToken).ConfigureAwait(false);
            // Before the commit wakes a stream: its RetainsAsync then already sees the ranges the raises proved quiet (which
            // hold whether or not the commit lands; the rows are unchanged until a later cut).
            foreach (var (from, to) in raised)
            {
                Remember(store.Incarnation, from, to);
            }

            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _backlog[store.Incarnation] = left;
            Prune(store.Incarnation, oldest);
        }

        await TailAsync(store, floor, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RecordAsync(SqliteDatabaseStore store, ReplicaId peer, long changeId, CancellationToken cancellationToken)
    {
        long? floor;
        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            (var left, floor) = await transaction.RecordAckedAsync(peer, changeId, store.BackupAnchor, _engine.HostTime.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _backlog[store.Incarnation] = left;
        }

        // After the writer is released: the gauge's read must not hold it.
        await TailAsync(store, floor, cancellationToken).ConfigureAwait(false);
    }

    // The oldest local group above the slowest live peer's ack, looked at no more than once a second per database. Only a
    // gauge: it never throws into an ack. A read that fails (a damaged change log row, a busy or closed store) keeps the
    // last value; the held database shows in send_lag and unreadable_reads instead.
    private async ValueTask TailAsync(SqliteDatabaseStore store, long? floor, CancellationToken cancellationToken)
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
                var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    (_, _, committed) = await transaction.ReadChangeGroupsAsync(after, 1, 1, _engine.ReplicaId, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
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
