using System.Text;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

internal sealed class SqlService(
    IStorageEngine engine, IStorageQuota quota, IUsageMeter usage, TimeProvider time,
    [FromKeyedServices(SqlLimits.TransactionClock)] TimeProvider transactionTime) : ISqlService
{
    private const int MaxTables = 10_000;

    public ISqlSession Open(TenantContext database, bool canWrite)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new SqlSession(engine, quota, usage, time, transactionTime, database, canWrite);
    }

    public async ValueTask<IReadOnlyList<SqlTableInfo>> ListTablesAsync(TenantContext database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (await engine.BeginTransactionAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return [];
        }

        await using (transaction.ConfigureAwait(false))
        {
            var collections = await SqlSession.CollectionsAsync(transaction, cancellationToken).ConfigureAwait(false);
            var names = await transaction.ExecuteStatementAsync(
                SqlSession.Internal("SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name"), cancellationToken).ConfigureAwait(false);
            var tables = new List<SqlTableInfo>();
            foreach (var row in names.Rows)
            {
                if (row[0] is not string name || SqlScript.IsReservedName(name) || collections.Contains(name))
                {
                    continue;
                }

                if (tables.Count == MaxTables)
                {
                    break;
                }

                var columns = await transaction.ExecuteStatementAsync(
                    SqlSession.Internal($"PRAGMA table_xinfo(\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\")"), cancellationToken).ConfigureAwait(false);
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
}

internal sealed class SqlSession(
    IStorageEngine engine, IStorageQuota quota, IUsageMeter usage, TimeProvider time, TimeProvider transactionTime, TenantContext database, bool canWrite)
    : ISqlSession
{
    private const int MaxStatements = 256;
    private const int MaxRows = 10_000;
    private const long MaxBytes = 8 * 1024 * 1024;
    private const long MaxChangedRows = 1024;
    private const long MaxGrowthBytes = 8 * 1024 * 1024;
    private const string StatementSavepoint = "_tb_statement";

    private static readonly SqlResult Empty = new([], [], 0, null);
    private static readonly IReadOnlyDictionary<string, object?> NoNamed = new Dictionary<string, object?>();
    private static readonly SqlError UnknownDatabase = SqlErrors.Error("the database does not exist");
    private static readonly SqlError TooManyChanges = SqlErrors.TooBig("a transaction changes at most 1024 rows and grows the database by at most 8 MiB");
    private static readonly SqlError TooManyRows = SqlErrors.TooBig("a result holds at most 10000 rows and 8 MiB; add a LIMIT");
    private static readonly SqlError Expired = new("SQLITE_INTERRUPT", "the transaction expired: it was open for more than 5 seconds and was rolled back");
    private static readonly SqlError Aborted = new("SQLITE_ABORT", "the transaction was rolled back after a failed statement; end it with ROLLBACK");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _transportStarted;
    private long? _scriptStarted;
    private bool _inTransaction;
    private bool _readOnlyTransaction;
    private Pinned? _pinned;
    private IStorageTransaction? _running;
    private int _expiring = -1;
    private SqlError? _lost;
    private int _generation;
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
            return await ExecuteLockedAsync(command, admit, cancellationToken).ConfigureAwait(false);
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
        List<string> statements;
        switch (SqlScript.Split(sql))
        {
            case SqlError error:
                return error;
            case List<string> split:
                statements = split;
                break;
            default:
                return SqlErrors.Error("SQL syntax error in the script");
        }

        if (statements.Count > maxStatements)
        {
            return SqlErrors.TooBig($"a script holds at most {maxStatements} statements");
        }

        SqlOutcome last = Empty;
        _scriptStarted = time.GetTimestamp();
        try
        {
            foreach (var statement in statements)
            {
                if (RequestExpired)
                {
                    return SqlErrors.Interrupt;
                }

                last = await ExecuteAsync(new SqlCommand(statement, [], new Dictionary<string, SqlValue>()), cancellationToken).ConfigureAwait(false);
                if (last is SqlError)
                {
                    break;
                }

                if (last is SqlResult result)
                {
                    result.ReadCharge?.Accept();
                }
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

    internal static async ValueTask<HashSet<string>> CollectionsAsync(IStorageTransaction transaction, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? after = null;
        while (names.Count < 10_000)
        {
            var (collections, hasMore) = await transaction.ListCollectionsAsync(after, 200, cancellationToken).ConfigureAwait(false);
            foreach (var collection in collections)
            {
                names.Add(collection.Name);
            }

            if (!hasMore || collections.Count == 0)
            {
                break;
            }

            after = collections[^1].Name;
        }

        return names;
    }

    private bool RequestExpired =>
        Math.Min(_transportStarted ?? long.MaxValue, _scriptStarted ?? long.MaxValue) is var started && started != long.MaxValue
        && time.GetElapsedTime(started) > SqlLimits.MaxRequestTime;

    private TimeSpan StatementTimeout()
    {
        var timeout = SqlLimits.MaxRequestTime;
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

    private SqlError LostReason() =>
        _expiring == _generation || (_pinned is { } pinned && transactionTime.GetElapsedTime(pinned.Opened) >= SqlLimits.MaxTransactionTime) ? Expired : Aborted;

    private SqlPlanOutcome Plan(string sql)
    {
        if (!string.Equals(sql, _plannedSql, StringComparison.Ordinal))
        {
            _planned = SqlScript.Classify(sql);
            _plannedSql = sql;
        }

        return _planned;
    }

    private async ValueTask<SqlOutcome> ExecuteLockedAsync(SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
    {
        if (Plan(command.Sql) is not SqlStatementPlan plan)
        {
            return Plan(command.Sql) is SqlError error ? error : SqlErrors.Error("the statement cannot run");
        }

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
                return new SqlError("SQLITE_READONLY", "a READ ONLY transaction cannot write");
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
                return await SavepointAsync(plan, admit, cancellationToken).ConfigureAwait(false);
            case var _ when _inTransaction:
                return await InTransactionAsync(plan, command, admit, cancellationToken).ConfigureAwait(false);
            default:
                return await AutocommitAsync(plan, command, admit, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<SqlOutcome> AutocommitAsync(SqlStatementPlan plan, SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
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
                var tally = new Tally();
                var (outcome, _) = await RunAsync(transaction, plan, command, tally, admit, cancellationToken).ConfigureAwait(false);
                if (outcome is not SqlResult result)
                {
                    return outcome;
                }

                if (write && await CommitAsync(transaction, lease, tally, cancellationToken).ConfigureAwait(false) is { } refused)
                {
                    return refused;
                }

                return Charged(plan, result);
            }
        }
    }

    private async ValueTask<SqlOutcome> InTransactionAsync(SqlStatementPlan plan, SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
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

        var (outcome, ended) = await RunAsync(pinned.Transaction, plan, command, pinned.Tally, admit, cancellationToken).ConfigureAwait(false);
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
            return await ControlAsync(pinned, $"ROLLBACK TO {StatementSavepoint}", cancellationToken).ConfigureAwait(false)
                ?? await ControlAsync(pinned, $"RELEASE {StatementSavepoint}", cancellationToken).ConfigureAwait(false)
                ?? outcome;
        }

        return await ControlAsync(pinned, $"RELEASE {StatementSavepoint}", cancellationToken).ConfigureAwait(false) is { } unreleased
            ? unreleased
            : Charged(plan, result);
    }

    private async ValueTask<SqlOutcome> SavepointAsync(SqlStatementPlan plan, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
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

        return await ControlAsync(pinned, plan.Sql, cancellationToken).ConfigureAwait(false) is { } error ? error : Empty;
    }

    private async ValueTask<SqlError?> ControlAsync(Pinned pinned, string sql, CancellationToken cancellationToken)
    {
        try
        {
            _ = await pinned.Transaction.ExecuteStatementAsync(Internal(sql), cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<(SqlOutcome Outcome, bool Ended)> RunAsync(
        IStorageTransaction transaction, SqlStatementPlan plan, SqlCommand command, Tally tally, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
    {
        if (RequestExpired)
        {
            return (SqlErrors.Interrupt, false);
        }

        if (plan.Kind == SqlStatementKind.Write && plan.Names.Count > 0
            && (await CollectionsAsync(transaction, cancellationToken).ConfigureAwait(false)) is var collections && plan.Names.Any(collections.Contains))
        {
            return (SqlErrors.Error("SQL statements cannot change a document collection's table; use the documents API"), false);
        }

        SqlStatementResult raw;
        Volatile.Write(ref _running, transaction);
        try
        {
            raw = await transaction.ExecuteStatementAsync(
                new SqlStatement(plan.Sql, [.. command.Args.Select(Bound)], command.Named.ToDictionary(pair => pair.Key, pair => Bound(pair.Value), StringComparer.Ordinal),
                    MaxRows, MaxBytes, StatementTimeout()),
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

        if (raw.Truncated)
        {
            return (TooManyRows, false);
        }

        if (tally.Rows + raw.RowsChanged > MaxChangedRows || tally.Growth + raw.GrowthBytes > MaxGrowthBytes)
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
            var shape = await transaction.DescribeStatementAsync(plan.Sql, cancellationToken).ConfigureAwait(false);
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
            if (!pinned.Write || !pinned.Tally.Wrote)
            {
                return Empty;
            }

            StorageQuotaLease? lease = null;
            if (pinned.Tally.Growth > 0)
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(SqlLimits.MaxTransactionTime);
                try
                {
                    switch (await quota.EnterAsync(database, pinned.Store, bounded.Token).ConfigureAwait(false))
                    {
                        case StorageQuotaLease admitted:
                            lease = admitted;
                            break;
                        case Unavailable:
                            return SqlErrors.Busy("storage accounting is unavailable; retry");
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return SqlErrors.Busy("storage accounting is busy; retry");
                }
            }

            using (lease)
            {
                return await CommitAsync(pinned.Transaction, lease, pinned.Tally, cancellationToken).ConfigureAwait(false) is { } failed ? failed : Empty;
            }
        }
        finally
        {
            await DropAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<SqlError?> CommitAsync(IStorageTransaction transaction, StorageQuotaLease? lease, Tally tally, CancellationToken cancellationToken)
    {
        try
        {
            if (lease is not null)
            {
                switch (await lease.CheckAsync(tally.Growth, cancellationToken).ConfigureAwait(false))
                {
                    case QuotaExceeded:
                        return new SqlError("SQLITE_FULL", "the organization's storage quota is exhausted");
                    case Unavailable:
                        return SqlErrors.Busy("storage accounting is unavailable; retry");
                }
            }

            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            lease?.Committed(Math.Max(0, tally.Growth));
            if (tally.Rows > 0)
            {
                usage.RecordWrites(database, (int)tally.Rows, tally.Rows);
                foreach (var (kind, count) in tally.Operations)
                {
                    usage.RecordOperation(database, kind, count);
                }
            }

            return null;
        }
        catch (Exception error) when (Failure(error) is { } failure)
        {
            return failure;
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
            return (null, UnknownDatabase);
        }

        IStorageTransaction transaction;
        try
        {
            transaction = await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageBusyException)
        {
            return (null, SqlErrors.Busy("the database is busy; retry"));
        }

        var generation = ++_generation;
        var timer = transactionTime.CreateTimer(_ => _ = ExpireLaterAsync(generation), null, SqlLimits.MaxTransactionTime, Timeout.InfiniteTimeSpan);
        _pinned = new Pinned(store, transaction, write, transactionTime.GetTimestamp(), timer, new Tally());
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
        await pinned.Transaction.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<(IStorageTransaction? Transaction, StorageQuotaLease? Lease, SqlError? Error)> OpenAsync(
        bool write, CancellationToken cancellationToken, bool enterQuota = true)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return (null, null, UnknownDatabase);
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

        var units = result.Rows.Sum(row => Math.Max(1, OperationUnits.Of(row.Sum(value => value.Kind switch
        {
            SqlValueKind.Text => (long)Encoding.UTF8.GetByteCount(value.Text!),
            SqlValueKind.Blob => value.Blob!.LongLength,
            SqlValueKind.Null => 0L,
            _ => 8L,
        }))));
        return result with { ReadCharge = new SqlReadCharge(() => usage.RecordRead(database, units)) };
    }

    private SqlError Map(SqlStatementException error) => error.Failure switch
    {
        SqlFailure.PrimaryKey => SqlErrors.Constraint("PRIMARYKEY", error.Message),
        SqlFailure.Unique => SqlErrors.Constraint("UNIQUE", error.Message),
        SqlFailure.NotNull => SqlErrors.Constraint("NOTNULL", error.Message),
        SqlFailure.Check => SqlErrors.Constraint("CHECK", error.Message),
        SqlFailure.ForeignKey => SqlErrors.Constraint("FOREIGNKEY", error.Message),
        SqlFailure.Constraint => new SqlError("SQLITE_CONSTRAINT", error.Message),
        SqlFailure.Busy => SqlErrors.Busy(error.Message),
        SqlFailure.TooBig => SqlErrors.TooBig(error.Message),
        SqlFailure.Interrupted => SqlErrors.Interrupt,
        SqlFailure.ReadOnly => canWrite ? new SqlError("SQLITE_READONLY", error.Message) : SqlErrors.Auth,
        _ => SqlErrors.Error(error.Message),
    };

    private static SqlError? Failure(Exception error) => error switch
    {
        StorageBusyException => SqlErrors.Busy("the database is busy; retry"),
        QueryCapacityException => SqlErrors.Interrupt,
        _ => null,
    };

    private static object? Bound(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Integer => value.Integer,
        SqlValueKind.Real => value.Real,
        SqlValueKind.Text => value.Text,
        SqlValueKind.Blob => value.Blob,
        _ => null,
    };

    private static SqlValue Value(object? value) => value switch
    {
        long integer => SqlValue.Of(integer),
        double real => SqlValue.Of(real),
        string text => SqlValue.Of(text),
        byte[] blob => SqlValue.Of(blob),
        _ => SqlValue.Null,
    };

    private sealed record Pinned(IDatabaseStore Store, IStorageTransaction Transaction, bool Write, long Opened, ITimer Timer, Tally Tally);

    private sealed class Tally
    {
        public long Rows { get; private set; }

        public long Growth { get; private set; }

        public bool Wrote { get; private set; }

        public Dictionary<string, int> Operations { get; } = [with(StringComparer.Ordinal)];

        public void Add(SqlStatementPlan plan, SqlStatementResult result)
        {
            Rows += result.RowsChanged;
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
