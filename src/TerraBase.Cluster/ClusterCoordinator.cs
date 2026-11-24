using System.Diagnostics;

namespace TerraBase.Cluster;

public sealed class ClusterCoordinator(ClusterTopology topology, ClusterDatabases databases, CommitFinalizer finalizer, StrongCoordinator strong, PeerHealthMonitor health) : IClusterCoordinator
{
    public const string StrongRequiredCode = "strong_required";

    public string LocalRegion => topology.LocalRegion;

    public string LocalNodeId => topology.LocalNodeId;

    public IReadOnlyList<PeerHealth> Peers => health.Snapshot;

    public void TrackDatabase(Ulid databaseId) => databases.Track(databaseId);

    public void UntrackDatabase(Ulid databaseId) => databases.Untrack(databaseId);

    public CausalBookmark CurrentBookmark(Ulid databaseId) => databases.Current(databaseId);

    public async ValueTask<ClusterWaitBookmarkOutcome> WaitForBookmarkAsync(Ulid databaseId, CausalBookmark bookmark, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        var resolved = await databases.ResolveAsync(databaseId, cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession session => await WaitLeasedAsync(databases, session, bookmark, timeout, cancellationToken),
            DatabaseNotFound notFound => notFound,
            ClusterUnavailable unavailable => new ClusterTimeout(unavailable.Message),
        };
    }

    public async ValueTask<ClusterWriteOutcome> ExecuteWriteAsync(
        ISqlDatabaseSession session,
        SqlStatementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        databases.Track(session.DatabaseId);
        if (request.Mode == ConsistencyMode.Strong)
        {
            return IsLocalOnlyStrong(session.DatabaseId)
                ? await ExecuteLocalStrongAsync(session, request, cancellationToken)
                : await strong.ExecuteAsync(session, request, cancellationToken);
        }

        var local = await session.ExecuteWriteAsync(request, cancellationToken);
        return local switch
        {
            SqlWriteResult written => Durable(written, await finalizer.FinalizeFastAsync(session.DatabaseId, written.Commit, cancellationToken)),
            StrongRequired => IsLocalOnlyStrong(session.DatabaseId)
                ? await ExecuteLocalStrongAsync(session, request with { Mode = ConsistencyMode.Strong }, cancellationToken)
                : await strong.ExecuteAsync(session, request with { Mode = ConsistencyMode.Strong }, cancellationToken),
            SqlError error => error,
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
        };
    }

    public async ValueTask<ClusterWriteOutcome> ExecuteLocalBootstrapWriteAsync(
        ISqlDatabaseSession session,
        SqlStatementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        databases.Track(session.DatabaseId);
        var local = await session.ExecuteWriteAsync(request with { Mode = ConsistencyMode.Strong }, cancellationToken);
        return local switch
        {
            SqlWriteResult written => Durable(written, await finalizer.FinalizeFastAsync(session.DatabaseId, written.Commit, cancellationToken)),
            StrongRequired => new ClusterUnavailable("bootstrap write was refused locally", "bootstrap_strong_required"),
            SqlError error => error,
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
        };
    }

    public async ValueTask<ClusterCommitTransactionOutcome> CommitTransactionAsync(
        ISqlDatabaseSession session,
        ISqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transaction);
        databases.Track(session.DatabaseId);
        if (transaction.Options.Mode == ConsistencyMode.Strong)
        {
            return IsLocalOnlyStrong(session.DatabaseId)
                ? await CommitLocalStrongAsync(session, transaction, cancellationToken)
                : await strong.CommitTransactionAsync(session, transaction, cancellationToken);
        }

        var local = await transaction.CommitLocalAsync(cancellationToken);
        return local switch
        {
            CommitMetadata commit => Durable(commit, await finalizer.FinalizeFastAsync(session.DatabaseId, commit, cancellationToken)),
            StrongRequired required => new SerializationConflict(session.DatabaseId, transaction.TxId, string.Concat(StrongRequiredCode, ":", Reason(required.Reason))),
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
        };
    }

    public async ValueTask<ClusterBeginTransactionOutcome> BeginTransactionAsync(ISqlDatabaseSession session, SqlTransactionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        databases.Track(session.DatabaseId);
        var begun = await session.BeginTransactionAsync(options, cancellationToken);
        return begun switch
        {
            ISqlTransaction transaction => Upgrading(new UpgradingTransaction(session, transaction)),
            StorageBusy busy => busy,
            StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
        };

        static ClusterBeginTransactionOutcome Upgrading(ISqlTransaction transaction) => new(transaction);
    }

    private static async ValueTask<ClusterWaitBookmarkOutcome> WaitLeasedAsync(ClusterDatabases databases, ISqlDatabaseSession session, CausalBookmark bookmark, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using (session)
        {
            if (!session.CurrentBookmark.Dominates(bookmark))
            {
                _ = databases.RequestCatchUp(session.DatabaseId);
            }

            var waited = await session.WaitForBookmarkAsync(bookmark, timeout, cancellationToken);
            return waited switch
            {
                BookmarkReached reached => reached,
                StorageTimeout expired => new ClusterTimeout(expired.Message, expired.Code),
            };
        }
    }

    private bool IsLocalOnlyStrong(Ulid databaseId) =>
        topology.RegionPeers.Count == 0 && topology.StrongParticipants(databaseId) is [{ IsLocal: true }];

    private async ValueTask<ClusterWriteOutcome> ExecuteLocalStrongAsync(
        ISqlDatabaseSession session,
        SqlStatementRequest request,
        CancellationToken cancellationToken)
    {
        var local = await session.ExecuteWriteAsync(request, cancellationToken);
        return local switch
        {
            SqlWriteResult written => await FinalizeLocalStrongAsync(session.DatabaseId, written, cancellationToken),
            StrongRequired => await strong.ExecuteAsync(session, request, cancellationToken),
            SqlError error => error,
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
        };
    }

    private async ValueTask<SqlWriteResult> FinalizeLocalStrongAsync(Ulid databaseId, SqlWriteResult written, CancellationToken cancellationToken)
    {
        await finalizer.FinalizeStrongAsync(databaseId, written.Commit, cancellationToken);
        return written;
    }

    private async ValueTask<ClusterCommitTransactionOutcome> CommitLocalStrongAsync(
        ISqlDatabaseSession session,
        ISqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var local = await transaction.CommitLocalAsync(cancellationToken);
        return local switch
        {
            CommitMetadata commit => await FinalizeLocalStrongCommitAsync(session.DatabaseId, commit, cancellationToken),
            StrongRequired required => new SerializationConflict(session.DatabaseId, transaction.TxId, string.Concat(StrongRequiredCode, ":", Reason(required.Reason))),
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
        };
    }

    private async ValueTask<CommitMetadata> FinalizeLocalStrongCommitAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        await finalizer.FinalizeStrongAsync(databaseId, commit, cancellationToken);
        return commit;
    }

    private static ClusterWriteOutcome Durable(SqlWriteResult written, InRegionReplicationOutcome durable) => durable switch
    {
        InRegionReplicated => written,
        ClusterUnavailable unavailable => QuorumUnconfirmed(unavailable.Message),
        ClusterTimeout timeout => QuorumUnconfirmed(timeout.Message),
    };

    private static ClusterCommitTransactionOutcome Durable(CommitMetadata commit, InRegionReplicationOutcome durable) => durable switch
    {
        InRegionReplicated => commit,
        ClusterUnavailable unavailable => QuorumUnconfirmed(unavailable.Message),
        ClusterTimeout timeout => QuorumUnconfirmed(timeout.Message),
    };

    private static ClusterUnavailable QuorumUnconfirmed(string reason) =>
        new(string.Concat("committed locally; in-region quorum unconfirmed: ", reason), "in_region_quorum_unconfirmed");

    private static string Reason(StrongRequiredReason reason) => reason switch
    {
        StrongRequiredReason.SchemaChange => "schema_change",
        StrongRequiredReason.SecondaryUniqueIndex => "secondary_unique_index",
        StrongRequiredReason.StrictTable => "strict_table",
        _ => throw new UnreachableException(),
    };
}

public sealed class UpgradingTransaction(ISqlDatabaseSession session, ISqlTransaction inner) : ISqlTransaction
{
    private readonly List<(SqlStatement Statement, SqlResultSet Result)> _statements = [];
    private ISqlTransaction _inner = inner;

    public Ulid TxId => _inner.TxId;

    public SqlTransactionOptions Options => _inner.Options;

    public bool Upgraded { get; private set; }

    public async ValueTask<TransactionExecuteResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var executed = await _inner.ExecuteAsync(statement, cancellationToken);
        return executed switch
        {
            SqlResultSet result => Recorded(statement, result),
            SqlError error when Options.Mode == ConsistencyMode.Fast && string.Equals(error.Code, ClusterCoordinator.StrongRequiredCode, StringComparison.Ordinal)
                => await UpgradeAsync(statement, cancellationToken),
            SqlError error => error,
            StorageBusy busy => busy,
            StorageTimeout timeout => timeout,
        };
    }

    public ValueTask<TransactionCommitLocalResult> CommitLocalAsync(CancellationToken cancellationToken) =>
        _inner.CommitLocalAsync(cancellationToken);

    public ValueTask<TransactionStageResult> StageAsync(CancellationToken cancellationToken) =>
        _inner.StageAsync(cancellationToken);

    public ValueTask RollbackAsync(CancellationToken cancellationToken) => _inner.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private TransactionExecuteResult Recorded(SqlStatement statement, SqlResultSet result)
    {
        if (Options.Mode == ConsistencyMode.Fast)
        {
            _statements.Add((statement, result));
        }
        return result;
    }

    private async ValueTask<TransactionExecuteResult> UpgradeAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        var options = _inner.Options with { Mode = ConsistencyMode.Strong };
        await _inner.RollbackAsync(cancellationToken);
        await _inner.DisposeAsync();
        var begun = await session.BeginTransactionAsync(options, cancellationToken);
        return begun switch
        {
            ISqlTransaction transaction => await ReplayAsync(transaction, statement, cancellationToken),
            StorageBusy busy => busy,
            StorageTimeout timeout => timeout,
        };
    }

    private async ValueTask<TransactionExecuteResult> ReplayAsync(ISqlTransaction strong, SqlStatement statement, CancellationToken cancellationToken)
    {
        _inner = strong;
        Upgraded = true;
        for (var index = 0; index < _statements.Count; index++)
        {
            var (recorded, previous) = _statements[index];
            var replayed = await strong.ExecuteAsync(recorded, cancellationToken);
            TransactionExecuteResult? failure = replayed switch
            {
                SqlResultSet result when SameResult(previous, result) => (TransactionExecuteResult?)null,
                SqlResultSet => (TransactionExecuteResult)new SqlError("serialization_conflict", "transaction replay changed a result already returned; the transaction was rolled back", "40001", index, 0),
                SqlError or StorageBusy or StorageTimeout => replayed,
            };
            if (failure is { } refused)
            {
                _statements.Clear();
                await strong.RollbackAsync(CancellationToken.None);
                return refused;
            }
        }

        _statements.Clear();
        var executed = await strong.ExecuteAsync(statement, cancellationToken);
        return executed switch
        {
            SqlResultSet result => Recorded(statement, result),
            SqlError error => error,
            StorageBusy busy => busy,
            StorageTimeout timeout => timeout,
        };
    }

    private static bool SameResult(SqlResultSet first, SqlResultSet second) =>
        first.RowsAffected == second.RowsAffected
        && first.LastInsertRowId == second.LastInsertRowId
        && first.Columns.SequenceEqual(second.Columns)
        && first.Rows.Count == second.Rows.Count
        && first.Rows.Zip(second.Rows).All(static rows => rows.First.SequenceEqual(rows.Second));
}
