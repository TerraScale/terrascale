using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlParser;
using SqlParser.Tokens;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Stats;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

/// <summary>Relational SQL over a database's collections (docs/terrabase/sql/relational-sql.md).</summary>
internal sealed class SqlService(
    IStorageEngine engine, IDocumentService documents, IStorageQuota quota, IUsageMeter usage, IDatabaseStats stats, IMemoryBudget memory, TimeProvider time) : ISqlService
{
    private readonly SqlReadCache _reads = new();

    public ISqlSession Open(TenantContext database, bool canWrite)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new SqlSession(engine, quota, usage, stats, memory, new SqlExecutor(documents, memory, time, _reads, database.DatabaseId), time, database, canWrite, _reads);
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
            var tables = new List<SqlTableInfo>();
            string? after = null;
            for (var scanned = 0; scanned < MaxTables;)
            {
                var (collections, hasMore) = await transaction.ListCollectionsAsync(after, 200, cancellationToken).ConfigureAwait(false);
                foreach (var collection in collections)
                {
                    if (await SqlTable.ReadAsync(transaction, collection.Name, cancellationToken).ConfigureAwait(false) is { } table)
                    {
                        tables.Add(new SqlTableInfo(collection.Name, [.. table.Columns.Select(column => new SqlTableColumn(
                            column.Name, column.Type.Length == 0 ? null : column.Type, column.NotNull, table.PrimaryKey.Contains(column.Name),
                            column.Default switch
                            {
                                { ValueKind: JsonValueKind.String } text => "'" + text.GetString()!.Replace("'", "''", StringComparison.Ordinal) + "'",
                                { } number => number.GetRawText(),
                                null => null,
                            }))]));
                    }
                }

                scanned += collections.Count;
                if (!hasMore || collections.Count == 0)
                {
                    break;
                }

                after = collections[^1].Name;
            }

            return tables;
        }
    }

    private const int MaxTables = 10_000;
}

/// <summary>
/// One connection (section 5 of the design): autocommit statements run alone; a transaction keeps a log of its statements
/// and result hashes and never holds the database writer between requests. Each request that continues it replays the log
/// in a fresh storage transaction and checks every hash: a mismatch is a conflict (<c>SQLITE_BUSY</c>), and nothing commits.
/// Usage (docs/terrabase/operation-allowance.md): a table read bills its returned rows when it answers, never again on
/// replay; the rows a transaction writes bill once it commits.
/// </summary>
internal sealed class SqlSession(
    IStorageEngine engine, IStorageQuota quota, IUsageMeter usage, IDatabaseStats stats, IMemoryBudget memory, SqlExecutor executor, TimeProvider time, TenantContext database,
    bool canWrite, SqlReadCache reads)
    : ISqlSession
{
    private const int MaxStatements = 256;
    private const long MaxLogBytes = 1024 * 1024;
    private const int MaxSavepoints = 128, MaxSavepointBytes = 24 * 1024;

    private static readonly SqlResult Empty = new([], [], 0, null);
    private static readonly SqlError UnknownDatabase = SqlErrors.Error("the database does not exist");
    private static readonly SqlError Conflict = SqlErrors.Busy("transaction conflict: data changed since the transaction read it; retry the transaction");

    private readonly List<(SqlCommand Command, byte[] Hash)> _log = [];
    private readonly List<Savepoint> _savepoints = [];
    private long _savepointBytes;
    private long _logBytes;
    private long? _scriptStarted;
    private long? _transportStarted;
    private IMemoryReservation? _logMemory;
    private SqlKeys _keys = new();
    private bool? _readOnly;
    private Request? _request;
    private string? _parsedSql;
    private SqlParseResult _parsed;

    public bool IsAutocommit => _readOnly is null;

    public async ValueTask<SqlOutcome> SavepointAsync(SqlSavepointKind kind, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsAutocommit)
        {
            return SqlErrors.Error("savepoints require an explicit transaction");
        }

        if (kind == SqlSavepointKind.Create)
        {
            var bytes = 128L + (2L * name.Length);
            if (_savepoints.Count == MaxSavepoints || _savepointBytes + bytes > MaxSavepointBytes)
            {
                return SqlErrors.TooBig("a transaction holds at most 128 savepoints and 24 KiB of savepoint state");
            }

            var reservation = memory.TryReserve(MemoryLane.Foreground, bytes, database);
            if (reservation is null)
            {
                return new SqlError("SQLITE_NOMEM", "the server's memory budget is exhausted; retry");
            }

            _savepoints.Add(new Savepoint(name, _log.Count, _logBytes, _keys.Count, reservation));
            _savepointBytes += bytes;
            return Empty;
        }

        var index = _savepoints.FindLastIndex(saved => string.Equals(saved.Name, name, StringComparison.Ordinal));
        if (index < 0)
        {
            return SqlErrors.Error("savepoint does not exist");
        }

        if (kind == SqlSavepointKind.RollbackTo)
        {
            // Drop the physical writes and their tally, then replay only the surviving read/write log on next use.
            await EndRequestAsync().ConfigureAwait(false);
            var saved = _savepoints[index];
            _log.RemoveRange(saved.Statements, _log.Count - saved.Statements);
            _logBytes = saved.LogBytes;
            _keys.Truncate(saved.Keys);
            index++; // ROLLBACK TO keeps its target; RELEASE removes it too.
        }

        RemoveSavepoints(index);
        return Empty;
    }

    private void RemoveSavepoints(int start)
    {
        for (var i = _savepoints.Count - 1; i >= start; i--)
        {
            _savepointBytes -= _savepoints[i].Memory.Bytes;
            _savepoints[i].Memory.Dispose();
            _savepoints.RemoveAt(i);
        }
    }

    private sealed record Savepoint(string Name, int Statements, long LogBytes, int Keys, IMemoryReservation Memory);

    public void BeginRequest() => _transportStarted = time.GetTimestamp();

    public ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken) =>
        ExecuteAsync(command, admit: null, cancellationToken);

    public async ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (Parse(command.Sql))
        {
            case SqlError error:
                return error;
            // Controls answer nothing: admitted before they act, so a refused COMMIT commits nothing.
            case PragmaSql { Kind: PragmaKind.ForeignKeys } or TransactionSql when admit?.Invoke(Empty) is { } refused:
                return refused;
            case PragmaSql { Kind: PragmaKind.ForeignKeys }:
                return Empty;
            case TransactionSql { Kind: TransactionKind.Begin or TransactionKind.BeginReadOnly } begin:
                if (_readOnly is not null)
                {
                    return SqlErrors.Error("cannot start a transaction within a transaction");
                }

                _readOnly = begin.Kind == TransactionKind.BeginReadOnly;
                _keys = new SqlKeys();
                return Empty;
            case TransactionSql { Kind: TransactionKind.Commit }:
                return _readOnly is null ? SqlErrors.Error("cannot commit - no transaction is active") : await CommitAsync(cancellationToken).ConfigureAwait(false);
            case TransactionSql:
                await AbortAsync().ConfigureAwait(false);
                return Empty;
            case ParsedSql { IsWrite: true } when !canWrite:
                return SqlErrors.Auth;
            case ParsedSql { IsWrite: true } when _readOnly == true:
                return new SqlError("SQLITE_READONLY", "a READONLY transaction cannot write");
            case ParsedSql statement when _readOnly is null:
                return await AutocommitAsync(statement, command, admit, describe: false, cancellationToken).ConfigureAwait(false);
            case ParsedSql statement:
                return await InTransactionAsync(statement, command, admit, cancellationToken).ConfigureAwait(false);
            default:
                return SqlErrors.Error("this statement is not supported");
        }
    }

    public ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, CancellationToken cancellationToken) =>
        ExecuteScriptAsync(sql, MaxStatements, cancellationToken);

    public async ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, int maxStatements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        SqlParser.Sequence<SqlParser.Ast.Statement> statements;
        try
        {
            // The whole text passes the literal and name checks before it is split.
            _ = SqlRead.Number(sql);
            var parser = new Parser();
            statements = parser.ParseSql(sql, new SqlDialect());
            // Reject an unconsumed tail before any statement in the script executes.
            if (parser.PeekToken() is not EOF)
            {
                throw Parser.Expected("end of input", parser.PeekToken());
            }
        }
        catch (FormatException error)
        {
            return SqlErrors.Error(error.Message);
        }
        catch (Exception error) when (error is ParserException or TokenizeException)
        {
            return SqlErrors.Error("SQL syntax error in the script");
        }

        if (statements.Count > maxStatements)
        {
            return SqlErrors.TooBig(string.Create(CultureInfo.InvariantCulture, $"a script holds at most {maxStatements} statements"));
        }

        SqlOutcome last = Empty;
        _scriptStarted = time.GetTimestamp();
        try
        {
            foreach (var statement in statements)
            {
                // Transaction controls bypass RunAsync; an expired script must not commit its earlier writes.
                if (time.GetElapsedTime(Math.Min(_scriptStarted.Value, _transportStarted ?? long.MaxValue)) > SqlLimits.MaxRequestTime)
                {
                    return SqlErrors.Interrupt;
                }

                last = await ExecuteAsync(new SqlCommand(statement.ToSql(), [], new Dictionary<string, SqlValue>()), cancellationToken).ConfigureAwait(false);
                if (last is SqlError)
                {
                    break;
                }

                // Sequence retains its existing executed-read policy; its result has no protocol row projection.
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
        return Parse(command.Sql) switch
        {
            SqlError error => error,
            // Columns come from the statement run with no rows: names and declared types exactly as execution reports them.
            ReadSql read => await DescribeResultAsync(read, command, cancellationToken)
                .ConfigureAwait(false) switch
            {
                SqlResult result => new SqlDescription(read.Params, result.Columns, IsExplain: false, IsReadOnly: true),
                SqlError error => error,
            },
            // A write's RETURNING columns come from the table definition, read without writing.
            ParsedSql statement and (InsertSql { Returning: not null } or ChangeSql { Returning: not null } or PragmaSql { Kind: not PragmaKind.ForeignKeys }) =>
                await DescribeResultAsync(statement, command, cancellationToken)
                    .ConfigureAwait(false) switch
                {
                    SqlResult result => new SqlDescription(statement.Params, result.Columns, IsExplain: false, IsReadOnly: !statement.IsWrite),
                    SqlError error => error,
                },
            ParsedSql statement => new SqlDescription(statement.Params, [], IsExplain: false, IsReadOnly: !statement.IsWrite),
        };
    }

    // Describe sees uncommitted DDL after replay, without logging or billing a user statement, or committing writes.
    private async ValueTask<SqlOutcome> DescribeResultAsync(ParsedSql statement, SqlCommand command, CancellationToken cancellationToken)
    {
        command = command with { Args = [], Named = new Dictionary<string, SqlValue>() };
        if (IsAutocommit)
        {
            return await AutocommitAsync(statement, command, admit: null, describe: true, cancellationToken).ConfigureAwait(false);
        }

        if (await ResumeAsync(cancellationToken).ConfigureAwait(false) is SqlError refused)
        {
            return refused;
        }

        var outcome = await RunAsync(_request!, statement, command, new SqlKeys(), cancellationToken, describe: true).ConfigureAwait(false);
        if (outcome is SqlError)
        {
            await EndRequestAsync().ConfigureAwait(false);
        }

        return outcome;
    }

    public async ValueTask EndRequestAsync()
    {
        if (_request is { } request)
        {
            _request = null;
            await request.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => AbortAsync();

    // Batches repeat one statement with new arguments: its last parse is reused, and a read's from the database's cache.
    private SqlParseResult Parse(string sql)
    {
        if (!string.Equals(sql, _parsedSql, StringComparison.Ordinal))
        {
            _parsed = reads.Parse(database.DatabaseId, sql);
            _parsedSql = sql;
        }

        return _parsed;
    }

    // A statement of the open transaction: run in this request's storage transaction, which first replays the log.
    private async ValueTask<SqlOutcome> InTransactionAsync(ParsedSql statement, SqlCommand command, Func<SqlResult, SqlError?>? admit, CancellationToken cancellationToken)
    {
        if (await ResumeAsync(cancellationToken).ConfigureAwait(false) is SqlError refused)
        {
            return refused;
        }

        // A failed statement undoes only itself, as in SQLite: this request's storage transaction (which a refused write
        // may leave uncommittable) is dropped, and the next statement replays the log without it.
        var keys = _keys.Count;
        var outcome = await RunAsync(_request!, statement, command, _keys, cancellationToken).ConfigureAwait(false);
        if (outcome is SqlResult answered && admit?.Invoke(answered) is { } inadmissible)
        {
            outcome = inadmissible;
        }

        if (outcome is not SqlResult result)
        {
            await EndRequestAsync().ConfigureAwait(false);
            _keys.Truncate(keys);
            return outcome;
        }

        var size = Size(command);
        if (_log.Count == MaxStatements || _logBytes + size > MaxLogBytes)
        {
            await EndRequestAsync().ConfigureAwait(false);
            _keys.Truncate(keys);
            return SqlErrors.TooBig("a transaction holds at most 256 statements and 1 MiB of statements");
        }

        // One reservation for the whole log, replaced by a larger one as it grows (never one per statement).
        if (_logBytes + size > (_logMemory?.Bytes ?? 0))
        {
            var grown = memory.TryReserve(MemoryLane.Foreground, Math.Min(MaxLogBytes, Math.Max(_logBytes + size, 2 * (_logMemory?.Bytes ?? 32 * 1024))), database);
            if (grown is null)
            {
                await EndRequestAsync().ConfigureAwait(false);
                _keys.Truncate(keys);
                return new SqlError("SQLITE_NOMEM", "the server's memory budget is exhausted; retry");
            }

            _logMemory?.Dispose();
            _logMemory = grown;
        }

        _log.Add((command, Hash(result)));
        _logBytes += size;
        return StageRead(statement, result);
    }

    // This request's storage transaction, opened and brought up to date by replaying the log; a replayed statement that
    // fails or answers differently ends the transaction as a conflict.
    private async ValueTask<SqlError?> ResumeAsync(CancellationToken cancellationToken)
    {
        if (_request is not null)
        {
            return null;
        }

        var (opened, error) = await OpenAsync(write: _readOnly != true, cancellationToken).ConfigureAwait(false);
        if (opened is not { } request)
        {
            await EndRequestAsync().ConfigureAwait(false);
            return error;
        }

        _request = request;
        _keys.Rewind();
        foreach (var (command, hash) in _log)
        {
            var replayed = Parse(command.Sql) is ParsedSql statement
                ? await RunAsync(request, statement, command, _keys, cancellationToken).ConfigureAwait(false)
                : SqlErrors.Error("the statement no longer parses");
            if (replayed is not SqlResult result || !Hash(result).AsSpan().SequenceEqual(hash))
            {
                // A statement failure preserves markers so ROLLBACK TO can discard conflicting reads.
                await EndRequestAsync().ConfigureAwait(false);
                return replayed is SqlError { Code: "SQLITE_BUSY" } busy ? busy : Conflict;
            }
        }

        return null;
    }

    private async ValueTask<SqlOutcome> CommitAsync(CancellationToken cancellationToken)
    {
        if (await ResumeAsync(cancellationToken).ConfigureAwait(false) is SqlError refused)
        {
            await AbortAsync().ConfigureAwait(false);
            return refused;
        }

        var request = _request!;
        var outcome = request.Lease is { } lease
            ? await CommitAsync(request, lease, cancellationToken).ConfigureAwait(false)
            : Empty;
        await AbortAsync().ConfigureAwait(false);
        return outcome;
    }

    private async ValueTask AbortAsync()
    {
        await EndRequestAsync().ConfigureAwait(false);
        _readOnly = null;
        _log.Clear();
        RemoveSavepoints(0);
        _logBytes = 0;
        _logMemory?.Dispose();
        _logMemory = null;
        _keys = new SqlKeys();
    }

    // One statement in its own storage transaction, committed when it writes.
    private async ValueTask<SqlOutcome> AutocommitAsync(
        ParsedSql statement, SqlCommand command, Func<SqlResult, SqlError?>? admit, bool describe, CancellationToken cancellationToken)
    {
        // A described statement only reads, even a write with RETURNING.
        var (opened, error) = await OpenAsync(statement.IsWrite && !describe, cancellationToken).ConfigureAwait(false);
        if (opened is not { } request)
        {
            return error!;
        }

        await using (request.ConfigureAwait(false))
        {
            // Autocommit keys are never replayed: a throwaway log.
            var outcome = await RunAsync(request, statement, command, new SqlKeys(), cancellationToken, describe).ConfigureAwait(false);
            // A refused result commits nothing: disposing the request rolls its writes back.
            if (outcome is SqlResult answered && admit?.Invoke(answered) is { } refused)
            {
                return refused;
            }

            if (outcome is SqlResult && request.Lease is { } lease && await CommitAsync(request, lease, cancellationToken).ConfigureAwait(false) is SqlError failed)
            {
                return failed;
            }

            return outcome is SqlResult result && !describe ? StageRead(statement, result) : outcome;
        }
    }

    // A storage transaction; a write one is admitted by the storage quota. An error when the database does not exist or
    // cannot be admitted now.
    private async ValueTask<(Request? Request, SqlError? Error)> OpenAsync(bool write, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return (null, UnknownDatabase);
        }

        StorageQuotaLease? lease = null;
        if (write)
        {
            switch (await quota.EnterAsync(database, store, cancellationToken).ConfigureAwait(false))
            {
                case StorageQuotaLease admitted:
                    lease = admitted;
                    break;
                case Unavailable:
                    return (null, SqlErrors.Busy("storage accounting is unavailable; retry"));
            }
        }

        try
        {
            var transaction = await store.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
            return (new Request(transaction, lease, new SqlTally(lease is { RequiresAccounting: true }), time.GetTimestamp()), null);
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
    }

    private async ValueTask<SqlOutcome> RunAsync(
        Request request, ParsedSql statement, SqlCommand command, SqlKeys keys, CancellationToken cancellationToken, bool describe = false)
    {
        // Each statement has its own work budget; a request's statements (and a replay) also share one deadline, so a
        // request that holds the writer cannot hold it for 256 budgets.
        if (time.GetElapsedTime(Math.Min(request.Started, Math.Min(_scriptStarted ?? long.MaxValue, _transportStarted ?? long.MaxValue))) > SqlLimits.MaxRequestTime)
        {
            return SqlErrors.Interrupt;
        }

        try
        {
            return await executor.ExecuteAsync(request.Transaction, statement, command, keys, request.Tally, describe, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Failure(error) is { } failure)
        {
            return failure;
        }
    }

    // Commits a write transaction under the storage quota, and meters it.
    private async ValueTask<SqlOutcome> CommitAsync(Request request, StorageQuotaLease lease, CancellationToken cancellationToken)
    {
        try
        {
            switch (await lease.CheckAsync(request.Tally.Delta, cancellationToken).ConfigureAwait(false))
            {
                case QuotaExceeded:
                    return new SqlError("SQLITE_FULL", "the organization's storage quota is exhausted");
                case Unavailable:
                    return SqlErrors.Busy("storage accounting is unavailable; retry");
            }

            await request.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            lease.Committed(request.Tally.Delta);
            if (request.Tally.Writes > 0)
            {
                usage.RecordWrites(database, request.Tally.Writes, request.Tally.WriteUnits);
                foreach (var ((collection, kind), count) in request.Tally.Operations)
                {
                    usage.RecordOperation(database, kind, count);
                    for (var i = 0; i < count; i++)
                    {
                        stats.CollectionWrote(database, collection);
                    }
                }
            }

            return Empty;
        }
        catch (Exception error) when (Failure(error) is { } failure)
        {
            return failure;
        }
    }

    // The SQL error an engine failure means, or null for an unexpected one.
    private static SqlError? Failure(Exception error) => error switch
    {
        StorageBusyException => SqlErrors.Busy("the database is busy; retry"),
        ChangeGroupCapacityException or StorageWriteLimitException => SqlErrors.TooBig("a transaction writes at most 1024 rows and 2 MiB"),
        QueryCapacityException => SqlErrors.Interrupt,
        SqlValueTooLargeException large => SqlErrors.TooBig(large.Message),
        DocumentValidationException invalid => invalid.TooLarge ? SqlErrors.TooBig(invalid.Message) : SqlErrors.Error("the row does not satisfy document validation"),
        SqlProgramException { Message: "syntax error" } => SqlErrors.Error("SQL syntax error"),
        SqlProgramException program => SqlErrors.Error(program.Message),
        _ => null,
    };

    // A read of tables stages each returned row's started 4 KiB, at least one: UTF-8 text, 8 bytes per number.
    // Catalog and constant reads bill nothing, and neither do the rows an UPDATE, DELETE or INSERT … SELECT reads.
    private SqlResult StageRead(ParsedSql statement, SqlResult result)
    {
        if (statement is ReadSql { Tables.Count: > 0 })
        {
            var units = result.Rows.Sum(row => Math.Max(1, OperationUnits.Of(row.Sum(value => value.Kind switch
            {
                SqlValueKind.Text => (long)Encoding.UTF8.GetByteCount(value.Text!),
                SqlValueKind.Null => 0L,
                _ => 8L,
            }))));
            return result with { ReadCharge = new SqlReadCharge(() => usage.RecordRead(database, units)) };
        }

        return result;
    }

    // Bytes a logged statement holds: its text and arguments, as UTF-16.
    private static long Size(SqlCommand command) =>
        64 + (2L * command.Sql.Length)
        + command.Args.Sum(arg => 16L + (2L * (arg.Text?.Length ?? 0)))
        + command.Named.Sum(pair => 16L + (2L * (pair.Key.Length + (pair.Value.Text?.Length ?? 0))));

    // A digest of everything a statement answered, so a replay can tell whether it would answer the same.
    private static byte[] Hash(SqlResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var column in result.Columns)
            {
                writer.WriteStringValue(column.Name);
                writer.WriteStringValue(column.DeclType);
            }

            foreach (var row in result.Rows)
            {
                writer.WriteStartArray();
                foreach (var value in row)
                {
                    writer.WriteNumberValue((int)value.Kind);
                    SqlWrite.Write(writer, value);
                }

                writer.WriteEndArray();
            }

            writer.WriteNumberValue(result.RowsAffected);
            if (result.LastInsertRowid is { } rowid)
            {
                writer.WriteNumberValue(rowid);
            }

            writer.WriteEndArray();
        }

        return SHA256.HashData(buffer.WrittenSpan);
    }

    /// <summary>A storage transaction of one request, its quota admission and the byte growth of its writes.</summary>
    private sealed class Request(IStorageTransaction transaction, StorageQuotaLease? lease, SqlTally tally, long started) : IAsyncDisposable
    {
        public IStorageTransaction Transaction { get; } = transaction;

        public StorageQuotaLease? Lease { get; } = lease;

        public SqlTally Tally { get; } = tally;

        /// <summary>When the storage transaction opened: one request's statements share one deadline.</summary>
        public long Started { get; } = started;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Lease?.Dispose();
            }
        }
    }
}
