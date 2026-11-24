using System.Net;
using Microsoft.AspNetCore.Connections;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sessions;

namespace TerraBase.Postgres;

public sealed class PostgresConnectionHandler(
    IPostgresSessionFactory sessions,
    PostgresServerOptions options,
    PostgresBackendRegistry backends,
    TimeProvider timeProvider) : ConnectionHandler
{
    private const int CleartextPassword = 3;
    private const int AuthenticationOk = 0;

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var closed = connection.ConnectionClosed;
        await using var wire = new PgWire(connection.Transport, timeProvider);
        try
        {
            await ServeAsync(wire, (connection.RemoteEndPoint as IPEndPoint)?.Address, closed);
        }
        catch (Exception error) when (error is IOException or ConnectionResetException or ConnectionAbortedException
            || (error is OperationCanceledException && closed.IsCancellationRequested))
        {
        }
        finally
        {
            if (wire.Writer.Aborted)
            {
                connection.Abort();
            }
        }
    }

    private async Task ServeAsync(PgWire wire, IPAddress? client, CancellationToken closed)
    {
        var startup = await NegotiateAsync(wire, closed);
        if (startup is null)
        {
            return;
        }

        if (!backends.TryAdmit())
        {
            await RefuseAsync<object>(wire, new PgError("53300", "sorry, too many clients already", Fatal: true), closed);
            return;
        }

        try
        {
            await AuthenticateAsync(wire, startup, client, closed);
        }
        finally
        {
            backends.Release();
        }
    }

    private async ValueTask<PgStartupMessage?> NegotiateAsync(PgWire wire, CancellationToken closed)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (proceed, startup) = await NegotiateStepAsync(wire, closed);
            if (!proceed || startup is not null)
            {
                return startup;
            }
        }

        await RefuseAsync<object>(wire, PgError.ProtocolViolation("too many encryption negotiation attempts"), closed);
        return null;
    }

    private async ValueTask<(bool Proceed, PgStartupMessage? Startup)> NegotiateStepAsync(PgWire wire, CancellationToken closed)
    {
        var read = await wire.Reader.ReadStartupAsync(PostgresLimits.HandshakeTimeout);
        (PgStartupPacket? Packet, PgError? Error) received = read switch
        {
            PgStartupPacket value => (value, null),
            PgPeerClosed or PgIdleExpired => (null, null),
            PgError error => (null, error),
        };
        if (received.Error is { } refusal)
        {
            await RefuseAsync<object>(wire, refusal, closed);
            return (false, null);
        }

        return received.Packet switch
        {
            null => (false, null),
            PgSslRequest => (await NegotiateTlsAsync(wire, closed), null),
            PgGssEncryptionRequest => (await DeclineAsync(wire, closed), null),
            PgCancelRequest cancel => (Cancel(cancel), null),
            PgStartupMessage message => await AcceptAsync(wire, message, closed) ? (true, message) : (false, null),
        };
    }

    private async ValueTask<bool> NegotiateTlsAsync(PgWire wire, CancellationToken closed)
    {
        if (options.Tls is not { } tls)
        {
            return await DeclineAsync(wire, closed);
        }

        wire.Writer.Raw((byte)'S');
        return await wire.Writer.FlushAsync(closed) && await wire.UpgradeAsync(tls, closed);
    }

    private static async ValueTask<bool> DeclineAsync(PgWire wire, CancellationToken closed)
    {
        wire.Writer.Raw((byte)'N');
        return await wire.Writer.FlushAsync(closed);
    }

    private bool Cancel(PgCancelRequest cancel)
    {
        backends.Cancel(cancel.ProcessId, cancel.SecretKey);
        return false;
    }

    private static async ValueTask<bool> AcceptAsync(PgWire wire, PgStartupMessage message, CancellationToken closed)
    {
        if (message.ProtocolVersion != PgFrameReader.ProtocolVersion3)
        {
            wire.Writer.NegotiateProtocolVersion(PgFrameReader.ProtocolVersion3,
                [.. message.Parameters.Keys.Where(static key => key.StartsWith("_pq_.", StringComparison.Ordinal))]);
        }

        if (!message.Parameters.TryGetValue("user", out var user) || user.Length == 0)
        {
            await RefuseAsync<object>(wire, new PgError("28000", "no PostgreSQL user name specified in startup packet", Fatal: true), closed);
            return false;
        }

        return true;
    }

    private async Task AuthenticateAsync(PgWire wire, PgStartupMessage message, IPAddress? client, CancellationToken closed)
    {
        var user = message.Parameters["user"];
        var database = message.Parameters.TryGetValue("database", out var requested) && requested.Length > 0 ? requested : user;
        var slash = database.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == database.Length - 1)
        {
            await RefuseAsync<object>(wire, new PgError("3D000", $"database \"{database}\" does not exist", Fatal: true, Hint: "connect to <project>/<database>"), closed);
            return;
        }

        wire.Writer.Authentication(CleartextPassword);
        if (!await wire.Writer.FlushAsync(closed))
        {
            return;
        }

        var read = await wire.Reader.ReadMessageAsync(PostgresLimits.HandshakeTimeout, PostgresLimits.HandshakeTimeout, closed);
        var password = read switch
        {
            PgFrontendMessage frontend => frontend switch
            {
                PgPasswordMessage passwordMessage => passwordMessage.Password,
                PgQueryMessage or PgParseMessage or PgBindMessage or PgDescribeMessage or PgExecuteMessage or PgCloseMessage or PgSyncMessage or PgFlushMessage
                    or PgTerminateMessage or PgCopyDataMessage or PgCopyDoneMessage or PgCopyFailMessage or PgFunctionCallMessage or PgUnknownMessage => null,
            },
            PgPeerClosed or PgIdleExpired or PgIncompleteExpired or PgReadInterrupted or PgError => null,
        };
        if (password is null)
        {
            await RefuseAsync<object>(wire, PgError.ProtocolViolation("expected a password response"), closed);
            return;
        }

        var applicationName = message.Parameters.TryGetValue("application_name", out var application) ? application : string.Empty;
        var startup = new PostgresStartup(user, database[..slash], database[(slash + 1)..], password, applicationName, client, message.Parameters);
        var authenticated = await sessions.AuthenticateAsync(startup, closed);
        var grant = authenticated switch
        {
            PostgresGrant value => value,
            PostgresPasswordRejected => await RefuseAsync<PostgresGrant>(wire, new PgError("28P01", $"password authentication failed for user \"{user}\"", Fatal: true), closed),
            PostgresAccessDenied denied => await RefuseAsync<PostgresGrant>(wire, new PgError("28000", denied.Message, Fatal: true), closed),
            PostgresSessionLimitReached limit => await RefuseAsync<PostgresGrant>(wire, new PgError("53300", limit.Message, Fatal: true), closed),
            PostgresBackendUnavailable unavailable => await RefuseAsync<PostgresGrant>(wire, new PgError("57P03", unavailable.Message, Fatal: true), closed),
        };
        if (grant is null)
        {
            return;
        }

        await using (grant.Lease)
        {
            var session = new PostgresSession(grant, startup, timeProvider);
            session.ProcessId = backends.Allocate(session);
            try
            {
                wire.Writer.Authentication(AuthenticationOk);
                foreach (var (name, value) in session.Settings.Reported)
                {
                    wire.Writer.ParameterStatus(name, value);
                }

                wire.Writer.BackendKeyData(session.ProcessId, session.SecretKey);
                wire.Writer.ReadyForQuery(session.Status);
                if (await wire.Writer.FlushAsync(closed))
                {
                    await new PostgresConnection(session, wire, closed).RunAsync();
                }
            }
            finally
            {
                backends.Remove(session.ProcessId);
                await PostgresConnection.ReleaseAsync(session);
            }
        }
    }

    private static async ValueTask<T?> RefuseAsync<T>(PgWire wire, PgError error, CancellationToken closed)
        where T : class
    {
        wire.Writer.Error(error with { Fatal = true });
        await wire.Writer.FlushAsync(closed);
        return null;
    }
}
