using System.Globalization;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;

namespace TerraBase.Postgres.Sessions;

internal sealed partial class PostgresConnection
{
    private async ValueTask<bool> ExtendedAsync(ValueTask<PgError?> step)
    {
        if (await step.ConfigureAwait(false) is not { } error)
        {
            return true;
        }

        Writer.Error(error);
        await MarkFailedAsync().ConfigureAwait(false);
        await EndImplicitAsync(commit: false).ConfigureAwait(false);
        session.SkipUntilSync = true;
        if (error.Fatal)
        {
            await Writer.FlushAsync(closed).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async ValueTask<PgError?> ParseAsync(PgParseMessage message)
    {
        if (message.Name.Length > 0 && session.Statements.ContainsKey(message.Name))
        {
            return new PgError("42P05", $"prepared statement \"{message.Name}\" already exists");
        }

        if (message.Name.Length > 0 && session.Statements.Count >= PostgresLimits.MaxPreparedStatements)
        {
            return new PgError("54000", $"at most {PostgresLimits.MaxPreparedStatements.ToString(CultureInfo.InvariantCulture)} prepared statements per session");
        }

        (PgCommand? Command, PgError? Error) parsed = PgCommandParser.Parse(message.Sql, session.RewriteContext) switch
        {
            IReadOnlyList<PgCommand> and [var single] => (single, (PgError?)null),
            IReadOnlyList<PgCommand> => ((PgCommand?)null, PgError.Syntax("cannot insert multiple commands into a prepared statement")),
            PgError error => (null, error),
        };
        if (parsed.Command is not { } command)
        {
            return parsed.Error;
        }

        var count = Math.Max(message.ParameterOids.Length, command is PgSqlCommand sql ? sql.Rewrite.ParameterCount : 0);
        var oids = new int[count];
        message.ParameterOids.CopyTo(oids, 0);
        if (command is PgSqlCommand typed)
        {
            foreach (var (parameter, oid) in typed.Rewrite.ParameterTypes)
            {
                if (parameter <= count && oids[parameter - 1] == 0)
                {
                    oids[parameter - 1] = oid;
                }
            }

            if (Array.IndexOf(oids, 0) >= 0 && typed.Rewrite.ParameterColumns.Count > 0
                && await InferParameterTypesAsync(typed, oids).ConfigureAwait(false) is { } inferenceError)
            {
                return inferenceError;
            }
        }

        session.Statements[message.Name] = new PgPreparedStatement(message.Name, command, oids);
        Writer.Empty('1');
        return null;
    }

    private async ValueTask<PgError?> InferParameterTypesAsync(PgSqlCommand command, int[] oids)
    {
        if (session.Transaction is { Failed: true })
        {
            return PgError.InFailedTransaction;
        }

        var names = command.Rewrite.ReferencedNames;
        if (names.Count == 0)
        {
            return null;
        }

        var placeholders = string.Join(", ", Enumerable.Range(1, names.Count).Select(static number => "?" + number.ToString(CultureInfo.InvariantCulture)));
        var schema = $"SELECT m.name, p.name, p.type FROM sqlite_schema m JOIN pragma_table_info(m.name) p WHERE m.type IN ('table', 'view') AND m.name COLLATE NOCASE IN ({placeholders})";
        var outcome = await ReadMetadataAsync(schema, [.. names.Select(SqlValue.FromText)]).ConfigureAwait(false);
        return outcome switch
        {
            SqlResultSet set => ApplyParameterTypes(command, oids, set),
            PgError error => error,
        };
    }

    private static PgError? ApplyParameterTypes(PgSqlCommand command, int[] oids, SqlResultSet set)
    {
        Dictionary<string, int> target = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> types = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ambiguous = new(StringComparer.OrdinalIgnoreCase);
        foreach (var row in set.Rows)
        {
            if (row.Count < 3 || !row[0].TryGetText(out var table) || !row[1].TryGetText(out var name))
            {
                continue;
            }

            var oid = row[2].TryGetText(out var declared) ? PgOid.FromDeclaredType(declared) : 0;
            if (command.TargetTable is { } owner && owner.Equals(table, StringComparison.OrdinalIgnoreCase))
            {
                target[name] = oid;
            }

            if (types.TryGetValue(name, out var existing) && existing != oid)
            {
                ambiguous.Add(name);
            }

            types[name] = oid;
        }

        foreach (var (parameter, column) in command.Rewrite.ParameterColumns)
        {
            if (parameter > oids.Length || oids[parameter - 1] != 0)
            {
                continue;
            }

            var oid = target.TryGetValue(column, out var owned) ? owned
                : !ambiguous.Contains(column) && types.TryGetValue(column, out var shared) ? shared
                : 0;
            if (oid > 0)
            {
                oids[parameter - 1] = oid;
            }
        }

        return null;
    }

    private async ValueTask<Result<SqlResultSet, PgError>> ReadMetadataAsync(string sql, IReadOnlyList<SqlValue> parameters)
    {
        using var scope = session.BeginStatement(closed);
        try
        {
            return await ExecuteStatementAsync(new SqlStatement(sql, parameters, NoNamed), SqlAccess.Read, scope.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!closed.IsCancellationRequested)
        {
            return scope.TimedOut ? PgErrors.StatementTimeout : PgErrors.QueryCanceled;
        }
    }

    private ValueTask<PgError?> BindAsync(PgBindMessage message)
    {
        if (!session.Statements.TryGetValue(message.Statement, out var statement))
        {
            return ValueTask.FromResult<PgError?>(new PgError("26000", $"prepared statement \"{message.Statement}\" does not exist"));
        }

        if (message.Parameters.Length != statement.ParameterOids.Length)
        {
            return ValueTask.FromResult<PgError?>(new PgError("08P01",
                $"bind message supplies {message.Parameters.Length.ToString(CultureInfo.InvariantCulture)} parameters, but prepared statement \"{message.Statement}\" requires {statement.ParameterOids.Length.ToString(CultureInfo.InvariantCulture)}"));
        }

        if (message.ParameterFormats.Length > 1 && message.ParameterFormats.Length != message.Parameters.Length)
        {
            return ValueTask.FromResult<PgError?>(new PgError("08P01", "bind message has an invalid number of parameter formats"));
        }

        var values = new SqlValue[message.Parameters.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var format = message.ParameterFormats.Length == 0 ? (short)0 : message.ParameterFormats[Math.Min(index, message.ParameterFormats.Length - 1)];
            if (!PgValueCodec.TryDecode(statement.ParameterOids[index], format, message.Parameters[index], out values[index]))
            {
                return ValueTask.FromResult<PgError?>(new PgError("22P02",
                    $"invalid input for parameter ${(index + 1).ToString(CultureInfo.InvariantCulture)} of type {PgOid.FormatName(statement.ParameterOids[index])}"));
            }
        }

        if (message.Portal.Length > 0 && session.Portals.ContainsKey(message.Portal))
        {
            return ValueTask.FromResult<PgError?>(new PgError("42P03", $"cursor \"{message.Portal}\" already exists"));
        }

        if (message.Portal.Length > 0 && session.Portals.Count >= PostgresLimits.MaxPortals)
        {
            return ValueTask.FromResult<PgError?>(new PgError("54000", $"at most {PostgresLimits.MaxPortals.ToString(CultureInfo.InvariantCulture)} portals per session"));
        }

        session.Portals[message.Portal] = new PgPortal(statement, values, message.ResultFormats) { Columns = statement.Described ? statement.Columns : null };
        Writer.Empty('2');
        return ValueTask.FromResult<PgError?>(null);
    }

    private async ValueTask<PgError?> DescribeAsync(PgDescribeMessage message)
    {
        if (message.Target == 'S')
        {
            if (!session.Statements.TryGetValue(message.Name, out var statement))
            {
                return new PgError("26000", $"prepared statement \"{message.Name}\" does not exist");
            }

            if (!statement.Described)
            {
                (IReadOnlyList<PgColumn>? Columns, PgError? Error) described = await DescribeColumnsAsync(statement.Command, [.. statement.ParameterOids.Select(static _ => SqlValue.Null)]).ConfigureAwait(false) switch
                {
                    PgDescription description => (description.Columns, (PgError?)null),
                    PgError failure => (null, failure),
                };
                if (described.Error is { } error)
                {
                    return error;
                }

                statement.Columns = described.Columns;
                statement.Described = true;
            }

            Writer.ParameterDescription([.. statement.ParameterOids.Select(static oid => oid == 0 ? PgOid.Text : oid)]);
            WriteDescription(statement.Columns, []);
            return null;
        }

        if (!session.Portals.TryGetValue(message.Name, out var portal))
        {
            return new PgError("34000", $"portal \"{message.Name}\" does not exist");
        }

        if (portal.Columns is null && !portal.Completed)
        {
            if (portal.Statement.Command is PgSqlCommand { Access: SqlAccess.Read } or PgShowCommand or PgCannedCommand && portal.Result is null)
            {
                (PgResult? Result, PgError? Error) executed = await GuardAsync(portal.Statement.Command, portal.Parameters, null).ConfigureAwait(false) switch
                {
                    PgResult value => (value, (PgError?)null),
                    PgError failure => (null, failure),
                };
                if (executed.Error is { } error)
                {
                    return error;
                }

                portal.Result = executed.Result;
                portal.Columns = executed.Result?.Columns;
            }
            else
            {
                (IReadOnlyList<PgColumn>? Columns, PgError? Error) described = await DescribeColumnsAsync(portal.Statement.Command, portal.Parameters).ConfigureAwait(false) switch
                {
                    PgDescription description => (description.Columns, (PgError?)null),
                    PgError failure => (null, failure),
                };
                if (described.Error is { } error)
                {
                    return error;
                }

                portal.Columns = described.Columns;
            }
        }

        WriteDescription(portal.Columns, portal.ResultFormats);
        return null;
    }

    private void WriteDescription(IReadOnlyList<PgColumn>? columns, IReadOnlyList<short> formats)
    {
        if (columns is { Count: > 0 })
        {
            Writer.RowDescription(columns, formats);
            return;
        }

        Writer.Empty('n');
    }

    private async ValueTask<PgError?> ExecuteAsync(PgExecuteMessage message)
    {
        if (!session.Portals.TryGetValue(message.Portal, out var portal))
        {
            return new PgError("34000", $"portal \"{message.Portal}\" does not exist");
        }

        if (portal.Result is null)
        {
            if (portal.Completed)
            {
                Writer.CommandComplete("SELECT 0");
                return null;
            }

            (PgResult? Result, PgError? Error) executed = await GuardAsync(portal.Statement.Command, portal.Parameters, portal.Columns ?? portal.Statement.Columns).ConfigureAwait(false) switch
            {
                PgResult value => (value, (PgError?)null),
                PgError failure => (null, failure),
            };
            if (executed.Error is { } error)
            {
                return error;
            }

            portal.Result = executed.Result;
        }

        var current = portal.Result ?? PgResult.EmptyQuery;
        if (current.Tag is null)
        {
            Writer.Empty('I');
            portal.Result = null;
            portal.Completed = true;
            return null;
        }

        var shape = portal.Columns ?? current.Columns ?? [];
        var end = message.MaxRows > 0 ? Math.Min(current.Rows.Count, portal.Position + message.MaxRows) : current.Rows.Count;
        for (var index = portal.Position; index < end; index++)
        {
            if (Writer.DataRow(current.Rows[index], shape, portal.ResultFormats) is { } encodeError)
            {
                return encodeError;
            }

            if (Writer.ShouldFlush && !await Writer.FlushAsync(closed).ConfigureAwait(false))
            {
                return new PgError("08006", "the client stopped reading results", Fatal: true);
            }
        }

        portal.Position = end;
        if (end < current.Rows.Count)
        {
            Writer.Empty('s');
            return null;
        }

        if (current.Notice is { } notice)
        {
            Writer.Notice(notice);
        }

        Writer.CommandComplete(current.Tag);
        portal.Result = null;
        portal.Completed = true;
        return null;
    }

    private PgError? Close(PgCloseMessage message)
    {
        if (message.Target == 'S')
        {
            session.Statements.Remove(message.Name);
        }
        else
        {
            session.Portals.Remove(message.Name);
        }

        Writer.Empty('3');
        return null;
    }
}
