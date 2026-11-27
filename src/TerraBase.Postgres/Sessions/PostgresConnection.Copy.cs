using System.Globalization;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;

namespace TerraBase.Postgres.Sessions;

internal sealed partial class PostgresConnection
{
    private async ValueTask<PgOutcome> CopyFromAsync(PgCopyFromCommand command, CancellationToken cancellationToken)
    {
        if (session.Grant.MaxAccess == SqlAccess.Read)
        {
            return PgErrors.ReadOnlyCredential;
        }

        var schema = await ExecuteStatementAsync(new SqlStatement("SELECT name, type FROM pragma_table_info(?1)", [SqlValue.FromText(command.Table)], NoNamed),
            SqlAccess.Read, cancellationToken);
        var (columns, schemaError) = schema switch
        {
            SqlResultSet set => CopyColumns(command, set),
            PgError error => ([], error),
        };
        if (schemaError is not null)
        {
            return schemaError;
        }

        var placeholders = string.Join(", ", columns.Select(static (_, index) => "?" + (index + 1).ToString(CultureInfo.InvariantCulture)));
        var insert = $"INSERT INTO {Quote(command.Table)} ({string.Join(", ", columns.Select(static column => Quote(column.Name)))}) VALUES ({placeholders})";
        Writer.CopyResponse('G', command.Options.Format == PgCopyFormat.Binary, columns.Count);
        if (!await Writer.FlushAsync(closed))
        {
            return new PgError("08006", "the client stopped reading", Fatal: true);
        }

        var ownBlock = session.Transaction is null;
        var block = session.Transaction ??= new PostgresTransactionBlock(session.Settings.DefaultMode, SqlAccess.Write, isImplicit: true, session.Time.GetTimestamp());

        var remaining = PostgresLimits.MaxTransaction - session.Time.GetElapsedTime(block.Started);
        using var expiry = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, session.Time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            using var reader = new PgCopyReader(command.Options, [.. columns.Select(static column => column.Oid)]);
            var (rows, failure) = await ReceiveAsync(reader, insert, block.Started, bounded.Token);
            if (ownBlock)
            {
                failure = failure is null
                    ? await CommitImplicitAsync(block)
                    : await EndImplicitAsync(commit: false) ?? failure;
            }

            return failure is null ? PgResult.Command("COPY " + rows.ToString(CultureInfo.InvariantCulture)) : failure;
        }
        finally
        {
            if (ownBlock && ReferenceEquals(session.Transaction, block))
            {
                await EndTransactionAsync(block, rollback: true);
            }
        }
    }

    private async ValueTask<PgError?> CommitImplicitAsync(PostgresTransactionBlock block)
    {
        if (CopyInterruption(block.Started) is { } interrupted)
        {
            await EndTransactionAsync(block, rollback: true);
            return interrupted;
        }

        return await FinishAsync(block, closed);
    }

    private PgError? CopyInterruption(long started) =>
        ShuttingDown ? PgErrors.AdministratorShutdown
        : Revoked ? PgErrors.CredentialRevoked
        : session.Time.GetElapsedTime(started) >= PostgresLimits.MaxTransaction ? PgErrors.TransactionExpired
        : null;

    private static (List<PgColumn> Columns, PgError? Error) CopyColumns(PgCopyFromCommand command, SqlResultSet set)
    {
        List<PgColumn> table = [];
        foreach (var row in set.Rows)
        {
            if (row.Count >= 2 && row[0].TryGetText(out var name))
            {
                table.Add(new PgColumn(name, PgOid.FromDeclaredType(row[1].TryGetText(out var declared) ? declared : null)));
            }
        }

        if (table.Count == 0)
        {
            return ([], new PgError("42P01", $"relation \"{command.Table}\" does not exist"));
        }

        if (command.Columns.Count == 0)
        {
            return (table, null);
        }

        List<PgColumn> selected = [];
        foreach (var name in command.Columns)
        {
            var index = table.FindIndex(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return ([], new PgError("42703", $"column \"{name}\" of relation \"{command.Table}\" does not exist"));
            }

            selected.Add(table[index]);
        }

        return (selected, null);
    }

    private async ValueTask<(long Rows, PgError? Error)> ReceiveAsync(PgCopyReader reader, string insert, long started, CancellationToken cancellationToken)
    {
        long rows = 0;
        while (true)
        {
            if (CopyInterruption(started) is { } interrupted)
            {
                return (rows, interrupted);
            }

            var remaining = PostgresLimits.MaxTransaction - session.Time.GetElapsedTime(started);
            var read = await Reader.ReadMessageAsync(remaining, new PgReadDeadline(remaining, TimeSpan.Zero), closed);
            var (records, done, error) = read switch
            {
                PgFrontendMessage message => message switch
                {
                    PgCopyDataMessage data => Unpack(reader.Consume(data.Data), done: false),
                    PgCopyDoneMessage => Unpack(reader.Complete(), done: true),
                    PgCopyFailMessage fail => (null, true, new PgError("57014", $"COPY from stdin failed: {fail.Reason}")),
                    PgFlushMessage or PgSyncMessage => (null, false, null),
                    PgQueryMessage or PgParseMessage or PgBindMessage or PgDescribeMessage or PgExecuteMessage or PgCloseMessage or PgTerminateMessage
                        or PgFunctionCallMessage or PgPasswordMessage or PgUnknownMessage => (null, true, PgError.ProtocolViolation("unexpected message type during COPY from stdin")),
                },
                PgPeerClosed => (null, true, new PgError("08006", "connection lost during COPY", Fatal: true)),
                PgIdleExpired or PgIncompleteExpired => (null, true, PgErrors.TransactionExpired),
                PgReadInterrupted => (null, false, null),
                PgError violation => (null, true, violation),
            };
            if (error is not null)
            {
                return (rows, error);
            }

            if (CopyInterruption(started) is { } expired)
            {
                return (rows, expired);
            }

            foreach (var record in records ?? [])
            {
                var inserted = await ExecuteStatementAsync(new SqlStatement(insert, record, NoNamed), SqlAccess.Write, cancellationToken);
                var failure = inserted switch
                {
                    SqlResultSet => null,
                    PgError insertError => insertError,
                };
                if (failure is not null)
                {
                    return (rows, failure);
                }

                rows++;
            }

            if (done)
            {
                return (rows, null);
            }
        }
    }

    private static (List<SqlValue[]>? Records, bool Done, PgError? Error) Unpack(PgCopyRowsResult parsed, bool done) => parsed switch
    {
        List<SqlValue[]> records => (records, done, null),
        PgError error => (null, true, error),
    };

    private async ValueTask<PgOutcome> CopyToAsync(PgCopyToCommand command, IReadOnlyList<SqlValue> parameters, CancellationToken cancellationToken)
    {
        var outcome = await ExecuteSqlAsync(command.Query, parameters, null, cacheEntry: null, cancellationToken);
        return outcome switch
        {
            PgResult result => await StreamCopyAsync(command.Options, result),
            PgError error => error,
        };
    }

    private async ValueTask<PgOutcome> StreamCopyAsync(PgCopyOptions options, PgResult result)
    {
        var columns = result.Columns ?? [];
        Writer.CopyResponse('H', options.Format == PgCopyFormat.Binary, columns.Count);
        PgCopyWriter.Header(Writer.Buffer, options, columns);
        foreach (var row in result.Rows)
        {
            if (PgCopyWriter.Row(Writer.Buffer, options, columns, row) is { } error)
            {
                return error;
            }

            if (Writer.ShouldFlush && !await Writer.FlushAsync(closed))
            {
                return new PgError("08006", "the client stopped reading COPY data", Fatal: true);
            }
        }

        PgCopyWriter.Trailer(Writer.Buffer, options);
        Writer.Empty('c');
        return PgResult.Command("COPY " + result.Rows.Count.ToString(CultureInfo.InvariantCulture));
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
