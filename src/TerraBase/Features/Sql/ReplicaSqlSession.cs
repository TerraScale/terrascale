using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

/// <summary>
/// A Postgres or WebSocket SQL session in a region that holds a replica of its database (docs/terrabase/region-replicas.md).
/// Reads outside a transaction, and READ ONLY transactions, run on the local replica. Writes, other transactions and reads
/// the replica cannot answer yet run on one session at the home. Read-your-writes: after anything the home ran, a read
/// waits for a replica pull that started after the home's answer, and goes to the home until one completed.
/// </summary>
internal sealed class ReplicaSqlSession(
    SqlService service, IDatabaseReplicas replicas, IReplicaCatalog placements, HomeSessions homes, RegionReplication replication,
    TerraBaseOptions options, TimeProvider transactionTime, TenantContext database, bool canWrite, Func<ISqlSession> local) : ISqlSession
{
    private static readonly SqlResult Empty = new([], [], 0, null);
    private static readonly SqlError ReadOnlyWrite = new(SqlErrors.ReadOnlyTransaction, "a READ ONLY transaction cannot write");
    private static readonly SqlError Expired = new(SqlErrors.QueryCanceled, "the transaction expired: it was open for more than 5 seconds and was rolled back");

    private ISqlSession? _home;
    private IReplicaReader? _snapshot;
    private bool _readOnlyTransaction;
    private bool _expired;
    private long _opened;
    private long _position = -1;

    public bool IsAutocommit => !_readOnlyTransaction && (_home?.IsAutocommit ?? true);

    public void BeginRequest() => _home?.BeginRequest();

    public ValueTask EndRequestAsync() => _home?.EndRequestAsync() ?? ValueTask.CompletedTask;

    public ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken) => ExecuteAsync(command, admit: null, cancellationToken);

    public async ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_home is { IsAutocommit: false })
        {
            return await HomeAsync(command, admit, cancellationToken).ConfigureAwait(false);
        }

        if (SqlScript.Classify(command.Sql) is not SqlStatementPlan plan)
        {
            return SqlScript.Classify(command.Sql) is SqlError refused ? refused : SqlErrors.Error("the statement cannot be run");
        }

        if (_readOnlyTransaction)
        {
            return await InSnapshotAsync(plan, command, admit, cancellationToken).ConfigureAwait(false);
        }

        switch (plan.Kind)
        {
            case SqlStatementKind.Read when await ReadAsync(plan, command, admit, cancellationToken).ConfigureAwait(false) is { } read:
                return read;
            case SqlStatementKind.Begin when plan.ReadOnly
                && await replicas.BeginReadAsync(database, _position, cancellationToken).ConfigureAwait(false) is { } snapshot:
                (_snapshot, _readOnlyTransaction, _expired, _opened) = (snapshot, true, false, transactionTime.GetTimestamp());
                return Empty;
            case SqlStatementKind.Write when !canWrite:
                return SqlErrors.Auth;
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
        if (_home is { IsAutocommit: false })
        {
            return await DescribeAtHomeAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (SqlScript.Classify(command.Sql) is not SqlStatementPlan plan)
        {
            return SqlScript.Classify(command.Sql) is SqlError refused ? refused : SqlErrors.Error("the statement cannot be described");
        }

        if (plan.Kind is not (SqlStatementKind.Read or SqlStatementKind.Write))
        {
            return new SqlDescription([], [], IsExplain: false, IsReadOnly: plan.Kind != SqlStatementKind.Write);
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
            var expired = _expired;
            await DropSnapshotAsync().ConfigureAwait(false);
            _readOnlyTransaction = false;
            return expired && plan.Kind == SqlStatementKind.Commit ? Expired : Empty;
        }

        if (!_expired && transactionTime.GetElapsedTime(_opened) >= SqlLimits.MaxTransactionTime)
        {
            _expired = true;
            await DropSnapshotAsync().ConfigureAwait(false);
        }

        return plan.Kind switch
        {
            _ when _expired || _snapshot is null => Expired,
            SqlStatementKind.Begin => SqlErrors.Error("cannot start a transaction within a transaction"),
            SqlStatementKind.Write => canWrite ? ReadOnlyWrite : SqlErrors.Auth,
            SqlStatementKind.Read => await RunOnAsync(_snapshot, plan, command, admit, cancellationToken).ConfigureAwait(false),
            _ => Empty,
        };
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

    private async ValueTask<(ISqlSession? Session, SqlError? Error)> OpenHomeAsync(CancellationToken cancellationToken)
    {
        if (_home is { } open)
        {
            return (open, null);
        }

        if (await placements.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement.Home: var home })
        {
            return (null, new SqlError(SqlErrors.Unavailable, "the database's placement cannot be read now; retry"));
        }

        if (home == options.Region)
        {
            _home = local();
            return (_home, null);
        }

        switch (await homes.OpenAsync(database, home, canWrite, cancellationToken).ConfigureAwait(false))
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
