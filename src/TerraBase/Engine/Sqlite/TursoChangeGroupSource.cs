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
internal sealed class TursoChangeGroupSource : IChangeGroupSource
{
    // Cached ranges without local groups, keyed by (file incarnation, a read's start): a peer reading again from the same
    // change id starts at the range's end, so a replica that mostly joins remote writes is not rescanned on every pass.
    private const int MaxQuiet = 65_536;
    private readonly Lock _gate = new();
    private readonly Dictionary<(Ulid Incarnation, long After), long> _quiet = [];
    private readonly SqliteStorageEngine _engine;

    // Per database file, the CDC rows its last acknowledgement left.
    private readonly ConcurrentDictionary<Ulid, long> _backlog = new();

    public TursoChangeGroupSource(SqliteStorageEngine engine, IMeterFactory meters)
    {
        _engine = engine;
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.replication.cdc_backlog", Backlog, null,
            "CDC rows kept because some peer has not acknowledged them, as of each database's last acknowledgement.");
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
            var (groups, quiet) = await transaction.ReadChangeGroupsAsync(start, maxGroups, maxBytes, _engine.ReplicaId, cancellationToken).ConfigureAwait(false);
            if (quiet > after)
            {
                lock (_gate)
                {
                    if (_quiet.Count >= MaxQuiet)
                    {
                        _quiet.Clear();
                    }

                    _quiet[(store.Incarnation, after)] = quiet;
                }
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

        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            if (await transaction.GetAckedAsync(peer, cancellationToken).ConfigureAwait(false) is { } acked)
            {
                return acked;
            }
        }

        // A peer's stream reaches the database for the first time, or this file is a restore or a copy: from now on its
        // unshipped rows stay until it acks them.
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

    private async ValueTask RecordAsync(SqliteDatabaseStore store, ReplicaId peer, long changeId, CancellationToken cancellationToken)
    {
        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var left = await transaction.RecordAckedAsync(peer, changeId, store.BackupAnchor, _engine.HostTime.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _backlog[store.Incarnation] = left;
        }
    }

    private long Backlog() => _backlog.Values.Sum();
}
