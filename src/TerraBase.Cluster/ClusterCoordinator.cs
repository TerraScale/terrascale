using System.Diagnostics;

namespace TerraBase.Cluster;

public sealed class ClusterCoordinator(ClusterTopology topology, ClusterDatabases databases, CommitFinalizer finalizer, StrongCoordinator strong, PeerHealthMonitor health) : IClusterCoordinator
{
    public const string StrongRequiredCode = "strong_required";

    public string LocalRegion => topology.LocalRegion;

    public string LocalNodeId => topology.LocalNodeId;

    public IReadOnlyList<PeerHealth> Peers => health.Snapshot;

    public void TrackDatabase(Ulid databaseId) => databases.Track(databaseId);

    public CausalBookmark CurrentBookmark(Ulid databaseId) => databases.Current(databaseId);

    public async ValueTask<Result<Done, NotFound, RequestTimeout>> WaitForBookmarkAsync(Ulid databaseId, CausalBookmark bookmark, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        var resolved = await databases.ResolveAsync(databaseId, cancellationToken).ConfigureAwait(false);
        return resolved switch
        {
            ISqlDatabaseSession session => await WaitLeasedAsync(databases, session, bookmark, timeout, cancellationToken).ConfigureAwait(false),
            NotFound notFound => notFound,
            Unavailable unavailable => new RequestTimeout(unavailable.Message),
        };
    }

    public async ValueTask<Result<SqlWriteResult, SqlError, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> ExecuteWriteAsync(
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
                ? await ExecuteLocalStrongAsync(session, request, cancellationToken).ConfigureAwait(false)
                : await strong.ExecuteAsync(session, request, cancellationToken).ConfigureAwait(false);
        }

        var local = await session.ExecuteWriteAsync(request, cancellationToken).ConfigureAwait(false);
        return local switch
        {
            SqlWriteResult written => Durable(written, await finalizer.FinalizeFastAsync(session.DatabaseId, written.Commit, cancellationToken).ConfigureAwait(false)),
            StrongRequired => IsLocalOnlyStrong(session.DatabaseId)
                ? await ExecuteLocalStrongAsync(session, request with { Mode = ConsistencyMode.Strong }, cancellationToken).ConfigureAwait(false)
                : await strong.ExecuteAsync(session, request with { Mode = ConsistencyMode.Strong }, cancellationToken).ConfigureAwait(false),
            SqlError error => error,
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };
    }

    public async ValueTask<Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> CommitTransactionAsync(
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
                ? await CommitLocalStrongAsync(session, transaction, cancellationToken).ConfigureAwait(false)
                : await strong.CommitTransactionAsync(session, transaction, cancellationToken).ConfigureAwait(false);
        }

        var local = await transaction.CommitLocalAsync(cancellationToken).ConfigureAwait(false);
        return local switch
        {
            CommitMetadata commit => Durable(commit, await finalizer.FinalizeFastAsync(session.DatabaseId, commit, cancellationToken).ConfigureAwait(false)),
            StrongRequired required => new SerializationConflict(session.DatabaseId, transaction.TxId, string.Concat(StrongRequiredCode, ":", Reason(required.Reason))),
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };
    }

    public async ValueTask<Result<ISqlTransaction, StorageBusy, RequestTimeout>> BeginTransactionAsync(ISqlDatabaseSession session, SqlTransactionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        databases.Track(session.DatabaseId);
        var begun = await session.BeginTransactionAsync(options, cancellationToken).ConfigureAwait(false);
        return begun switch
        {
            ISqlTransaction transaction => Upgrading(new UpgradingTransaction(session, transaction)),
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };

        static Result<ISqlTransaction, StorageBusy, RequestTimeout> Upgrading(ISqlTransaction transaction) => new(transaction);
    }

    private static async ValueTask<Result<Done, NotFound, RequestTimeout>> WaitLeasedAsync(ClusterDatabases databases, ISqlDatabaseSession session, CausalBookmark bookmark, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using (session.ConfigureAwait(false))
        {
            if (!session.CurrentBookmark.Dominates(bookmark))
            {
                _ = databases.RequestCatchUp(session.DatabaseId);
            }

            var waited = await session.WaitForBookmarkAsync(bookmark, timeout, cancellationToken).ConfigureAwait(false);
            return waited switch
            {
                Done done => done,
                RequestTimeout expired => expired,
            };
        }
    }

    private bool IsLocalOnlyStrong(Ulid databaseId) =>
        topology.StrongParticipants(databaseId) is [{ IsLocal: true }];

    private async ValueTask<Result<SqlWriteResult, SqlError, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> ExecuteLocalStrongAsync(
        ISqlDatabaseSession session,
        SqlStatementRequest request,
        CancellationToken cancellationToken)
    {
        var local = await session.ExecuteWriteAsync(request, cancellationToken).ConfigureAwait(false);
        return local switch
        {
            SqlWriteResult written => await FinalizeLocalStrongAsync(session.DatabaseId, written, cancellationToken).ConfigureAwait(false),
            StrongRequired => await strong.ExecuteAsync(session, request, cancellationToken).ConfigureAwait(false),
            SqlError error => error,
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };
    }

    private async ValueTask<SqlWriteResult> FinalizeLocalStrongAsync(Ulid databaseId, SqlWriteResult written, CancellationToken cancellationToken)
    {
        await finalizer.FinalizeStrongAsync(databaseId, written.Commit, cancellationToken).ConfigureAwait(false);
        return written;
    }

    private async ValueTask<Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> CommitLocalStrongAsync(
        ISqlDatabaseSession session,
        ISqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var local = await transaction.CommitLocalAsync(cancellationToken).ConfigureAwait(false);
        return local switch
        {
            CommitMetadata commit => await FinalizeLocalStrongCommitAsync(session.DatabaseId, commit, cancellationToken).ConfigureAwait(false),
            StrongRequired required => new SerializationConflict(session.DatabaseId, transaction.TxId, string.Concat(StrongRequiredCode, ":", Reason(required.Reason))),
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };
    }

    private async ValueTask<CommitMetadata> FinalizeLocalStrongCommitAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        await finalizer.FinalizeStrongAsync(databaseId, commit, cancellationToken).ConfigureAwait(false);
        return commit;
    }

    private static Result<SqlWriteResult, SqlError, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy> Durable(SqlWriteResult written, Result<Done, Unavailable, RequestTimeout> durable) => durable switch
    {
        Done => written,
        Unavailable unavailable => new Unavailable(string.Concat("committed locally; in-region quorum unconfirmed: ", unavailable.Message), "in_region_quorum_unconfirmed"),
        RequestTimeout timeout => timeout,
    };

    private static Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy> Durable(CommitMetadata commit, Result<Done, Unavailable, RequestTimeout> durable) => durable switch
    {
        Done => commit,
        Unavailable unavailable => new Unavailable(string.Concat("committed locally; in-region quorum unconfirmed: ", unavailable.Message), "in_region_quorum_unconfirmed"),
        RequestTimeout timeout => timeout,
    };

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
    private readonly List<SqlStatement> _statements = [];
    private ISqlTransaction _inner = inner;

    public Ulid TxId => _inner.TxId;

    public SqlTransactionOptions Options => _inner.Options;

    public bool Upgraded { get; private set; }

    public async ValueTask<Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout>> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var executed = await _inner.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
        return executed switch
        {
            SqlResultSet result => Recorded(statement, result),
            SqlError error when Options.Mode == ConsistencyMode.Fast && string.Equals(error.Code, ClusterCoordinator.StrongRequiredCode, StringComparison.Ordinal)
                => await UpgradeAsync(statement, cancellationToken).ConfigureAwait(false),
            SqlError error => error,
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };
    }

    public ValueTask<Result<CommitMetadata, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>> CommitLocalAsync(CancellationToken cancellationToken) =>
        _inner.CommitLocalAsync(cancellationToken);

    public ValueTask<Result<StagedWrite, SchemaMismatch, StorageBusy, RequestTimeout>> StageAsync(CancellationToken cancellationToken) =>
        _inner.StageAsync(cancellationToken);

    public ValueTask<Done> RollbackAsync(CancellationToken cancellationToken) => _inner.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout> Recorded(SqlStatement statement, SqlResultSet result)
    {
        _statements.Add(statement);
        return result;
    }

    private async ValueTask<Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout>> UpgradeAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        var options = _inner.Options with { Mode = ConsistencyMode.Strong };
        _ = await _inner.RollbackAsync(cancellationToken).ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);
        var begun = await session.BeginTransactionAsync(options, cancellationToken).ConfigureAwait(false);
        return begun switch
        {
            ISqlTransaction transaction => await ReplayAsync(transaction, statement, cancellationToken).ConfigureAwait(false),
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };
    }

    private async ValueTask<Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout>> ReplayAsync(ISqlTransaction strong, SqlStatement statement, CancellationToken cancellationToken)
    {
        _inner = strong;
        Upgraded = true;
        foreach (var recorded in _statements)
        {
            var replayed = await strong.ExecuteAsync(recorded, cancellationToken).ConfigureAwait(false);
            var succeeded = replayed switch
            {
                SqlResultSet => true,
                SqlError or StorageBusy or RequestTimeout => false,
            };
            if (!succeeded)
            {
                return replayed;
            }
        }

        var executed = await strong.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
        return executed switch
        {
            SqlResultSet result => Recorded(statement, result),
            SqlError error => error,
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };
    }
}
