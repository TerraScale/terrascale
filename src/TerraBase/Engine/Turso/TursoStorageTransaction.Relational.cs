using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Turso.Data.Sqlite;
using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;
using Turso.Raw.Public.Value;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    private long _pageSize;
    private bool _sqlChanged;

    ValueTask<SqlStatementResult> IStorageTransaction.ExecuteStatementAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return RunAsync(() => WithQueryBudget(() => RunStatement(statement, cancellationToken), cancellationToken), cancellationToken);
    }

    ValueTask<SqlStatementShape> IStorageTransaction.DescribeStatementAsync(string sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return RunAsync(() => DescribeStatement(sql), cancellationToken);
    }

    private readonly Lock _interruptGate = new();
    private TursoDatabaseHandle? _interruptible;

    void IStorageTransaction.InterruptStatement() => InterruptCustomerStatement();

    private void InterruptCustomerStatement()
    {
        lock (_interruptGate)
        {
            if (_interruptible is not { } native)
            {
                return;
            }

            try
            {
                TursoBindings.Interrupt(native);
            }
            catch (TursoException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    ValueTask<long> IStorageTransaction.MeasureSqlStorageAsync(CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var native = session.Native;
            try
            {
                return (Counter(native, "PRAGMA page_count;") - Counter(native, "PRAGMA freelist_count;")) * PageSize(native);
            }
            catch (TursoException error)
            {
                throw Failure(native, error);
            }
        }, cancellationToken);

    async ValueTask IStorageTransaction.AddSqlStorageAsync(long delta, CancellationToken cancellationToken) =>
        _ = await RunAsync(() =>
        {
            if (!write || store.IsSystem)
            {
                throw new InvalidOperationException("SQL storage belongs to a client database's write transaction");
            }

            var command = Command(TursoStatements.AddSqlStorage);
            command.Parameters.AddWithValue("$bytes", delta);
            _ = command.ExecuteNonQuery();
            _sqlChanged = true;
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);

    private SqlStatementResult RunStatement(SqlStatement statement, CancellationToken cancellationToken)
    {
        var native = session.Native;
        var changed = write;
        _queryBudget!.Charge(statement.Sql);
        CountRead("query");
        var started = Stopwatch.GetTimestamp();
        try
        {
            var pages = write ? Counter(native, "PRAGMA page_count;") : 0L;
            var changes = write ? Counter(native, "SELECT total_changes();") : 0L;
            var schema = write ? Counter(native, "PRAGMA schema_version;") : 0L;
            var (prepared, consumed) = Prepare(native, statement.Sql, statement.First);
            (List<SqlStatementColumn> Columns, List<object?[]> Rows, long Affected, bool Truncated) stepped;
            TursoBindings.SetQueryTimeout(native, statement.Timeout);
            TursoBindings.SetMaxValueLength(native, statement.MaxBytes);
            try
            {
                using (cancellationToken.Register(static state => ((TursoStorageTransaction)state!).InterruptCustomerStatement(), this))
                {
                    store.ThrowIfUnavailable();
                    stepped = Step(prepared, statement, cancellationToken);
                    store.ThrowIfUnavailable();
                }
            }
            finally
            {
                TursoBindings.SetQueryTimeout(native, TimeSpan.Zero);
                TursoBindings.SetMaxValueLength(native, long.MaxValue);
            }

            var rowid = Counter(native, "SELECT last_insert_rowid();");
            var grown = write ? Counter(native, "PRAGMA page_count;") - pages : 0L;
            changed = write && (grown != 0 || Counter(native, "SELECT total_changes();") != changes || Counter(native, "PRAGMA schema_version;") != schema);
            return new SqlStatementResult(stepped.Columns, stepped.Rows, stepped.Affected, rowid, grown * PageSize(native), stepped.Truncated, consumed,
                Stopwatch.GetElapsedTime(started));
        }
        catch (TursoException error)
        {
            throw Failure(native, error);
        }
        finally
        {
            _sqlChanged |= changed;
            if (!store.IsSystem)
            {
                QosContext.ChargeEngine(Stopwatch.GetElapsedTime(started));
            }
        }
    }

    private void OpenInterruptWindow(TursoDatabaseHandle native)
    {
        lock (_interruptGate)
        {
            _interruptible = native;
        }
    }

    private void CloseInterruptWindow(TursoDatabaseHandle native)
    {
        lock (_interruptGate)
        {
            _interruptible = null;
        }

        TursoBindings.ClearInterrupt(native);
    }

    internal static (TursoStatementHandle Prepared, int Consumed) Prepare(TursoDatabaseHandle native, string sql, bool first)
    {
        var prepared = TursoBindings.PrepareFirst(native, sql, out var tail)
            ?? throw new SqlStatementException(new Error(), "the statement is empty", transactionEnded: false);
        var consumed = sql.Length - tail;
        if (!first && !(SqlText.TryTokenize(sql[consumed..], out var rest) && rest.TrueForAll(token => token.Kind == SqlTokenKind.Semicolon)))
        {
            Release(prepared);
            throw new SqlStatementException(new Error(), "an engine call runs exactly one SQL statement", transactionEnded: false);
        }

        return (prepared, consumed);
    }

    private (List<SqlStatementColumn> Columns, List<object?[]> Rows, long Affected, bool Truncated) Step(
        TursoStatementHandle prepared, SqlStatement statement, CancellationToken cancellationToken)
    {
        try
        {
            Bind(prepared, statement);
            var columns = Columns(prepared);
            var rows = new List<object?[]>();
            long bytes = 0;
            long reserved = 0;
            while (TursoBindings.Read(prepared))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_queryBudget!.Exceeded)
                {
                    throw new QueryCapacityException("query execution budget exceeded");
                }

                long size = 0;
                for (var i = 0; i < columns.Count; i++)
                {
                    size += ValueSize(prepared, i);
                }

                if (rows.Count == statement.MaxRows || size > statement.MaxBytes - bytes)
                {
                    return (columns, rows, TursoBindings.RowsAffected(prepared), true);
                }

                var held = checked((2 * size) + (16L * columns.Count) + 64);
                if (held > reserved)
                {
                    var block = store.IsSystem ? held : Math.Max(held, 256 * 1024);
                    QosContext.ReserveResult(block, store.IsSystem ? null : QosContext.Current);
                    reserved += block;
                }

                reserved -= held;
                bytes += size;
                var row = new object?[columns.Count];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = Value(prepared, i);
                }

                rows.Add(row);
            }

            return (columns, rows, TursoBindings.RowsAffected(prepared), false);
        }
        finally
        {
            Release(prepared);
        }
    }

    private SqlStatementShape DescribeStatement(string sql)
    {
        var native = session.Native;
        try
        {
            var (prepared, _) = Prepare(native, sql, first: false);
            try
            {
                var parameters = new string?[TursoBindings.GetParameterCount(prepared)];
                for (var i = 0; i < parameters.Length; i++)
                {
                    parameters[i] = TursoBindings.GetParameterName(prepared, i + 1);
                }

                return new SqlStatementShape(parameters, Columns(prepared));
            }
            finally
            {
                Release(prepared);
            }
        }
        catch (TursoException error)
        {
            throw Failure(native, error);
        }
    }

    internal static void Bind(TursoStatementHandle prepared, SqlStatement statement)
    {
        var count = TursoBindings.GetParameterCount(prepared);
        for (var i = 1; i <= count; i++)
        {
            var value = TursoBindings.GetParameterName(prepared, i) switch
            {
                null or ['?', ..] => i <= statement.Args.Count ? statement.Args[i - 1] : null,
                var name => statement.Named.TryGetValue(name, out var exact) ? exact : statement.Named.GetValueOrDefault(name[1..]),
            };
            TursoBindings.BindParameter(prepared, i, value switch
            {
                null => TursoValue.Null(),
                long integer => TursoValue.Int(integer),
                double real => TursoValue.Real(real),
                string text => TursoValue.String(text),
                byte[] blob => TursoValue.Blob(blob),
                _ => throw new ArgumentException("arguments are null, long, double, string or byte[]", nameof(statement)),
            });
        }
    }

    internal static List<SqlStatementColumn> Columns(TursoStatementHandle prepared)
    {
        var count = TursoBindings.GetFieldCount(prepared);
        var columns = new List<SqlStatementColumn>(count);
        for (var i = 0; i < count; i++)
        {
            var declared = TursoBindings.GetDeclaredTypeName(prepared, i);
            columns.Add(new SqlStatementColumn(TursoBindings.GetName(prepared, i), declared.Length == 0 ? null : declared));
        }

        return columns;
    }

    internal static long ValueSize(TursoStatementHandle prepared, int column) =>
        TursoBindings.GetValueType(prepared, column) switch
        {
            TursoValueType.Text or TursoValueType.Blob => Math.Max(0, TursoBindings.GetValueByteCount(prepared, column)),
            TursoValueType.Null or TursoValueType.Empty => 0,
            _ => 8,
        };

    internal static object? Value(TursoStatementHandle prepared, int column) =>
        TursoBindings.GetValue(prepared, column) switch
        {
            { ValueType: TursoValueType.Integer } value => value.IntValue,
            { ValueType: TursoValueType.Real } value => value.RealValue,
            { ValueType: TursoValueType.Text } value => value.StringValue,
            { ValueType: TursoValueType.Blob } value => value.BlobValue,
            _ => null,
        };

    internal static long Counter(TursoDatabaseHandle native, string sql)
    {
        var prepared = TursoBindings.PrepareStatement(native, sql);
        try
        {
            return TursoBindings.Read(prepared) ? TursoBindings.GetValue(prepared, 0).IntValue : 0;
        }
        finally
        {
            Release(prepared);
        }
    }

    private long PageSize(TursoDatabaseHandle native) => _pageSize > 0 ? _pageSize : _pageSize = Counter(native, "PRAGMA page_size;");

    internal static void Release(TursoStatementHandle prepared)
    {
        try
        {
            TursoBindings.ResetStatement(prepared);
        }
        catch (TursoException)
        {
        }
        finally
        {
            prepared.Dispose();
        }
    }

    private SqlStatementException Failure(TursoDatabaseHandle native, TursoException error)
    {
        var damaged = error is TursoNativeException { StatusCode: 132 or 133 or 134 };
        if (damaged)
        {
            NoteCopyFault("the file or its log reported an I/O error or corruption");
        }

        var ended = EndedByFailure(native);
        if (damaged)
        {
            return new SqlStatementException(new StorageFailure(), "the database reported an I/O error or corruption", ended, error);
        }

        var (failure, reported) = Classify(native, error);
        return new SqlStatementException(failure, reported, ended, error);
    }

    internal static (SqlFailure Failure, string Message) Classify(TursoDatabaseHandle native, TursoException error)
    {
        var (message, parse, code) = Unwrap(error);
        var (failure, reported) = SqlErrors.Classify(message, parse, error is TursoNativeException { StatusCode: 5 }, code);
        return failure is Unique && IsPrimaryKey(native, reported) ? (new PrimaryKey(), reported) : (failure, reported);
    }

    private static (string Message, bool Parse, int Code) Unwrap(Exception error)
    {
        var message = error.Message;
        var parse = false;
        foreach (var prefix in (ReadOnlySpan<string>)["Unable to prepare statement: ", "Parse error: ", "Extension error: "])
        {
            if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                parse |= prefix != "Extension error: ";
                message = message[prefix.Length..];
            }
        }

        if (SqliteCoded().Match(message) is { Success: true } coded)
        {
            return (coded.Groups[2].Value, parse, int.Parse(coded.Groups[1].ValueSpan, CultureInfo.InvariantCulture));
        }

        return (message, parse, SqliteCode(error));
    }

    private static int SqliteCode(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite)
            {
                return sqlite.SqliteExtendedErrorCode == 0 ? sqlite.SqliteErrorCode : sqlite.SqliteExtendedErrorCode;
            }
        }

        return 0;
    }

    [GeneratedRegex(@"\A__turso_sqlite_error__:(\d+):(.*)\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SqliteCoded();

    private bool EndedByFailure(TursoDatabaseHandle native)
    {
        try
        {
            using var begin = TursoBindings.PrepareStatement(native, "BEGIN;");
            while (TursoBindings.Read(begin))
            {
            }
        }
        catch (TursoException)
        {
            return false;
        }

        Aborted = _faulted = true;
        return true;
    }

    private static bool IsPrimaryKey(TursoDatabaseHandle native, string message)
    {
        var named = SqlErrors.ConstraintColumns(message);
        if (named.Count == 0 || named.Select(column => column.Table).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is not [var table])
        {
            return false;
        }

        try
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prepared = TursoBindings.PrepareStatement(native, $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");");
            try
            {
                while (TursoBindings.Read(prepared))
                {
                    if (TursoBindings.GetValue(prepared, 5).IntValue > 0)
                    {
                        keys.Add(TursoBindings.GetValue(prepared, 1).StringValue);
                    }
                }
            }
            finally
            {
                Release(prepared);
            }

            return keys.SetEquals(named.Select(column => column.Name));
        }
        catch (TursoException)
        {
            return false;
        }
    }

}
