using System.Collections.Frozen;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;

namespace TerraBase.Postgres.Sessions;

internal sealed partial class PostgresConnection(PostgresSession session, PgWire wire, CancellationToken closed)
{
    private static readonly IReadOnlyDictionary<string, SqlValue> NoNamed = FrozenDictionary<string, SqlValue>.Empty;

    private volatile bool _revoked;

    private PgFrameReader Reader => wire.Reader;

    private PgBackendWriter Writer => wire.Writer;

    public static async ValueTask ReleaseAsync(PostgresSession session)
    {
        await StopListeningAsync(session).ConfigureAwait(false);
        if (session.Transaction?.Active is { } active)
        {
            await active.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await active.DisposeAsync().ConfigureAwait(false);
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
        while (true)
        {
            if (_revoked)
            {
                await FatalAsync(new PgError("28000", "the credential for this session was revoked", Fatal: true)).ConfigureAwait(false);
                return;
            }

            if (session.Status == 'I' && !await DeliverNotificationsAsync().ConfigureAwait(false))
            {
                return;
            }

            var timeout = session.Transaction is null ? PostgresLimits.SessionIdleTimeout : PostgresLimits.IdleInTransactionTimeout;
            var read = await Reader.ReadMessageAsync(timeout, closed).ConfigureAwait(false);
            var proceed = read switch
            {
                PgFrontendMessage message => await HandleAsync(message).ConfigureAwait(false),
                PgPeerClosed => false,
                PgIdleExpired => await FatalAsync(session.Transaction is null
                    ? new PgError("57P05", "terminating connection due to idle-session timeout", Fatal: true)
                    : new PgError("25P03", "terminating connection due to idle-in-transaction timeout", Fatal: true)).ConfigureAwait(false),
                PgReadInterrupted => true,
                PgError error => await FatalAsync(error).ConfigureAwait(false),
            };
            if (!proceed)
            {
                return;
            }

            if (Writer.ShouldFlush && !await Writer.FlushAsync(closed).ConfigureAwait(false))
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
            PgQueryMessage query => await SimpleQueryAsync(query.Sql).ConfigureAwait(false),
            PgParseMessage parse => await ExtendedAsync(ParseAsync(parse)).ConfigureAwait(false),
            PgBindMessage bind => await ExtendedAsync(BindAsync(bind)).ConfigureAwait(false),
            PgDescribeMessage describe => await ExtendedAsync(DescribeAsync(describe)).ConfigureAwait(false),
            PgExecuteMessage execute => await ExtendedAsync(ExecuteAsync(execute)).ConfigureAwait(false),
            PgCloseMessage close => await ExtendedAsync(ValueTask.FromResult(Close(close))).ConfigureAwait(false),
            PgSyncMessage => await SyncAsync().ConfigureAwait(false),
            PgFlushMessage => await Writer.FlushAsync(closed).ConfigureAwait(false),
            PgTerminateMessage => false,
            PgCopyDataMessage or PgCopyDoneMessage or PgCopyFailMessage => true,
            PgFunctionCallMessage => await FunctionCallAsync().ConfigureAwait(false),
            PgPasswordMessage => await FatalAsync(PgError.ProtocolViolation("unexpected password message")).ConfigureAwait(false),
            PgUnknownMessage unknown => await FatalAsync(PgError.ProtocolViolation($"invalid frontend message type {(int)unknown.Type}")).ConfigureAwait(false),
        };
    }

    private async ValueTask<bool> SimpleQueryAsync(string sql)
    {
        var parsed = PgCommandParser.Parse(sql, session.RewriteContext);
        var failure = parsed switch
        {
            IReadOnlyList<PgCommand> commands => await RunCommandsAsync(commands).ConfigureAwait(false),
            PgError error => error,
        };
        if (failure is not null)
        {
            Writer.Error(failure);
            MarkFailed();
            if (failure.Fatal)
            {
                await Writer.FlushAsync(closed).ConfigureAwait(false);
                return false;
            }
        }

        return await ReadyAsync().ConfigureAwait(false);
    }

    private async ValueTask<PgError?> RunCommandsAsync(IReadOnlyList<PgCommand> commands)
    {
        var implicitBlock = commands.Count > 1 && session.Transaction is null
            && commands.Any(static command => command is PgSqlCommand { Access: SqlAccess.Write } or PgCopyFromCommand)
            && !commands.Any(static command => command is PgBeginCommand or PgCommitCommand or PgRollbackCommand);
        if (implicitBlock)
        {
            session.Transaction = new PostgresTransactionBlock(session.Settings.DefaultMode, SqlAccess.Write, isImplicit: true);
        }

        foreach (var command in commands)
        {
            var outcome = await GuardAsync(command, [], describedColumns: null).ConfigureAwait(false);
            var failure = outcome switch
            {
                PgResult result => WriteResult(result, [], describe: true, columns: null),
                PgError error => error,
            };
            if (failure is not null)
            {
                if (implicitBlock)
                {
                    await EndImplicitAsync(commit: false).ConfigureAwait(false);
                }

                return failure;
            }

            if (Writer.ShouldFlush && !await Writer.FlushAsync(closed).ConfigureAwait(false))
            {
                return new PgError("08006", "the client stopped reading results", Fatal: true);
            }
        }

        return implicitBlock ? await EndImplicitAsync(commit: true).ConfigureAwait(false) : null;
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
        return await Writer.FlushAsync(closed).ConfigureAwait(false);
    }

    private async ValueTask<bool> SyncAsync()
    {
        session.SkipUntilSync = false;
        return await ReadyAsync().ConfigureAwait(false);
    }

    private async ValueTask<bool> FunctionCallAsync()
    {
        Writer.Error(PgError.Unsupported("the function call sub-protocol"));
        return await ReadyAsync().ConfigureAwait(false);
    }

    private async ValueTask<bool> FatalAsync(PgError error)
    {
        Writer.Error(error with { Fatal = true });
        await Writer.FlushAsync(closed).ConfigureAwait(false);
        return false;
    }

    private void MarkFailed()
    {
        if (session.Transaction is { Implicit: false } block)
        {
            block.Failed = true;
        }
    }
}
