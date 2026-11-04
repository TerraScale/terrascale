using System.Text;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

internal sealed class SqlService(
    IStorageEngine engine, IStorageQuota quota, IUsageMeter usage, IOperationAllowance allowance, TimeProvider time,
    [FromKeyedServices(SqlLimits.TransactionClock)] TimeProvider transactionTime,
    IDatabaseReplicas replicas, IReplicaCatalog placements, HomeSessions homes, RegionReplication replication, TerraBaseOptions options, IHomeApply apply,
    Routing.WriteRouter router, DatabaseFences fences) : ISqlService
{
    private const int MaxTables = 10_000;

    public ISqlSession Open(TenantContext database, bool canWrite)
    {
        ArgumentNullException.ThrowIfNull(database);
        ISqlSession Local() => new SqlSession(engine, quota, usage, allowance, time, transactionTime, database, canWrite, apply, fences);
        // Only a region holding a replica of the database serves it from one; its home always holds the file.
        return replicas.Holds(database)
            ? new ReplicaSqlSession(this, replicas, placements, homes, replication, router, options, transactionTime, database, canWrite, Local)
            : Local();
    }

    public async ValueTask<ReplicaSqlOutcome> ExecuteOnReplicaAsync(
        TenantContext database, IReplicaReader reader, IReadOnlyList<(SqlStatementPlan Plan, SqlCommand Command)> statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(statements);
        var results = new List<SqlResult>(statements.Count);
        long units = 0;
        for (var i = 0; i < statements.Count; i++)
        {
            var (plan, command) = statements[i];
            if (plan.ReadsTables)
            {
                switch (await allowance.CheckAsync(database, MeteredOperation.SqlRead, cancellationToken).ConfigureAwait(false))
                {
                    case QuotaExceeded exceeded:
                        return new ReplicaSqlFailure(SqlErrors.Quota(exceeded.Message), i);
                    case Unavailable:
                        return new ReplicaSqlFailure(SqlErrors.Busy("usage accounting is unavailable; retry"), i);
                }
            }

            if (command.Args.Concat(command.Named.Values).Any(value => value.Kind == SqlValueKind.Real && !double.IsFinite(value.Real)))
            {
                return new ReplicaSqlFailure(SqlErrors.NonFinite, i);
            }

            SqlStatementResult raw;
            try
            {
                raw = await reader.ExecuteAsync(
                    new SqlStatement(await SqlSession.RunTextAsync(plan, statement => reader.ExecuteAsync(statement, cancellationToken)).ConfigureAwait(false), [.. command.Args.Select(SqlSession.Bound)],
                        command.Named.ToDictionary(pair => pair.Key, pair => SqlSession.Bound(pair.Value), StringComparer.Ordinal),
                        SqlSession.MaxRows, SqlSession.MaxBytes, SqlLimits.MaxRequestTime),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqlStatementException error)
            {
                return new ReplicaSqlFailure(SqlSession.Map(error, canWrite: false), i);
            }

            if (plan.HiddenColumns.Count > 0)
            {
                raw = raw with { Rows = SqlSandbox.HideRows(raw.Rows, plan.HiddenColumns) };
            }

            if (raw.Truncated)
            {
                return new ReplicaSqlFailure(SqlSession.TooManyRows, i);
            }

            if (raw.Rows.Any(row => row.Any(value => value is double real && !double.IsFinite(real))))
            {
                return new ReplicaSqlFailure(SqlErrors.NonFinite, i);
            }

            var result = new SqlResult(
                [.. raw.Columns.Select(column => new SqlColumn(column.Name, column.DeclType))],
                [.. raw.Rows.Select(row => (IReadOnlyList<SqlValue>)Array.ConvertAll(row, SqlSession.Value))], 0, null);
            units += plan.ReadsTables ? SqlSession.ReadUnits(result) : 0;
            results.Add(result);
        }

        if (units > 0)
        {
            usage.RecordRead(database, units);
        }

        return results;
    }

    public async ValueTask<IReadOnlyList<SqlTableInfo>> ListTablesAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (replicas.Holds(database))
        {
            // A replica region lists its replica's tables.
            var reader = await replicas.BeginReadAsync(database, cancellationToken).ConfigureAwait(false);
            if (reader is not null)
            {
                await using (reader.ConfigureAwait(false))
                {
                    return await ListTablesAsync(statement => reader.ExecuteAsync(statement, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (await engine.BeginTransactionAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return [];
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await ListTablesAsync(statement => transaction.ExecuteStatementAsync(statement, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<IReadOnlyList<SqlTableInfo>> ListTablesAsync(
        Func<SqlStatement, ValueTask<SqlStatementResult>> run, CancellationToken cancellationToken)
    {
        var names = await run(SqlSession.Internal("SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name")).ConfigureAwait(false);
        var tables = new List<SqlTableInfo>();
        foreach (var row in names.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row[0] is not string name || SqlScript.IsReservedName(name))
            {
                continue;
            }

            if (tables.Count == MaxTables)
            {
                break;
            }

            var columns = await run(SqlSession.Internal($"PRAGMA table_xinfo(\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\")")).ConfigureAwait(false);
            tables.Add(new SqlTableInfo(name, [.. columns.Rows.Select(column => new SqlTableColumn(
                column[1] as string ?? "",
                column[2] is string { Length: > 0 } type ? type : null,
                column[3] is long and not 0 || column[5] is long and > 0,
                column[5] is long and > 0,
                column[4] as string))]));
        }

        return tables;
    }
}


internal sealed class SqlSession(
    IStorageEngine engine, IStorageQuota quota, IUsageMeter usage, IOperationAllowance allowance, TimeProvider time, TimeProvider transactionTime,
    TenantContext database, bool canWrite, IHomeApply apply, DatabaseFences fences)
    : ISqlSession
{
    private const int MaxStatements = 256;
    internal const int MaxRows = 10_000;
    internal const long MaxBytes = 8 * 1024 * 1024;
    private const long MaxChangedRows = 1024;
    private const long MaxGrowthBytes = 8 * 1024 * 1024;
    private const string StatementSavepoint = "_tb_statement";

    private static readonly SqlResult Empty = new([], [], 0, null);
    private static readonly IReadOnlyDictionary<string, object?> NoNamed = new Dictionary<string, object?>();
    private static readonly SqlError UnknownDatabase = SqlErrors.Error("the database does not exist");

    private SqlError Missing() => database.DatabaseId is { } id && fences.MovedTo(id) is { } home
        ? SqlErrors.Moved(new DatabaseMovedException(home))
        : UnknownDatabase;
    private static readonly SqlError TooManyChanges = SqlErrors.TooBig("a transaction changes at most 1024 rows and grows the database by at most 8 MiB");
    internal static readonly SqlError TooManyRows = SqlErrors.TooBig("a result holds at most 10000 rows and 8 MiB; add a LIMIT");
    private static readonly SqlError Expired = new(SqlErrors.QueryCanceled, "the transaction expired: it was open for more than 5 seconds and was rolled back");
    private static readonly SqlError Aborted = new(SqlErrors.TransactionRolledBack, "the transaction was rolled back after a failed statement; end it with ROLLBACK");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _transportStarted;
    private long? _scriptStarted;
    private bool _inTransaction;
    private bool _readOnlyTransaction;
    private Pinned? _pinned;
    private IStorageTransaction? _running;
    private SqlError? _lost;
    private int _generation;
    private int _expiring = -1;
    private string? _plannedSql;
    private SqlPlanOutcome _planned;

    public bool IsAutocommit => !_inTransaction;

    public void BeginRequest() => _transportStarted = time.GetTimestamp();

    public ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken) =>
        ExecuteAsync(command, admit: null, cancellationToken);

    public async ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Plan(command.Sql) switch
            {
                SqlError error => error,
                SqlStatementPlan plan => await ExecuteLockedAsync(plan, command, admit, first: false, cancellationToken).ConfigureAwait(false),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, CancellationToken cancellationToken) =>
        ExecuteScriptAsync(sql, MaxStatements, cancellationToken);

    public async ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, int maxStatements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (SqlScript.CountAtMost(sql) > maxStatements)
        {
            return SqlErrors.TooBig($"a script holds at most {maxStatements} statements");
        }

        SqlOutcome last = Empty;
        _scriptStarted = time.GetTimestamp();
        try
        {
            var remaining = sql;
            for (var count = 0; SqlScript.HasStatement(remaining); count++)
            {
                if (count == maxStatements)
                {
                    return SqlErrors.TooBig($"a script holds at most {maxStatements} statements");
                }

                if (RequestExpired)
                {
                    return SqlErrors.Interrupt;
                }

                int consumed;
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var text = SqlScript.IsSimple(remaining) ? remaining[..SqlScript.SimpleLength(remaining)] : remaining;
                    if (SqlScript.Classify(text, bounded: false) is not SqlStatementPlan plan)
                    {
                        return SqlScript.Classify(text, bounded: false) is SqlError refused ? refused : SqlErrors.Error("SQL syntax error in the script");
                    }

                    _consumed = 0;
                    last = await ExecuteLockedAsync(plan, new SqlCommand(plan.Sql, [], new Dictionary<string, SqlValue>()), admit: null, first: true, cancellationToken)
                        .ConfigureAwait(false);
                    consumed = remaining.IndexOf(plan.Sql, StringComparison.Ordinal)
                        + (plan.GuardsTrigger && _consumed > 0 ? _consumed - (plan.Run.Length - plan.Sql.Length)
                            : !ReferenceEquals(plan.Run, plan.Sql) ? plan.Sql.Length : _consumed > 0 ? _consumed : SqlScript.SimpleLength(plan.Sql));
                }
                finally
                {
                    _gate.Release();
                }

                if (last is SqlError)
                {
                    break;
                }

                if (last is SqlResult result)
                {
                    result.ReadCharge?.Accept();
                }

                remaining = remaining[Math.Min(remaining.Length, consumed)..];
            }
        }
        finally
        {
            _scriptStarted = null;
        }

        return last;
    }

    public async ValueTask<SqlDescribeOutcome> DescribeAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Plan(command.Sql) is not SqlStatementPlan plan)
            {
                return Plan(command.Sql) is SqlError error ? error : SqlErrors.Error("the statement cannot be described");
            }

            await ExpireIfDueAsync().ConfigureAwait(false);
            if (_lost is { } lost)
            {
                return lost;
            }

            if (plan.Kind is not (SqlStatementKind.Read or SqlStatementKind.Write))
            {
                return new SqlDescription([], [], IsExplain: false, IsReadOnly: plan.Kind != SqlStatementKind.Write);
            }

            if (_inTransaction)
            {
                var (pinned, refused) = await PinAsync(cancellationToken).ConfigureAwait(false);
                return pinned is null ? refused! : await DescribeInAsync(pinned.Transaction, plan, cancellationToken).ConfigureAwait(false);
            }

            var (transaction, _, failed) = await OpenAsync(plan.Kind == SqlStatementKind.Write && canWrite, cancellationToken, enterQuota: false).ConfigureAwait(false);
            if (transaction is null)
            {
                return failed!;
            }

            await using (transaction.ConfigureAwait(false))
            {
                return await DescribeInAsync(transaction, plan, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask EndRequestAsync()
    {
        _transportStarted = null;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        (Volatile.Read(ref _running) ?? Volatile.Read(ref _pinned)?.Transaction)?.InterruptStatement();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DropAsync().ConfigureAwait(false);
            _inTransaction = false;
            _lost = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static SqlStatement Internal(string sql) => new(sql, [], NoNamed, MaxRows, MaxBytes, SqlLimits.MaxRequestTime);

    internal static async ValueTask<string> RunTextAsync(SqlStatementPlan plan, Func<SqlStatement, ValueTask<SqlStatementResult>> run)
    {
        if (plan.RunWithoutConflicts is not { } without)
        {
            return plan.Run;
        }

        var found = await run(Internal("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = '_ts_conflicts'")).ConfigureAwait(false);
        return found.Rows.Count > 0 ? plan.Run : without;
    }

    private int _consumed;

    private bool RequestExpired =>
        Math.Min(_transportStarted ?? long.MaxValue, _scriptStarted ?? long.MaxValue) is var started && started != long.MaxValue
        && time.GetElapsedTime(started) > SqlLimits.MaxRequestTime;

    private SqlPlanOutcome Plan(string sql)
    {
        if (!string.Equals(sql, _plannedSql, StringComparison.Ordinal))
        {
            _planned = SqlScript.Classify(sql);
            _plannedSql = sql;
        }

        return _planned;
    }

    private TimeSpan StatementTimeout()
    {
        var timeout = SqlLimits.MaxRequestTime;
        if (QosContext.Current is { } tenant && QosPolicy.For(tenant.Plan).QueryTimeout is var limit && limit != Timeout.InfiniteTimeSpan)
        {
            timeout = Min(timeout, limit);
        }

        if (Math.Min(_transportStarted ?? long.MaxValue, _scriptStarted ?? long.MaxValue) is var started && started != long.MaxValue)
        {
            timeout = Min(timeout, SqlLimits.MaxRequestTime - time.GetElapsedTime(started));
        }

        if (_pinned is { } pinned)
        {
            timeout = Min(timeout, SqlLimits.MaxTransactionTime - transactionTime.GetElapsedTime(pinned.Opened));
        }

        return timeout > TimeSpan.FromMilliseconds(1) ? timeout : TimeSpan.FromMilliseconds(1);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private async ValueTask<SqlOutcome> ExecuteLockedAsync(
        SqlStatementPlan plan, SqlCommand command, Func<SqlResult, SqlError?>? admit, bool first, CancellationToken cancellationToken)
    {
        if (command.Args.Concat(command.Named.Values).Any(value => value.Kind == SqlValueKind.Real && !double.IsFinite(value.Real)))
        {
            return SqlErrors.NonFinite;
        }

        await ExpireIfDueAsync().ConfigureAwait(false);
        if (_lost is { } lost)
        {
            if (plan.Kind is SqlStatementKind.Rollback or SqlStatementKind.Commit)
            {
                (_lost, _inTransaction) = (null, false);
            }

            return plan.Kind == SqlStatementKind.Rollback ? admit?.Invoke(Empty) ?? (SqlOutcome)Empty : lost;
        }

        switch (plan.Kind)
        {
            case SqlStatementKind.Begin when _inTransaction:
                return SqlErrors.Error("cannot start a transaction within a transaction");
            case SqlStatementKind.Commit when !_inTransaction:
                return SqlErrors.Error("cannot commit - no transaction is active");
            case SqlStatementKind.Savepoint or SqlStatementKind.Release or SqlStatementKind.RollbackTo when !_inTransaction:
                return SqlErrors.Error("savepoints run only inside a transaction: BEGIN one first");
            case SqlStatementKind.Write when !canWrite:
                return SqlErrors.Auth;
            case SqlStatementKind.Write when _inTransaction && _readOnlyTransaction:
                return new SqlError(SqlErrors.ReadOnlyTransaction, "a READ ONLY transaction cannot write");
            case SqlStatementKind.Noop or SqlStatementKind.Begin or SqlStatementKind.Commit or SqlStatementKind.Rollback when admit?.Invoke(Empty) is { } refused:
                return refused;
            case SqlStatementKind.Noop:
                return Empty;
            case SqlStatementKind.Begin:
                (_inTransaction, _readOnlyTransaction) = (true, plan.ReadOnly);
                return Empty;
            case SqlStatementKind.Commit:
                return await CommitAsync(cancellationToken).ConfigureAwait(false);
            case SqlStatementKind.Rollback:
                await DropAsync().ConfigureAwait(false);
                _inTransaction = false;
                return Empty;
            case SqlStatementKind.Savepoint or SqlStatementKind.Release or SqlStatementKind.RollbackTo:
                return await SavepointAsync(plan, admit, first, cancellationToken).ConfigureAwait(false);
        }

        if (await AllowedAsync(plan, cancellationToken).ConfigureAwait(false) is { } capped)
        {
            return capped;
        }

        switch (plan.Kind)
        {
            case var _ when _inTransaction:
                return await InTransactionAsync(plan, command, admit, first, cancellationToken).ConfigureAwait(false);
            default:
                return await AutocommitAsync(plan, command, admit, first, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<SqlError?> AllowedAsync(SqlStatementPlan plan, CancellationToken cancellationToken)
    {
        MeteredOperation? metered = plan.Kind switch
        {
            SqlStatementKind.Write => MeteredOperation.Write,
            SqlStatementKind.Read when plan.ReadsTables => MeteredOperation.SqlRead,
            _ => null,
        };
        if (metered is not { } operation)
        {
            return null;
        }

        return await allowance.CheckAsync(database, operation, cancellationToken).ConfigureAwait(false) switch
        {
            QuotaExceeded exceeded => SqlErrors.Quota(exceeded.Message),
            Unavailable => SqlErrors.Busy("usage accounting is unavailable; retry"),
            _ => null,
        };
    }

    private async ValueTask<SqlOutcome> AutocommitAsync(
        SqlStatementPlan plan, SqlCommand command, Func<SqlResult, SqlError?>? admit, bool first, CancellationToken cancellationToken)
    {
        var write = plan.Kind == SqlStatementKind.Write;
        var (transaction, lease, failed) = await OpenAsync(write, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            return failed!;
        }

        using (lease)
        {
            await using (transaction.ConfigureAwait(false))
            {
                long baseline = 0;
                if (write)
                {
                    if (await MeasureAsync(transaction, cancellationToken).ConfigureAwait(false) is not long measured)
                    {
                        return SqlErrors.Busy("the database is busy; retry");
                    }

                    baseline = measured;
                }

                var tally = new Tally();
                var (outcome, _) = await RunAsync(transaction, plan, command, tally, admit, first, cancellationToken).ConfigureAwait(false);
                if (outcome is not SqlResult result)
                {
                    return outcome;
                }

                if (write && await CommitAsync(transaction, lease, tally, baseline, cancellationToken).ConfigureAwait(false) is { } refused)
                {
                    return refused;
                }

                return Charged(plan, result);
            }
        }
    }

    private async ValueTask<SqlOutcome> InTransactionAsync(
        SqlStatementPlan plan, SqlCommand command, Func<SqlResult, SqlError?>? admit, bool first, CancellationToken cancellationToken)
    {
        var (pinned, refused) = await PinAsync(cancellationToken).ConfigureAwait(false);
        if (pinned is null)
        {
            return refused!;
        }

        var guarded = plan.Kind == SqlStatementKind.Write;
        if (guarded && await ControlAsync(pinned, $"SAVEPOINT {StatementSavepoint}", cancellationToken).ConfigureAwait(false) is { } unsaved)
        {
            return unsaved;
        }

        var before = pinned.Tally.Snapshot();
        var (outcome, ended) = await RunAsync(pinned.Transaction, plan, command, pinned.Tally, admit, first, cancellationToken).ConfigureAwait(false);
        if (ended)
        {
            await LoseAsync(LostReason()).ConfigureAwait(false);
            return outcome;
        }

        if (!guarded)
        {
            return outcome is SqlResult read ? Charged(plan, read) : outcome;
        }

        if (outcome is not SqlResult result)
        {
            pinned.Tally.Restore(before);
            return await ControlAsync(pinned, $"ROLLBACK TO {StatementSavepoint}", cancellationToken).ConfigureAwait(false)
                ?? await ControlAsync(pinned, $"RELEASE {StatementSavepoint}", cancellationToken).ConfigureAwait(false)
                ?? outcome;
        }

        return await ControlAsync(pinned, $"RELEASE {StatementSavepoint}", cancellationToken).ConfigureAwait(false) is { } unreleased
            ? unreleased
            : Charged(plan, result);
    }

    private async ValueTask<SqlOutcome> SavepointAsync(SqlStatementPlan plan, Func<SqlResult, SqlError?>? admit, bool first, CancellationToken cancellationToken)
    {
        var (pinned, failed) = await PinAsync(cancellationToken).ConfigureAwait(false);
        if (pinned is null)
        {
            return failed!;
        }

        if (admit?.Invoke(Empty) is { } refused)
        {
            return refused;
        }

        if (await ControlAsync(pinned, plan.Sql, cancellationToken, first).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        var name = plan.Savepoint ?? "";
        var at = pinned.Savepoints.FindLastIndex(savepoint => string.Equals(savepoint.Name, name, StringComparison.OrdinalIgnoreCase));
        switch (plan.Kind)
        {
            case SqlStatementKind.Savepoint:
                pinned.Savepoints.Add((name, pinned.Tally.Snapshot()));
                break;
            case SqlStatementKind.RollbackTo when at >= 0:
                pinned.Tally.Restore(pinned.Savepoints[at].Snapshot);
                pinned.Savepoints.RemoveRange(at + 1, pinned.Savepoints.Count - at - 1);
                break;
            case SqlStatementKind.Release when at >= 0:
                pinned.Savepoints.RemoveRange(at, pinned.Savepoints.Count - at);
                break;
        }

        return Empty;
    }

    private async ValueTask<SqlError?> ControlAsync(Pinned pinned, string sql, CancellationToken cancellationToken, bool first = false)
    {
        try
        {
            var result = await pinned.Transaction.ExecuteStatementAsync(Internal(sql) with { First = first }, cancellationToken).ConfigureAwait(false);
            if (first)
            {
                _consumed = result.Consumed;
            }

            return null;
        }
        catch (SqlStatementException error) when (error.TransactionEnded)
        {
            await LoseAsync(LostReason()).ConfigureAwait(false);
            return Map(error);
        }
        catch (SqlStatementException error)
        {
            return Map(error);
        }
    }

    private SqlError LostReason() =>
        _expiring == _generation || (_pinned is { } pinned && transactionTime.GetElapsedTime(pinned.Opened) >= SqlLimits.MaxTransactionTime) ? Expired : Aborted;

    private async ValueTask<(SqlOutcome Outcome, bool Ended)> RunAsync(
        IStorageTransaction transaction, SqlStatementPlan plan, SqlCommand command, Tally tally, Func<SqlResult, SqlError?>? admit, bool first,
        CancellationToken cancellationToken)
    {
        if (RequestExpired)
        {
            return (SqlErrors.Interrupt, false);
        }

        SqlStatementResult raw;
        Volatile.Write(ref _running, transaction);
        try
        {
            raw = await transaction.ExecuteStatementAsync(
                new SqlStatement(await RunTextAsync(plan, statement => transaction.ExecuteStatementAsync(statement, cancellationToken)).ConfigureAwait(false),
                    [.. command.Args.Select(Bound)], command.Named.ToDictionary(pair => pair.Key, pair => Bound(pair.Value), StringComparer.Ordinal),
                    MaxRows, MaxBytes, StatementTimeout(), first),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqlStatementException error)
        {
            return (Map(error), error.TransactionEnded);
        }
        catch (Exception error) when (Failure(error) is { } failure)
        {
            return (failure, false);
        }
        finally
        {
            Volatile.Write(ref _running, null);
        }

        _consumed = raw.Consumed;
        if (plan.HiddenColumns.Count > 0)
        {
            raw = raw with { Rows = SqlSandbox.HideRows(raw.Rows, plan.HiddenColumns) };
        }

        if (raw.Truncated)
        {
            return (TooManyRows, false);
        }

        if (tally.Work + raw.RowsAffected > MaxChangedRows || tally.Growth + raw.GrowthBytes > MaxGrowthBytes)
        {
            return (TooManyChanges, false);
        }

        if (raw.Rows.Any(row => row.Any(value => value is double real && !double.IsFinite(real))))
        {
            return (SqlErrors.NonFinite, false);
        }

        var result = new SqlResult(
            [.. raw.Columns.Select(column => new SqlColumn(column.Name, column.DeclType))],
            [.. raw.Rows.Select(row => (IReadOnlyList<SqlValue>)Array.ConvertAll(row, Value))],
            plan.Kind == SqlStatementKind.Write ? raw.RowsAffected : 0,
            plan.Verb == SqlWriteVerb.Insert && raw.RowsAffected > 0 ? raw.LastInsertRowid : null);
        if (admit?.Invoke(result) is { } refused)
        {
            return (refused, false);
        }

        tally.Add(plan, raw);
        return (result, false);
    }

    private async ValueTask<SqlDescribeOutcome> DescribeInAsync(IStorageTransaction transaction, SqlStatementPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            var run = await RunTextAsync(plan, statement => transaction.ExecuteStatementAsync(statement, cancellationToken)).ConfigureAwait(false);
            var shape = await transaction.DescribeStatementAsync(run, cancellationToken).ConfigureAwait(false);
            return new SqlDescription(shape.Parameters, [.. shape.Columns.Select(column => new SqlColumn(column.Name, column.DeclType))],
                plan.IsExplain, plan.Kind != SqlStatementKind.Write);
        }
        catch (SqlStatementException error)
        {
            if (error.TransactionEnded && _inTransaction)
            {
                await LoseAsync(LostReason()).ConfigureAwait(false);
            }

            return Map(error);
        }
        catch (Exception error) when (Failure(error) is { } failure)
        {
            return failure;
        }
    }

    private async ValueTask<SqlOutcome> CommitAsync(CancellationToken cancellationToken)
    {
        var pinned = _pinned;
        _inTransaction = false;
        if (pinned is null)
        {
            return Empty;
        }

        try
        {
            return !pinned.Write || !pinned.Tally.Wrote
                ? Empty
                : await CommitAsync(pinned.Transaction, pinned.Lease, pinned.Tally, pinned.Baseline, cancellationToken).ConfigureAwait(false) is { } failed ? failed : Empty;
        }
        finally
        {
            await DropAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<SqlError?> CommitAsync(IStorageTransaction transaction, StorageQuotaLease? lease, Tally tally, long baseline, CancellationToken cancellationToken)
    {
        try
        {
            await apply.StampHomeWritesAsync(transaction, cancellationToken).ConfigureAwait(false);
            if (await MeasureAsync(transaction, cancellationToken).ConfigureAwait(false) is not long after)
            {
                return SqlErrors.Busy("the database is busy; retry");
            }

            var growth = after - baseline;
            if (lease is not null)
            {
                switch (await lease.CheckAsync(growth, cancellationToken).ConfigureAwait(false))
                {
                    case QuotaExceeded:
                        return new SqlError(SqlErrors.QuotaExceeded, "the organization's storage quota is exhausted");
                    case Unavailable:
                        return SqlErrors.Busy("storage accounting is unavailable; retry");
                }
            }

            if (growth != 0)
            {
                await transaction.AddSqlStorageAsync(growth, cancellationToken).ConfigureAwait(false);
            }

            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            lease?.Committed(growth);
            if (tally.Rows > 0)
            {
                usage.RecordWrites(database, (int)tally.Rows, Math.Max(tally.Rows, OperationUnits.Of(Math.Max(0, growth))));
                foreach (var (kind, count) in tally.Operations)
                {
                    usage.RecordOperation(database, kind, count);
                }
            }

            return null;
        }
        catch (SqlStatementException error)
        {
            return Map(error);
        }
        catch (Exception error) when (Failure(error) is { } failure)
        {
            return failure;
        }
    }

    private static async ValueTask<long?> MeasureAsync(IStorageTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            return await transaction.MeasureSqlStorageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlStatementException)
        {
            return null;
        }
    }

    private async ValueTask<(Pinned? Pinned, SqlError? Error)> PinAsync(CancellationToken cancellationToken)
    {
        if (_pinned is { } pinned)
        {
            return (pinned, null);
        }

        var write = canWrite && !_readOnlyTransaction;
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return (null, Missing());
        }

        StorageQuotaLease? lease = null;
        if (write)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(SqlLimits.MaxTransactionTime);
            try
            {
                switch (await quota.EnterAsync(database, store, bounded.Token).ConfigureAwait(false))
                {
                    case StorageQuotaLease admitted:
                        lease = admitted;
                        break;
                    case Unavailable:
                        return (null, SqlErrors.Busy("storage accounting is unavailable; retry"));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (null, SqlErrors.Busy("storage accounting is busy; retry"));
            }
        }

        IStorageTransaction transaction;
        try
        {
            transaction = await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseMovedException moved)
        {
            lease?.Dispose();
            return (null, SqlErrors.Moved(moved));
        }
        catch (StorageBusyException)
        {
            lease?.Dispose();
            return (null, SqlErrors.Busy("the database is busy; retry"));
        }
        catch
        {
            lease?.Dispose();
            throw;
        }

        long baseline = 0;
        if (write && await MeasureAsync(transaction, cancellationToken).ConfigureAwait(false) is long measured)
        {
            baseline = measured;
        }

        var generation = ++_generation;
        var timer = transactionTime.CreateTimer(_ => _ = ExpireLaterAsync(generation), null, SqlLimits.MaxTransactionTime, Timeout.InfiniteTimeSpan);
        _pinned = new Pinned(transaction, lease, write, transactionTime.GetTimestamp(), baseline, timer, new Tally(), []);
        return (_pinned, null);
    }

    private async Task ExpireLaterAsync(int generation)
    {
        if (Volatile.Read(ref _pinned) is { } pinned && generation == Volatile.Read(ref _generation))
        {
            Volatile.Write(ref _expiring, generation);
            pinned.Transaction.InterruptStatement();
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation == _generation && _pinned is not null && _lost is null)
            {
                await LoseAsync(Expired).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask ExpireIfDueAsync()
    {
        if (_pinned is { } pinned && _lost is null && transactionTime.GetElapsedTime(pinned.Opened) >= SqlLimits.MaxTransactionTime)
        {
            await LoseAsync(Expired).ConfigureAwait(false);
        }
    }

    private async ValueTask LoseAsync(SqlError error)
    {
        _lost = _inTransaction ? error : null;
        await DropAsync().ConfigureAwait(false);
    }

    private async ValueTask DropAsync()
    {
        if (_pinned is not { } pinned)
        {
            return;
        }

        _pinned = null;
        _generation++;
        await pinned.Timer.DisposeAsync().ConfigureAwait(false);
        try
        {
            await pinned.Transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            pinned.Lease?.Dispose();
        }
    }

    private async ValueTask<(IStorageTransaction? Transaction, StorageQuotaLease? Lease, SqlError? Error)> OpenAsync(
        bool write, CancellationToken cancellationToken, bool enterQuota = true)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return (null, null, Missing());
        }

        StorageQuotaLease? lease = null;
        if (write && enterQuota)
        {
            switch (await quota.EnterAsync(database, store, cancellationToken).ConfigureAwait(false))
            {
                case StorageQuotaLease admitted:
                    lease = admitted;
                    break;
                case Unavailable:
                    return (null, null, SqlErrors.Busy("storage accounting is unavailable; retry"));
            }
        }

        try
        {
            return (await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false), lease, null);
        }
        catch (DatabaseMovedException moved)
        {
            lease?.Dispose();
            return (null, null, SqlErrors.Moved(moved));
        }
        catch (StorageBusyException)
        {
            lease?.Dispose();
            return (null, null, SqlErrors.Busy("the database is busy; retry"));
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    private SqlResult Charged(SqlStatementPlan plan, SqlResult result)
    {
        if (plan.Kind != SqlStatementKind.Read || !plan.ReadsTables)
        {
            return result;
        }

        var units = ReadUnits(result);
        return result with { ReadCharge = new SqlReadCharge(() => usage.RecordRead(database, units)) };
    }

    internal static long ReadUnits(SqlResult result) =>
        result.Rows.Sum(row => Math.Max(1, OperationUnits.Of(row.Sum(value => value.Kind switch
        {
            SqlValueKind.Text => (long)Encoding.UTF8.GetByteCount(value.Text!),
            SqlValueKind.Blob => value.Blob!.LongLength,
            SqlValueKind.Null => 0L,
            _ => 8L,
        }))));

    private SqlError Map(SqlStatementException error) => Map(error, canWrite);

    internal static SqlError Map(SqlStatementException error, bool canWrite) => error.Failure switch
    {
        SqlFailure.PrimaryKey => new SqlError(SqlErrors.PrimaryKeyViolation, error.Message),
        SqlFailure.Unique => new SqlError(SqlErrors.UniqueViolation, error.Message),
        SqlFailure.NotNull => new SqlError(SqlErrors.NotNullViolation, error.Message),
        SqlFailure.Check => new SqlError(SqlErrors.CheckViolation, error.Message),
        SqlFailure.ForeignKey => new SqlError(SqlErrors.ForeignKeyViolation, error.Message),
        SqlFailure.Constraint => new SqlError(SqlErrors.ConstraintViolation, error.Message),
        SqlFailure.Syntax => new SqlError(SqlErrors.SyntaxError, error.Message),
        SqlFailure.NoSuchTable => new SqlError(SqlErrors.UndefinedTable, error.Message),
        SqlFailure.NoSuchColumn => new SqlError(SqlErrors.UndefinedColumn, error.Message),
        SqlFailure.AlreadyExists => new SqlError(SqlErrors.DuplicateObject, error.Message),
        SqlFailure.Busy => SqlErrors.Busy(error.Message),
        SqlFailure.TooBig => SqlErrors.TooBig(error.Message),
        SqlFailure.Interrupted => SqlErrors.Interrupt,
        SqlFailure.ReadOnly => canWrite ? new SqlError(SqlErrors.ReadOnlyTransaction, error.Message) : SqlErrors.Auth,
        _ => SqlErrors.Error(error.Message),
    };

    private static SqlError? Failure(Exception error) => error switch
    {
        StorageBusyException => SqlErrors.Busy("the database is busy; retry"),
        DatabaseMovedException moved => SqlErrors.Moved(moved),
        QueryCapacityException => SqlErrors.Interrupt,
        _ => null,
    };

    internal static object? Bound(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Integer => value.Integer,
        SqlValueKind.Real => value.Real,
        SqlValueKind.Text => value.Text,
        SqlValueKind.Blob => value.Blob,
        _ => null,
    };

    internal static SqlValue Value(object? value) => value switch
    {
        long integer => SqlValue.Of(integer),
        double real => SqlValue.Of(real),
        string text => SqlValue.Of(text),
        byte[] blob => SqlValue.Of(blob),
        _ => SqlValue.Null,
    };

    private sealed record Pinned(
        IStorageTransaction Transaction, StorageQuotaLease? Lease, bool Write, long Opened, long Baseline, ITimer Timer, Tally Tally,
        List<(string Name, TallySnapshot Snapshot)> Savepoints);

    private sealed record TallySnapshot(long Rows, bool Wrote, Dictionary<string, int> Operations);

    private sealed class Tally
    {
        public long Rows { get; private set; }

        public long Work { get; private set; }

        public long Growth { get; private set; }

        public bool Wrote { get; private set; }

        public Dictionary<string, int> Operations { get; private set; } = [with(StringComparer.Ordinal)];

        public TallySnapshot Snapshot() => new(Rows, Wrote, new Dictionary<string, int>(Operations, StringComparer.Ordinal));

        public void Restore(TallySnapshot snapshot)
        {
            Rows = snapshot.Rows;
            Wrote = snapshot.Wrote;
            Operations = new Dictionary<string, int>(snapshot.Operations, StringComparer.Ordinal);
        }

        public void Add(SqlStatementPlan plan, SqlStatementResult result)
        {
            Rows += result.RowsAffected;
            Work += result.RowsAffected;
            Growth += result.GrowthBytes;
            Wrote |= plan.Kind == SqlStatementKind.Write;
            if (plan.Verb is SqlWriteVerb.Insert or SqlWriteVerb.Update or SqlWriteVerb.Delete && result.RowsAffected > 0)
            {
                var kind = plan.Verb switch { SqlWriteVerb.Insert => "put", SqlWriteVerb.Update => "patch", _ => "delete" };
                Operations[kind] = Operations.GetValueOrDefault(kind) + (int)result.RowsAffected;
            }
        }
    }
}
