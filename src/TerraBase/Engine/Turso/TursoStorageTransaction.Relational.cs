using System.Text;
using System.Text.RegularExpressions;
using TerraBase.Features.Qos;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;
using Turso.Raw.Public.Value;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    private static readonly string[] SqlPhrases =
    [
        "no such function", "ambiguous column", "misuse of aggregate", "misuse of window function", "circular reference",
        "wrong number of arguments", "datatype mismatch", "integer overflow", "not supported", "not allowed", "cannot",
        "malformed JSON", "must be",
    ];

    private long _pageSize;

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

    private SqlStatementResult RunStatement(SqlStatement statement, CancellationToken cancellationToken)
    {
        RequireOneStatement(statement.Sql);
        var native = session.Native;
        _ranPlain |= write;
        _queryBudget!.Charge(statement.Sql);
        CountRead("query");
        try
        {
            var (changes, pages) = write ? (Counter(native, "SELECT total_changes();"), Counter(native, "PRAGMA page_count;")) : (0L, 0L);
            var (columns, rows, affected, truncated) = Step(native, statement, cancellationToken);
            var rowid = Counter(native, "SELECT last_insert_rowid();");
            var (changed, grown) = write ? (Counter(native, "SELECT total_changes();") - changes, Counter(native, "PRAGMA page_count;") - pages) : (0L, 0L);
            return new SqlStatementResult(columns, rows, affected, changed, rowid, grown * PageSize(native), truncated);
        }
        catch (TursoException error)
        {
            throw Failure(native, error);
        }
    }

    private (List<SqlStatementColumn> Columns, List<object?[]> Rows, long Affected, bool Truncated) Step(
        TursoDatabaseHandle native, SqlStatement statement, CancellationToken cancellationToken)
    {
        var prepared = TursoBindings.PrepareStatement(native, statement.Sql);
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

                var row = new object?[columns.Count];
                long size = 0;
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = TursoBindings.GetValue(prepared, i) switch
                    {
                        { ValueType: TursoValueType.Integer } value => value.IntValue,
                        { ValueType: TursoValueType.Real } value => value.RealValue,
                        { ValueType: TursoValueType.Text } value => value.StringValue,
                        { ValueType: TursoValueType.Blob } value => value.BlobValue,
                        _ => null,
                    };
                    size += row[i] switch
                    {
                        string text => Encoding.UTF8.GetByteCount(text),
                        byte[] blob => blob.Length,
                        null => 0,
                        _ => 8,
                    };
                }

                if (rows.Count == statement.MaxRows || bytes + size > statement.MaxBytes)
                {
                    return (columns, rows, TursoBindings.RowsAffected(prepared), true);
                }

                var held = checked((2 * size) + (16L * row.Length) + 64);
                if (held > reserved)
                {
                    var block = Math.Max(held, 256 * 1024);
                    QosContext.ReserveResult(block, store.IsSystem ? null : QosContext.Current);
                    reserved += block;
                }

                reserved -= held;
                bytes += size;
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
        RequireOneStatement(sql);
        var native = session.Native;
        try
        {
            var prepared = TursoBindings.PrepareStatement(native, sql);
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

    private static void RequireOneStatement(string sql)
    {
        if (!SqlText.IsOneStatement(sql))
        {
            throw new SqlStatementException(SqlFailure.Error, "an engine call runs exactly one SQL statement", transactionEnded: false);
        }
    }

    private static void Bind(TursoStatementHandle prepared, SqlStatement statement)
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

    private static List<SqlStatementColumn> Columns(TursoStatementHandle prepared)
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

    private static long Counter(TursoDatabaseHandle native, string sql)
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

    private static void Release(TursoStatementHandle prepared)
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
            return new SqlStatementException(SqlFailure.Error, "the database reported an I/O error or corruption", ended, error);
        }

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
            message = coded.Groups[1].Value;
        }

        var (failure, reported) = message switch
        {
            _ when Has(message, "UNIQUE constraint failed") => (SqlFailure.Unique, "UNIQUE constraint failed" + Names(message)),
            _ when Has(message, "NOT NULL constraint failed") => (SqlFailure.NotNull, "NOT NULL constraint failed" + Names(message)),
            _ when Has(message, "CHECK constraint failed") => (SqlFailure.Check, "CHECK constraint failed" + Names(message)),
            _ when Has(message, "FOREIGN KEY constraint failed") => (SqlFailure.ForeignKey, "FOREIGN KEY constraint failed"),
            _ when Has(message, "constraint failed") => (SqlFailure.Constraint, "constraint failed"),
            _ when Has(message, "no such table") => (SqlFailure.NoSuchTable, "no such table" + Names(message)),
            _ when Has(message, "no such column") => (SqlFailure.NoSuchColumn, "no such column" + Names(message)),
            _ when Has(message, "already exists") => (SqlFailure.AlreadyExists, "already exists" + Existing(message)),
            _ when Has(message, "database is locked") || Has(message, "busy") || Has(message, "schema changed") || Has(message, "schema conflict")
                || Has(message, "write-write conflict") => (SqlFailure.Busy, "the database is busy; retry"),
            _ when error is TursoNativeException { StatusCode: 5 } || Has(message, "interrupt") => (SqlFailure.Interrupted, "the statement was interrupted"),
            _ when Has(message, "readonly") || Has(message, "read-only") || Has(message, "query_only") => (SqlFailure.ReadOnly, "attempt to write a readonly database"),
            _ when Has(message, "too big") || Has(message, "too large") || Has(message, "too many") => (SqlFailure.TooBig, "a value or statement is too big"),
            _ when Has(message, "syntax error") || Has(message, "near \"") || Has(message, "unexpected") || Has(message, "unrecognized token") =>
                (SqlFailure.Syntax, "SQL syntax error"),
            _ when SqlPhrases.FirstOrDefault(phrase => Has(message, phrase)) is { } phrase => (SqlFailure.Error, phrase),
            _ when parse => (SqlFailure.Syntax, "SQL syntax error"),
            _ => (SqlFailure.Error, "the statement failed to run"),
        };
        if (failure == SqlFailure.Unique && !ended && IsPrimaryKey(native, message))
        {
            failure = SqlFailure.PrimaryKey;
        }

        return new SqlStatementException(failure, reported, ended, error);
    }

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
        var named = ConstraintColumns(message);
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

    private static bool Has(string message, string phrase) => message.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    private static string Names(string message)
    {
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
        {
            return "";
        }

        var names = message[(colon + 2)..];
        var end = names.IndexOf(" (", StringComparison.Ordinal);
        names = (end < 0 ? names : names[..end]).Trim();
        return names.Length is > 0 and <= 512 && NamePattern().IsMatch(names) ? ": " + names : "";
    }

    private static List<(string Table, string Name)> ConstraintColumns(string message)
    {
        var names = Names(message);
        return names.Length == 0 ? [] : [.. names[2..].Split(", ").Select(name => name.Split('.')).Where(parts => parts.Length == 2).Select(parts => (parts[0], parts[1]))];
    }

    private static string Existing(string message) =>
        ExistingPattern().Match(message) is { Success: true } match && NamePattern().IsMatch(match.Groups[2].Value)
            ? $": {match.Groups[1].Value.ToLowerInvariant()} {match.Groups[2].Value}"
            : "";

    [GeneratedRegex(@"\A__turso_sqlite_error__:\d+:(.*)\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SqliteCoded();

    [GeneratedRegex(@"\A[\p{L}\p{N}_$.]+(?:, [\p{L}\p{N}_$.]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\b(table|index|view|trigger)\s+[""'`\[]?([^\s""'`\]]+)[""'`\]]?\s+already exists", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExistingPattern();
}
