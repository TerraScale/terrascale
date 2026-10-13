using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// The CDC tailer (docs/terrabase/multi-region-replication.md section 7, slice R3): this machine's change groups, rebuilt
/// on demand from each database file's <c>turso_cdc</c>, which is the outbox. A group is one Turso transaction's state rows
/// of this machine's own writes; joined rows carry their origin and are never shipped back.
/// </summary>
internal sealed class TursoChangeGroupSource : BackgroundService, IChangeGroupSource, IReplicaStates
{
    // Cached ranges without local groups, keyed by (file incarnation, a read's start): a peer reading again from the same
    // change id starts at the range's end, so a replica that mostly joins remote writes is not rescanned on every pass.
    private const int MaxQuiet = 65_536;
    private readonly Lock _gate = new();
    private readonly Dictionary<(Ulid Incarnation, long After), long> _quiet = [];
    private readonly SqliteStorageEngine _engine;

    // Per database file, the CDC rows its last acknowledgement left; and when its oldest local commit that some peer has
    // not acknowledged committed (Unix seconds), with when that was last looked at (host-clock ticks).
    private readonly ConcurrentDictionary<Ulid, long> _backlog = new();
    private readonly ConcurrentDictionary<Ulid, (long? Committed, long CheckedAt)> _tail = new();

    // Databases a peer stream reached since the start: their tail lag is looked at every few seconds, so it keeps growing
    // while a peer is away and acknowledges nothing.
    private readonly ConcurrentDictionary<Ulid, TenantContext> _tracked = new();
    private static readonly TimeSpan TailRefresh = TimeSpan.FromSeconds(5);

    public TursoChangeGroupSource(SqliteStorageEngine engine, IMeterFactory meters)
    {
        _engine = engine;
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

        var start = after;
        lock (_gate)
        {
            while (_quiet.TryGetValue((store.Incarnation, start), out var end))
            {
                start = end;
            }
        }

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

        var start = after;
        lock (_gate)
        {
            while (_quiet.TryGetValue((store.Incarnation, start), out var end))
            {
                start = end;
            }
        }

        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.RetainsAsync(start, cancellationToken).ConfigureAwait(false);
        }
    }

    // A range (from, to] of the file that holds no local group, proven by a read over all of its rows.
    // shortcut: forgets every range at MaxQuiet entries; a live stream below them may then refill once.
    private void Remember(Ulid incarnation, long from, long to)
    {
        lock (_gate)
        {
            if (_quiet.Count >= MaxQuiet)
            {
                _quiet.Clear();
            }

            _quiet[(incarnation, from)] = to;
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
                        continue;
                    }

                    await TrimAsync(store, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // Only a gauge refresh: the next tick tries again (a deleted or busy database, a closing engine).
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
            (var left, floor, var raised) = await transaction.TrimAsync(store.BackupAnchor, _engine.HostTime.GetUtcNow(), _engine.ReplicaId, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _backlog[store.Incarnation] = left;
            foreach (var (from, to) in raised)
            {
                Remember(store.Incarnation, from, to);
            }
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

    // The oldest local group above the slowest live peer's ack, looked at no more than once a second per database.
    private async ValueTask TailAsync(SqliteDatabaseStore store, long? floor, CancellationToken cancellationToken)
    {
        var now = _engine.HostTime.GetUtcNow();
        if (_tail.TryGetValue(store.Incarnation, out var known) && now.UtcTicks - known.CheckedAt < TimeSpan.TicksPerSecond)
        {
            return;
        }

        long? committed = null;
        if (floor is { } after)
        {
            var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                (_, _, committed) = await transaction.ReadChangeGroupsAsync(after, 1, 1, _engine.ReplicaId, cancellationToken).ConfigureAwait(false);
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
