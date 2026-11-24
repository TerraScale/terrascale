using System.Collections.Frozen;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;

namespace TerraBase.Postgres.Sessions;

internal sealed partial class PostgresConnection(PostgresSession session, PgWire wire, CancellationToken closed)
{
    private static readonly IReadOnlyDictionary<string, SqlValue> NoNamed = FrozenDictionary<string, SqlValue>.Empty;

    private volatile bool _revoked;
    private volatile bool _shuttingDown;

    private PgFrameReader Reader => wire.Reader;

    private PgBackendWriter Writer => wire.Writer;

    public static async ValueTask ReleaseAsync(PostgresSession session)
    {
        await StopListeningAsync(session);
        if (session.Transaction?.Active is { } active)
        {
            await active.RollbackAsync(CancellationToken.None);
            await active.DisposeAsync();
        }

        session.Transaction = null;
    }

    public async Task RunAsync()
    {
        using var revocation = session.Grant.Revocation.Register(() =>
        {
            _revoked = true;
            Reader.Interrupt();
        });
        using var shutdown = session.Grant.Shutdown.Register(() =>
        {
            _shuttingDown = true;
            Reader.Interrupt();
        });
        while (true)
        {
            if (_shuttingDown)
            {
                await FatalAsync(PgErrors.AdministratorShutdown);
                return;
            }

            if (_revoked)
            {
                await FatalAsync(PgErrors.CredentialRevoked);
                return;
            }

            if (session.Status == 'I' && !await DeliverNotificationsAsync())
            {
                return;
            }

            var open = session.Transaction is { Failed: false } block ? block : null;
            var timeout = open is null ? PostgresLimits.SessionIdleTimeout : PostgresLimits.MaxTransaction - session.Time.GetElapsedTime(open.Started);
            var read = await Reader.ReadMessageAsync(timeout, open is null ? null : timeout, closed);
            var proceed = read switch
            {
                PgFrontendMessage when open is not null && session.Time.GetElapsedTime(open.Started, Reader.LastMessageStarted) > PostgresLimits.MaxTransaction =>
                    await FatalAsync(PgErrors.TransactionExpired),
                PgFrontendMessage message => await HandleAsync(message),
                PgPeerClosed => false,
                PgIdleExpired => await FatalAsync(open is null
                    ? new PgError("57P05", "terminating connection due to idle-session timeout", Fatal: true)
                    : PgErrors.TransactionExpired),
                PgIncompleteExpired => await FatalAsync(open is null
                    ? new PgError("57P05", "incomplete message timeout exceeded", Fatal: true)
                    : PgErrors.TransactionExpired),
                PgReadInterrupted => true,
                PgError error => await FatalAsync(error),
            };
            if (!proceed)
            {
                return;
            }

            if (Writer.ShouldFlush && !await Writer.FlushAsync(closed))
            {
                return;
            }
        }
    }

    private async ValueTask<bool> HandleAsync(PgFrontendMessage message)
    {
        if (session.SkipUntilSync && message is not (PgSyncMessage or PgTerminateMessage))
        {
            return true;
        }

        return message switch
        {
            PgQueryMessage query => await SimpleQueryAsync(query.Sql),
            PgParseMessage parse => await ExtendedAsync(ParseAsync(parse)),
            PgBindMessage bind => await ExtendedAsync(ValueTask.FromResult(Bind(bind))),
            PgDescribeMessage describe => await ExtendedAsync(DescribeAsync(describe)),
            PgExecuteMessage execute => await ExtendedAsync(ExecuteAsync(execute)),
            PgCloseMessage close => await ExtendedAsync(ValueTask.FromResult(Close(close))),
            PgSyncMessage => await SyncAsync(),
            PgFlushMessage => await Writer.FlushAsync(closed),
            PgTerminateMessage => false,
            PgCopyDataMessage or PgCopyDoneMessage or PgCopyFailMessage => true,
            PgFunctionCallMessage => await FunctionCallAsync(),
            PgPasswordMessage => await FatalAsync(PgError.ProtocolViolation("unexpected password message")),
            PgUnknownMessage unknown => await FatalAsync(PgError.ProtocolViolation($"invalid frontend message type {(int)unknown.Type}")),
        };
    }

    private async ValueTask<bool> SimpleQueryAsync(string sql)
    {
        var parsed = PgCommandParser.Parse(sql, session.RewriteContext);
        var failure = parsed switch
        {
            IReadOnlyList<PgCommand> commands => await RunCommandsAsync(commands),
            PgError error => error,
        };
        if (failure is not null)
        {
            Writer.Error(failure);
            await MarkFailedAsync();
            if (failure.Fatal)
            {
                await Writer.FlushAsync(closed);
                return false;
            }
        }

        return await ReadyAsync();
    }

    private async ValueTask<PgError?> RunCommandsAsync(IReadOnlyList<PgCommand> commands)
    {
        var implicitBlock = commands.Count > 1 && session.Transaction is null
            && commands.Any(static command => command is PgSqlCommand { Access: SqlAccess.Write } or PgCopyFromCommand)
            && !commands.Any(static command => command is PgBeginCommand or PgCommitCommand or PgRollbackCommand);
        if (implicitBlock)
        {
            session.Transaction = new PostgresTransactionBlock(session.Settings.DefaultMode, SqlAccess.Write, isImplicit: true, session.Time.GetTimestamp());
        }

        foreach (var command in commands)
        {
            var outcome = await GuardAsync(command, [], describedColumns: null);
            var failure = outcome switch
            {
                PgResult result => WriteResult(result, [], describe: true, columns: null),
                PgError error => error,
            };
            if (failure is not null)
            {
                if (implicitBlock)
                {
                    await EndImplicitAsync(commit: false);
                }

                return failure;
            }

            if (Writer.ShouldFlush && !await Writer.FlushAsync(closed))
            {
                return new PgError("08006", "the client stopped reading results", Fatal: true);
            }
        }

        return implicitBlock ? await EndImplicitAsync(commit: true) : null;
    }

    private PgError? WriteResult(PgResult result, IReadOnlyList<short> formats, bool describe, IReadOnlyList<PgColumn>? columns)
    {
        if (result.Tag is null)
        {
            Writer.Empty('I');
            return null;
        }

        var shape = columns ?? result.Columns;
        if (describe && shape is not null)
        {
            Writer.RowDescription(shape, formats);
        }

        foreach (var row in result.Rows)
        {
            if (Writer.DataRow(row, shape ?? [], formats) is { } error)
            {
                return error;
            }
        }

        if (result.Notice is { } notice)
        {
            Writer.Notice(notice);
        }

        Writer.CommandComplete(result.Tag);
        return null;
    }

    private async ValueTask<bool> ReadyAsync()
    {
        foreach (var (name, value) in session.PendingReports)
        {
            Writer.ParameterStatus(name, value);
        }

        session.PendingReports.Clear();
        Writer.ReadyForQuery(session.Status);
        return await Writer.FlushAsync(closed);
    }

    private async ValueTask<bool> SyncAsync()
    {
        session.SkipUntilSync = false;
        session.Portals.Remove(string.Empty);
        return await ReadyAsync();
    }

    private async ValueTask<bool> FunctionCallAsync()
    {
        Writer.Error(PgError.Unsupported("the function call sub-protocol"));
        return await ReadyAsync();
    }

    private async ValueTask<bool> FatalAsync(PgError error)
    {
        Writer.Error(error with { Fatal = true });
        await Writer.FlushAsync(closed);
        return false;
    }

    private async ValueTask MarkFailedAsync()
    {
        if (session.Transaction is not { Implicit: false } block)
        {
            return;
        }

        block.Failed = true;
        if (block.Savepoints || block.Active is not { } active)
        {
            return;
        }

        block.Active = null;
        await active.RollbackAsync(CancellationToken.None);
        await active.DisposeAsync();
    }
}
