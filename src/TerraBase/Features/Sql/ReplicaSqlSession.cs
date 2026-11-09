using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sql.Routing;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

internal sealed class ReplicaSqlSession(
    SqlService service, IDatabaseReplicas replicas, IReplicaCatalog placements, HomeSessions homes, RegionReplication replication, WriteRouter router,
    TerraBaseOptions options, TimeProvider transactionTime, TenantContext database, bool canWrite, Func<ISqlSession> local) : ISqlSession
{
    private static readonly SqlResult Empty = new([], [], 0, null);
    private static readonly SqlError ReadOnlyWrite = new(SqlErrors.ReadOnlyTransaction, "a READ ONLY transaction cannot write");
    private static readonly SqlError Expired = new(SqlErrors.QueryCanceled, "the transaction expired: it was open for more than 5 seconds and was rolled back");
    private static readonly SqlError ReadThenWrite = new(SqlErrors.SerializationConflict,
        "the transaction read in this region and then wrote: retry it, and the retry runs at the database's home");

    private ISqlSession? _home;
    private bool _primary;
    private IReplicaReader? _snapshot;
    private bool _readOnlyTransaction;
    private bool _declaredReadOnly;
    private bool _readLocally;
    private bool _conflicted;
    private string? _first;
    private WriteOverride _preference;
    private bool _expired;
    private long _opened;
    private long _position = -1;

    public bool IsAutocommit => !_readOnlyTransaction && (_home?.IsAutocommit ?? true);

    public WriteRoute? LastRoute { get; private set; }

    public void Prefer(WriteOverride preference) => _preference = preference;

    public void BeginRequest() => _home?.BeginRequest();

    public ValueTask EndRequestAsync() => _home?.EndRequestAsync() ?? ValueTask.CompletedTask;

    public ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken) => ExecuteAsync(command, admit: null, cancellationToken);

    public async ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await UsePrimaryAsync(cancellationToken).ConfigureAwait(false);
        if (_primary || _home is { IsAutocommit: false })
        {
            return await HomeAsync(command, admit, cancellationToken).ConfigureAwait(false);
        }

        var planned = SqlScript.Classify(command.Sql);
        if (planned is not SqlStatementPlan plan)
        {
            return planned is SqlError refused ? refused : SqlErrors.Error("the statement cannot be run");
        }

        if (_readOnlyTransaction)
        {
            return await InSnapshotAsync(plan, command, admit, cancellationToken).ConfigureAwait(false);
        }

        switch (plan.Kind)
        {
            case SqlStatementKind.Read when await ReadAsync(plan, command, admit, cancellationToken).ConfigureAwait(false) is { } read:
                return read;
            case SqlStatementKind.Begin when (plan.ReadOnly || _preference != WriteOverride.Home)
                && await replicas.BeginReadAsync(database, _position, cancellationToken).ConfigureAwait(false) is { } snapshot:
                (_snapshot, _readOnlyTransaction, _declaredReadOnly, _expired, _opened) = (snapshot, true, plan.ReadOnly, false, transactionTime.GetTimestamp());
                (_readLocally, _conflicted, _first) = (false, false, null);
                return Empty;
            case SqlStatementKind.Write when !canWrite:
                return SqlErrors.Auth;
            case SqlStatementKind.Write:
                await RouteAsync([command.Sql], cancellationToken).ConfigureAwait(false);
                return await HomeAsync(command, admit, cancellationToken).ConfigureAwait(false);
            case SqlStatementKind.Noop:
                return Empty;
            case SqlStatementKind.Rollback when _home is null:
                return Empty;
            case SqlStatementKind.Commit when _home is null:
                return SqlErrors.Error("cannot commit - no transaction is active");
            case SqlStatementKind.Savepoint or SqlStatementKind.Release or SqlStatementKind.RollbackTo when _home is null:
                return SqlErrors.Error("savepoints run only inside a transaction: BEGIN one first");
            default:
                return await HomeAsync(command, admit, cancellationToken, marks: plan.Kind != SqlStatementKind.Read).ConfigureAwait(false);
        }
    }

    internal async ValueTask<SqlBatchOutcome?> TryBatchAsync(IReadOnlyList<SqlCommand> commands, CancellationToken cancellationToken)
    {
        if (_readOnlyTransaction)
        {
            return null;
        }

        var writes = commands.Any(command => SqlScript.Classify(command.Sql) is SqlStatementPlan { Kind: SqlStatementKind.Write });
        if ((writes && !canWrite) || (!writes && _preference != WriteOverride.Home && _home is not { IsAutocommit: false }))
        {
            return null;
        }

        if (writes)
        {
            await RouteAsync([.. commands.Select(command => command.Sql)], cancellationToken).ConfigureAwait(false);
        }

        switch (await OpenHomeAsync(cancellationToken).ConfigureAwait(false))
        {
            case (_, { } error):
                return new SqlBatchFailure(error, 0);
            case (HomeSqlSession remote, _):
                var outcome = await remote.BatchAsync(commands, cancellationToken).ConfigureAwait(false);
                _ = await MarkAsync(Empty).ConfigureAwait(false);
                return outcome;
            default:
                return null;
        }
    }

    public async ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, CancellationToken cancellationToken) =>
        await OpenHomeAsync(cancellationToken).ConfigureAwait(false) switch
        {
            (_, { } error) => error,
            (var home, _) => await MarkAsync(await home!.ExecuteScriptAsync(sql, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false),
        };

    public async ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, int maxStatements, CancellationToken cancellationToken) =>
        await OpenHomeAsync(cancellationToken).ConfigureAwait(false) switch
        {
            (_, { } error) => error,
            (var home, _) => await MarkAsync(await home!.ExecuteScriptAsync(sql, maxStatements, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false),
        };

    public async ValueTask<SqlDescribeOutcome> DescribeAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await UsePrimaryAsync(cancellationToken).ConfigureAwait(false);
        if (_primary || _home is { IsAutocommit: false })
        {
            return await DescribeAtHomeAsync(command, cancellationToken).ConfigureAwait(false);
        }

        var planned = SqlScript.Classify(command.Sql);
        if (planned is not SqlStatementPlan plan)
        {
            return planned is SqlError refused ? refused : SqlErrors.Error("the statement cannot be described");
        }

        if (plan.Kind is not (SqlStatementKind.Read or SqlStatementKind.Write))
        {
            return new SqlDescription([], [], IsExplain: false, IsReadOnly: plan.Kind != SqlStatementKind.Write);
        }

        if (_readOnlyTransaction && !_declaredReadOnly && !_conflicted && plan.Kind == SqlStatementKind.Write && canWrite)
        {
            if (_readLocally)
            {
                router.Remember(database, _first!);
                LastRoute = router.Record(new StatementRoute(WritePath.Home, WriteReasons.ReadThenWrite));
                _conflicted = true;
                await DropSnapshotAsync().ConfigureAwait(false);
                return ReadThenWrite;
            }

            await DropSnapshotAsync().ConfigureAwait(false);
            _readOnlyTransaction = false;
            if (await HomeAsync(SqlBatch.Control("BEGIN"), admit: null, cancellationToken, marks: false).ConfigureAwait(false) is SqlError refused)
            {
                return refused;
            }

            return await DescribeAtHomeAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (_readOnlyTransaction)
        {
            return plan.Kind == SqlStatementKind.Write ? (canWrite ? ReadOnlyWrite : SqlErrors.Auth)
                : _snapshot is { } snapshot ? await DescribeOnAsync(snapshot, plan, cancellationToken).ConfigureAwait(false) : Expired;
        }

        // The replica's connections are read-only and refuse to prepare a write: writes are described where they run, at the home.
        if (plan.Kind == SqlStatementKind.Write)
        {
            return await DescribeAtHomeAsync(command, cancellationToken).ConfigureAwait(false);
        }

        var reader = await replicas.BeginReadAsync(database, _position, cancellationToken).ConfigureAwait(false);
        if (reader is null)
        {
            return await DescribeAtHomeAsync(command, cancellationToken).ConfigureAwait(false);
        }

        await using (reader.ConfigureAwait(false))
        {
            return await DescribeOnAsync(reader, plan, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DropSnapshotAsync().ConfigureAwait(false);
        if (_home is { } home)
        {
            _home = null;
            await home.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<SqlOutcome?> ReadAsync(SqlStatementPlan plan, SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
    {
        var reader = await replicas.BeginReadAsync(database, _position, cancellationToken).ConfigureAwait(false);
        if (reader is null)
        {
            return null;
        }

        await using (reader.ConfigureAwait(false))
        {
            return await RunOnAsync(reader, plan, command, admit, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<SqlOutcome> RunOnAsync(IReplicaReader reader, SqlStatementPlan plan, SqlCommand command, Func<SqlResult, SqlError?>? admit,
        CancellationToken cancellationToken) =>
        await service.ExecuteOnReplicaAsync(database, reader, [(plan, command)], cancellationToken).ConfigureAwait(false) switch
        {
            IReadOnlyList<SqlResult> results => admit?.Invoke(results[0]) is { } refused ? refused : results[0],
            ReplicaSqlFailure failed => failed.Error,
        };

    private async ValueTask<SqlOutcome> InSnapshotAsync(SqlStatementPlan plan, SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
    {
        if (plan.Kind is SqlStatementKind.Commit or SqlStatementKind.Rollback)
        {
            var expired = _expired && !_conflicted;
            await DropSnapshotAsync().ConfigureAwait(false);
            (_readOnlyTransaction, _conflicted) = (false, false);
            return expired && plan.Kind == SqlStatementKind.Commit ? Expired : Empty;
        }

        if (_conflicted)
        {
            return SqlErrors.InFailedTransaction;
        }

        if (!_expired && transactionTime.GetElapsedTime(_opened) >= SqlLimits.MaxTransactionTime)
        {
            _expired = true;
            await DropSnapshotAsync().ConfigureAwait(false);
        }

        switch (plan.Kind)
        {
            case var _ when _expired || _snapshot is null:
                return Expired;
            case SqlStatementKind.Begin:
                return SqlErrors.Error("cannot start a transaction within a transaction");
            case SqlStatementKind.Write when !canWrite:
                return SqlErrors.Auth;
            case SqlStatementKind.Write when _declaredReadOnly:
                return ReadOnlyWrite;
            case SqlStatementKind.Read when !_declaredReadOnly && _first is null && router.Remembers(database, command.Sql):
                return await PromoteAsync(command, admit, write: false, cancellationToken).ConfigureAwait(false);
            case SqlStatementKind.Read:
                (_first, _readLocally) = (_first ?? command.Sql, true);
                return await RunOnAsync(_snapshot!, plan, command, admit, cancellationToken).ConfigureAwait(false);
            case SqlStatementKind.Write or SqlStatementKind.Savepoint or SqlStatementKind.Release or SqlStatementKind.RollbackTo when !_declaredReadOnly:
                if (!_readLocally)
                {
                    return await PromoteAsync(command, admit, plan.Kind == SqlStatementKind.Write, cancellationToken).ConfigureAwait(false);
                }

                router.Remember(database, _first!);
                LastRoute = router.Record(new StatementRoute(WritePath.Home, WriteReasons.ReadThenWrite));
                _conflicted = true;
                await DropSnapshotAsync().ConfigureAwait(false);
                return ReadThenWrite;
            default:
                return Empty;
        }
    }

    private async ValueTask<SqlOutcome> PromoteAsync(SqlCommand command, Func<SqlResult, SqlError?>? admit, bool write, CancellationToken cancellationToken)
    {
        await DropSnapshotAsync().ConfigureAwait(false);
        _readOnlyTransaction = false;
        if (await HomeAsync(SqlBatch.Control("BEGIN"), admit: null, cancellationToken, marks: false).ConfigureAwait(false) is SqlError refused)
        {
            return refused;
        }

        if (write)
        {
            await RouteAsync([command.Sql], cancellationToken).ConfigureAwait(false);
        }

        return await HomeAsync(command, admit, cancellationToken, marks: write).ConfigureAwait(false);
    }

    internal async ValueTask<WriteRoute> RouteAsync(IReadOnlyList<string> statements, CancellationToken cancellationToken)
    {
        IReplicaReader? reader;
        try
        {
            reader = await replicas.BeginReadAsync(database, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            reader = null;
        }

        try
        {
            return LastRoute = await router.RouteAsync(database, reader, statements, _preference, homeDurable: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<SqlDescribeOutcome> DescribeOnAsync(IReplicaReader reader, SqlStatementPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            var run = await SqlSession.RunTextAsync(plan, statement => reader.ExecuteAsync(statement, cancellationToken)).ConfigureAwait(false);
            var shape = await reader.DescribeAsync(run, cancellationToken).ConfigureAwait(false);
            return new SqlDescription(shape.Parameters, [.. shape.Columns.Select(column => new SqlColumn(column.Name, column.DeclType))],
                plan.IsExplain, plan.Kind != SqlStatementKind.Write);
        }
        catch (SqlStatementException error)
        {
            return SqlSession.Map(error, canWrite);
        }
    }

    private async ValueTask<SqlOutcome> HomeAsync(SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken, bool marks = true) =>
        await OpenHomeAsync(cancellationToken).ConfigureAwait(false) switch
        {
            (_, { } error) => error,
            (var home, _) => marks
                ? await MarkAsync(await home!.ExecuteAsync(command, admit, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false)
                : await ReleaseBrokenAsync(await home!.ExecuteAsync(command, admit, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false),
        };

    private async ValueTask<SqlDescribeOutcome> DescribeAtHomeAsync(SqlCommand command, CancellationToken cancellationToken) =>
        await OpenHomeAsync(cancellationToken).ConfigureAwait(false) switch
        {
            (_, { } error) => error,
            (var home, _) => await home!.DescribeAsync(command, cancellationToken).ConfigureAwait(false),
        };

    // Whatever the home ran may have changed the database: later reads here need a pull that started after its answer.
    private async ValueTask<SqlOutcome> MarkAsync(SqlOutcome outcome)
    {
        _position = replicas.Position;
        replication.PullSoon(database);
        return await ReleaseBrokenAsync(outcome).ConfigureAwait(false);
    }

    private async ValueTask<SqlOutcome> ReleaseBrokenAsync(SqlOutcome outcome)
    {
        if (_home is HomeSqlSession { IsBroken: true } broken)
        {
            _home = null;
            await broken.DisposeAsync().ConfigureAwait(false);
        }

        return outcome;
    }

    private async ValueTask UsePrimaryAsync(CancellationToken cancellationToken)
    {
        // Recovery can retain both a primary and a same-region replica. The
        // admitted writer must read its primary, while followers keep their
        // replica and R10 holds. Preserve any transaction's existing snapshot.
        if (!_primary && !_readOnlyTransaction && (_home is null || _home is SqlSession { IsAutocommit: true })
            && await service.UsePrimaryAsync(database, cancellationToken).ConfigureAwait(false))
        {
            _home ??= local();
            _primary = true;
        }
    }

    private async ValueTask<(ISqlSession? Session, SqlError? Error)> OpenHomeAsync(CancellationToken cancellationToken)
    {
        if (_home is { } open)
        {
            return (open, null);
        }

        if (await placements.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: { Home: var home, Version: var epoch } })
        {
            return (null, new SqlError(SqlErrors.Unavailable, "the database's placement cannot be read now; retry"));
        }

        if (home == options.Region)
        {
            _home = local();
            return (_home, null);
        }

        switch (await homes.OpenAsync(database, home, epoch, canWrite, cancellationToken).ConfigureAwait(false))
        {
            case HomeSqlSession session:
                session.BeginRequest();
                _home = session;
                return (session, null);
            case SqlError error:
                return (null, error);
        }

        return (null, HomeSessions.Unreachable(home));
    }

    private async ValueTask DropSnapshotAsync()
    {
        if (_snapshot is { } snapshot)
        {
            _snapshot = null;
            await snapshot.DisposeAsync().ConfigureAwait(false);
        }
    }
}
