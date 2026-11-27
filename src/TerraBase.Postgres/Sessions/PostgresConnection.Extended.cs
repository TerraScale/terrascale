using System.Globalization;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;

namespace TerraBase.Postgres.Sessions;

internal sealed partial class PostgresConnection
{
    private async ValueTask<bool> ExtendedAsync(ValueTask<PgError?> step)
    {
        if (await step is not { } error)
        {
            return true;
        }

        return await HandleExtendedErrorAsync(error);
    }

    private async ValueTask<bool> HandleExtendedErrorAsync(PgError error)
    {
        Writer.Error(error);
        await MarkFailedAsync();
        await EndImplicitAsync(commit: false);
        session.SkipUntilSync = true;
        if (error.Fatal)
        {
            await Writer.FlushAsync(closed);
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

        if (message.Name.Length > 0 && session.StatementCount >= PostgresLimits.MaxPreparedStatements)
        {
            return new PgError("54000", $"at most {PostgresLimits.MaxPreparedStatements.ToString(CultureInfo.InvariantCulture)} prepared statements per session");
        }

        session.ObserveCurrentSchemaEpoch();
        PgCommand command;
        var entry = message.CachedEntry is { } preResolved && preResolved.CacheGeneration == session.CacheGeneration
            ? preResolved
            : session.TryGetCachedParseEntry(message.Sql, out var cachedEntry)
                ? cachedEntry
                : null;
        if (entry is not null)
        {
            if (entry.Commands.Count != 1)
            {
                return PgError.Syntax("cannot insert multiple commands into a prepared statement");
            }

            command = entry.Commands[0];
        }
        else
        {
            (PgCommand? Command, CachedParseEntry? Entry, PgError? Error) parsed = PgCommandParser.Parse(message.Sql, session.RewriteContext) switch
            {
                IReadOnlyList<PgCommand> and [var single] => CacheSingle(message.Sql, single),
                IReadOnlyList<PgCommand> => ((PgCommand?)null, (CachedParseEntry?)null, PgError.Syntax("cannot insert multiple commands into a prepared statement")),
                PgError error => (null, null, error),
            };
            if (parsed.Command is not { } resolved)
            {
                return parsed.Error;
            }

            command = resolved;
            entry = parsed.Entry;
        }

        var count = Math.Max(message.ParameterOids.Length, command is PgSqlCommand sql ? sql.Rewrite.ParameterCount : 0);
        int[] oids;
        if (count == 0)
        {
            oids = [];
        }
        else if (message.ParameterOids.Length == count && Array.IndexOf(message.ParameterOids, 0) < 0)
        {
            oids = message.ParameterOids;
        }
        else if (AllZero(message.ParameterOids) && entry is { InferredOids: { } reusableOids } && reusableOids.Length == count)
        {
            oids = reusableOids;
        }
        else
        {
            oids = new int[count];
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

                if (Array.IndexOf(oids, 0) >= 0 && typed.Rewrite.ParameterColumns.Count > 0)
                {
                    int[]? cachedOids = entry?.InferredOids is { } fromEntry && fromEntry.Length >= count
                        ? fromEntry
                        : session.TryGetCachedInferredOids(message.Sql, out var fromCache) && fromCache.Length >= count
                            ? fromCache
                            : null;
                    if (cachedOids is not null)
                    {
                        for (var index = 0; index < count; index++)
                        {
                            if (oids[index] == 0)
                            {
                                oids[index] = cachedOids[index];
                            }
                        }
                    }
                    else
                    {
                        if (await InferParameterTypesAsync(typed, oids) is { } inferenceError)
                        {
                            return inferenceError;
                        }

                        if (session.Transaction is null && AllZero(message.ParameterOids))
                        {
                            if (entry is not null)
                            {
                                entry.InferredOids = oids;
                            }
                            else
                            {
                                session.CacheInferredOids(message.Sql, oids);
                            }
                        }
                    }
                }
                else if (session.Transaction is null && AllZero(message.ParameterOids) && entry is not null)
                {
                    entry.InferredOids = oids;
                }
            }
        }

        session.SetStatement(message.Name, command, oids, entry);
        Writer.Empty('1');
        return null;
    }

    private static bool AllZero(int[] oids)
    {
        for (var index = 0; index < oids.Length; index++)
        {
            if (oids[index] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private (PgCommand?, CachedParseEntry?, PgError?) CacheSingle(string sql, PgCommand command)
    {
        var entry = session.CacheParse(sql, [command]);
        return (command, entry, null);
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
        var outcome = await ReadMetadataAsync(schema, [.. names.Select(SqlValue.FromText)]);
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

    private async ValueTask<PgStatementResult> ReadMetadataAsync(string sql, IReadOnlyList<SqlValue> parameters)
    {
        using var scope = session.BeginStatement(closed);
        try
        {
            return await ExecuteStatementAsync(new SqlStatement(sql, parameters, NoNamed), SqlAccess.Read, scope.Token);
        }
        catch (OperationCanceledException) when (!closed.IsCancellationRequested)
        {
            return scope.TimedOut ? PgErrors.StatementTimeout : PgErrors.QueryCanceled;
        }
    }

    private PgError? Bind(PgBindMessage message)
    {
        if (!session.TryGetStatement(message.Statement, out var statement))
        {
            return new PgError("26000", $"prepared statement \"{message.Statement}\" does not exist");
        }

        if (message.ParameterCount != statement.ParameterOids.Length)
        {
            return new PgError("08P01",
                $"bind message supplies {message.ParameterCount.ToString(CultureInfo.InvariantCulture)} parameters, but prepared statement \"{message.Statement}\" requires {statement.ParameterOids.Length.ToString(CultureInfo.InvariantCulture)}");
        }

        if (message.ParameterFormats.Length > 1 && message.ParameterFormats.Length != message.ParameterCount)
        {
            return new PgError("08P01", "bind message has an invalid number of parameter formats");
        }

        if (message.Portal.Length > 0 && session.Portals.ContainsKey(message.Portal))
        {
            return new PgError("42P03", $"cursor \"{message.Portal}\" already exists");
        }

        if (message.Portal.Length > 0 && session.PortalCount >= PostgresLimits.MaxPortals)
        {
            return new PgError("54000", $"at most {PostgresLimits.MaxPortals.ToString(CultureInfo.InvariantCulture)} portals per session");
        }

        var budget = PostgresLimits.MaxPortalBytes - session.PortalBytes(except: message.Portal);
        var bytes = PgValueCodec.Footprint(message.ResultFormats);
        SqlValue[] values = message.ParameterCount == 0
            ? []
            : message.Portal.Length == 0
                ? session.RentUnnamedParameters(message.ParameterCount)
                : new SqlValue[message.ParameterCount];
        for (var index = 0; index < values.Length; index++)
        {
            var format = message.ParameterFormats.Length == 0 ? (short)0 : message.ParameterFormats[Math.Min(index, message.ParameterFormats.Length - 1)];
            var oid = statement.ParameterOids[index];
            var slice = message.Parameters[index];
            if (slice.Length == -1)
            {
                values[index] = SqlValue.Null;
            }
            else if (!PgValueCodec.TryDecode(oid, format, message.ParameterBuffer.AsSpan(slice.Offset, slice.Length), out values[index]))
            {
                return new PgError(PgValueCodec.DecodeFailureState(oid, format),
                    $"invalid input for parameter ${(index + 1).ToString(CultureInfo.InvariantCulture)} of type {PgOid.FormatName(oid)}");
            }

            bytes += PgValueCodec.Footprint(values[index]);
            if (bytes > budget)
            {
                return PgErrors.PortalMemory;
            }
        }

        if (bytes > budget)
        {
            return PgErrors.PortalMemory;
        }

        session.SetPortal(message.Portal, statement, values, bytes, message.ResultFormats, statement.Described ? statement.Columns : null);
        Writer.Empty('2');
        return null;
    }

    private async ValueTask<PgError?> DescribeAsync(PgDescribeMessage message)
    {
        if (message.Target == 'S')
        {
            if (!session.TryGetStatement(message.Name, out var statement))
            {
                return new PgError("26000", $"prepared statement \"{message.Name}\" does not exist");
            }

            if (!statement.Described)
            {
                var columnsOutcome = await DescribeColumnsAsync(statement.Command, [.. statement.ParameterOids.Select(static _ => SqlValue.Null)], statement.CacheEntry);
                (IReadOnlyList<PgColumn>? Columns, PgError? Error) described = columnsOutcome switch
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

            int[] parameterOids = Array.IndexOf(statement.ParameterOids, 0) < 0
                ? statement.ParameterOids
                : [.. statement.ParameterOids.Select(static oid => oid == 0 ? PgOid.Text : oid)];
            Writer.ParameterDescription(parameterOids);
            WriteDescription(statement.Columns, []);
            return null;
        }

        if (!session.TryGetPortal(message.Name, out var portal))
        {
            return new PgError("34000", $"portal \"{message.Name}\" does not exist");
        }

        if (portal.Columns is null && !portal.Completed)
        {
            if (portal.Statement.Command is PgSqlCommand { Access: SqlAccess.Read } or PgShowCommand or PgCannedCommand && portal.Result is null)
            {
                var outcome = await GuardAsync(portal.Statement.Command, portal.Parameters, null, portal.Statement.CacheEntry);
                (PgResult? Result, PgError? Error) executed = outcome switch
                {
                    PgResult value => (value, (PgError?)null),
                    PgError failure => (null, failure),
                };
                if (executed.Error is { } error)
                {
                    return error;
                }

                if (executed.Result is { } result && !session.TryHold(message.Name, portal, result))
                {
                    session.RemovePortal(message.Name);
                    return PgErrors.PortalMemory;
                }

                portal.Columns = executed.Result?.Columns;
            }
            else
            {
                var columnsOutcome = await DescribeColumnsAsync(portal.Statement.Command, portal.Parameters, portal.Statement.CacheEntry);
                (IReadOnlyList<PgColumn>? Columns, PgError? Error) described = columnsOutcome switch
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
        if (!session.TryGetPortal(message.Portal, out var portal))
        {
            return new PgError("34000", $"portal \"{message.Portal}\" does not exist");
        }

        var current = portal.Result;
        var deferredCommit = false;
        if (current is null)
        {
            if (portal.Completed)
            {
                Writer.CommandComplete("SELECT 0");
                return null;
            }

            deferredCommit = message.MaxRows > 0 && session.Transaction is null && portal.Statement.Command is PgSqlCommand { Access: SqlAccess.Write };
            if (deferredCommit)
            {
                session.Transaction = new PostgresTransactionBlock(session.Settings.DefaultMode, SqlAccess.Write, isImplicit: true, session.Time.GetTimestamp());
            }

            var outcome = await GuardAsync(portal.Statement.Command, portal.Parameters, portal.Columns ?? portal.Statement.Columns, portal.Statement.CacheEntry);
            (PgResult? Result, PgError? Error) executed = outcome switch
            {
                PgResult value => (value, (PgError?)null),
                PgError failure => (null, failure),
            };
            if (executed.Error is { } error)
            {
                return error;
            }

            current = executed.Result ?? PgResult.EmptyQuery;
        }

        if (current.Tag is null)
        {
            if (await CommitDeferredAsync(deferredCommit) is { } emptyCommitError)
            {
                session.RemovePortal(message.Portal);
                return emptyCommitError;
            }

            Writer.Empty('I');
            portal.Release();
            portal.Completed = true;
            return null;
        }

        var shape = portal.Columns ?? current.Columns ?? [];
        var end = message.MaxRows > 0 ? Math.Min(current.Rows.Count, portal.Position + message.MaxRows) : current.Rows.Count;
        if (end < current.Rows.Count && !ReferenceEquals(portal.Result, current) && !session.TryHold(message.Portal, portal, current))
        {
            session.RemovePortal(message.Portal);
            return PgErrors.PortalMemory;
        }

        var unsent = Writer.Buffer.Length;
        var cursor = portal.Position;
        while (cursor < end)
        {
            if (Writer.WriteRows(current.Rows, cursor, end, shape, portal.ResultFormats, stopWhenFull: !deferredCommit, out cursor) is { } encodeError)
            {
                if (deferredCommit)
                {
                    Writer.Buffer.Truncate(unsent);
                }

                session.RemovePortal(message.Portal);
                return encodeError;
            }

            if (!deferredCommit && Writer.ShouldFlush && !await Writer.FlushAsync(closed))
            {
                return new PgError("08006", "the client stopped reading results", Fatal: true);
            }
        }

        if (await CommitDeferredAsync(deferredCommit) is { } commitError)
        {
            Writer.Buffer.Truncate(unsent);
            session.RemovePortal(message.Portal);
            return commitError;
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
        portal.Release();
        portal.Completed = true;
        return null;
    }

    private async ValueTask<PgError?> CommitDeferredAsync(bool deferred) => deferred ? await EndImplicitAsync(commit: true) : null;

    private PgError? Close(PgCloseMessage message)
    {
        if (message.Target == 'S')
        {
            session.RemoveStatement(message.Name);
        }
        else
        {
            session.RemovePortal(message.Name);
        }

        Writer.Empty('3');
        return null;
    }
}
