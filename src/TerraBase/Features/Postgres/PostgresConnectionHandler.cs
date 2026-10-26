using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Connections;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Postgres;

/// <summary>
/// PostgreSQL wire protocol 3.0 on the postgres listener: plaintext behind the platform's TLS edge, a dashboard API key or a TerraBase API key as
/// the password, and one <see cref="ISqlSession"/> per connection under the tenant's QoS admission. A connection from the
/// edge may open with a PROXY protocol v2 header naming the client, whose address API key IP allowlists are checked against.
/// </summary>
internal sealed class PostgresConnectionHandler(IServiceProvider services) : ConnectionHandler
{
    private const int Protocol30 = 196608, CancelCode = 80877102, SslCode = 80877103, GssCode = 80877104;
    private const int MaxStartupBytes = 10_000, MaxMessageBytes = 1024 * 1024, MaxNamed = 256;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly PgError Denied = new("28P01", "password authentication failed");

    // PROXY protocol v2 (HAProxy's proxy-protocol.txt §2.2): the signature, then version/command, family and length.
    private static ReadOnlySpan<byte> ProxySignature => [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    // Sessions by process id, for CancelRequest; one handler serves the whole listener.
    private readonly ConcurrentDictionary<int, Session> _sessions = new();

    private static readonly ReadOnlyDictionary<string, SqlValue> NoNamed = ReadOnlyDictionary<string, SqlValue>.Empty;

    private readonly union Access(Authorized, PgError);

    // Resolved per use, not when Kestrel starts: opening the engine there would bypass Program's startup error reporting.
    private IApiKeyStore Keys => services.GetRequiredService<IApiKeyStore>();

    private ITenantDirectory Tenants => services.GetRequiredService<ITenantDirectory>();

    private IMemoryBudget Memory => services.GetRequiredService<IMemoryBudget>();

    /// <inheritdoc />
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var input = connection.Transport.Input;
        var output = new PgWriter(connection.Transport.Output);
        Session? session = null;
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed))
            {
                handshake.CancelAfter(HandshakeTimeout);
                var peer = (connection.RemoteEndPoint as IPEndPoint)?.Address;
                var client = IpAllowlists.IsTrustedPeer(peer) ? await ReadProxyHeaderAsync(input, peer, handshake.Token).ConfigureAwait(false) : peer;
                session = await StartAsync(input, output, client, handshake.Token).ConfigureAwait(false);
            }

            if (session is not null)
            {
                await ServeAsync(session, input, output, connection.ConnectionClosed).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is InvalidDataException or QosMemoryException)
        {
            // Framing errors leave the stream unreadable and memory refusals leave a body unread: both end the session.
            output.Error(error is QosMemoryException ? "53200" : "08P01", error.Message, fatal: true);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            // Closed by the client, the shutdown drain or the handshake timeout.
        }
        finally
        {
            if (session is not null)
            {
                _sessions.TryRemove(session.ProcessId, out _);
                await session.Sql.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // A startup packet's length never begins with 0x0D, so a connection without the header is read unchanged.
    private static async ValueTask<IPAddress?> ReadProxyHeaderAsync(PipeReader input, IPAddress? peer, CancellationToken cancellationToken)
    {
        var read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
        var first = read.Buffer.IsEmpty ? (byte)0 : read.Buffer.FirstSpan[0];
        input.AdvanceTo(read.Buffer.Start);
        if (first != ProxySignature[0])
        {
            return peer;
        }

        var header = await ReadAsync(input, 16, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14));
        if (!header.AsSpan(0, 12).SequenceEqual(ProxySignature) || header[12] >> 4 != 2 || length > 512)
        {
            throw new InvalidDataException("invalid PROXY protocol header");
        }

        var addresses = await ReadAsync(input, length, cancellationToken).ConfigureAwait(false);
        // LOCAL (the edge's own health check) and families other than TCP over IPv4 or IPv6 keep the socket peer.
        return (header[12] & 0x0F, header[13]) switch
        {
            (1, 0x11) when length >= 12 => new IPAddress(addresses.AsSpan(0, 4)),
            (1, 0x21) when length >= 36 => new IPAddress(addresses.AsSpan(0, 16)),
            _ => peer,
        };
    }

    private async ValueTask<Session?> StartAsync(PipeReader input, PgWriter output, IPAddress? client, CancellationToken cancellationToken)
    {
        for (var negotiated = 0; ; negotiated++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(await ReadAsync(input, 4, cancellationToken).ConfigureAwait(false));
            if (length is < 8 or > MaxStartupBytes)
            {
                throw new InvalidDataException("invalid startup packet length");
            }

            var packet = new PgReader(await ReadAsync(input, length - 4, cancellationToken).ConfigureAwait(false));
            var code = packet.Int32();
            if (code is SslCode or GssCode && negotiated < 2)
            {
                // TLS ends at the platform edge: this listener speaks plaintext, and the client continues on this connection.
                output.Raw((byte)'N');
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (code == CancelCode)
            {
                var (processId, secret) = (packet.Int32(), packet.Int32());
                if (_sessions.TryGetValue(processId, out var target) && target.Secret == secret)
                {
                    target.Cancel();
                }

                return null;
            }

            if (code >> 16 != 3)
            {
                output.Error("0A000", "unsupported frontend protocol: TerraBase speaks protocol 3.0", fatal: true);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var name = packet.CString(); name.Length > 0; name = packet.CString())
            {
                parameters[name] = packet.CString();
            }

            if (code != Protocol30)
            {
                var unknown = parameters.Keys.Where(name => name.StartsWith("_pq_.", StringComparison.Ordinal)).ToList();
                output.Begin('v');
                output.Int32(Protocol30);
                output.Int32(unknown.Count);
                unknown.ForEach(output.CString);
                output.End();
            }

            return await AuthenticateAsync(input, output, parameters, client, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<Session?> AuthenticateAsync(PipeReader input, PgWriter output, Dictionary<string, string> parameters, IPAddress? client, CancellationToken cancellationToken)
    {
        output.Begin('R');
        output.Int32(3); // AuthenticationCleartextPassword: the password is an API key.
        output.End();
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var (type, body, reservation) = await ReadMessageAsync(input, cancellationToken).ConfigureAwait(false);
        using (reservation)
        {
            if (type != 'p')
            {
                throw new InvalidDataException("expected a password message");
            }

            var secret = new PgReader(body).CString();
            var database = parameters.GetValueOrDefault("database") ?? parameters.GetValueOrDefault("user") ?? "";
            var access = await AuthorizeAsync(secret, database, client, cancellationToken).ConfigureAwait(false);
            if (access is PgError error)
            {
                output.Error(error.Code, error.Message, fatal: true);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var authorized = access is Authorized granted ? granted : throw new InvalidOperationException();
            var sql = services.GetRequiredService<ISqlService>().Open(authorized.Database, authorized.CanWrite);
            Session session;
            do
            {
                session = new Session(RandomNumberGenerator.GetInt32(1, int.MaxValue), RandomNumberGenerator.GetInt32(int.MaxValue), secret, database,
                    parameters.GetValueOrDefault("user") ?? "", client, sql, PostgresStatements.Settings(parameters.GetValueOrDefault("application_name") ?? ""));
            }
            while (!_sessions.TryAdd(session.ProcessId, session));

            try
            {
                await GreetAsync(session, output, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _sessions.TryRemove(session.ProcessId, out _);
                await sql.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return session;
        }
    }

    private static async ValueTask GreetAsync(Session session, PgWriter output, CancellationToken cancellationToken)
    {
        output.Begin('R');
        output.Int32(0);
        output.End();
        foreach (var name in PostgresStatements.Reported)
        {
            output.Begin('S');
            output.CString(name);
            output.CString(session.Settings[name]);
            output.End();
        }

        output.Begin('K');
        output.Int32(session.ProcessId);
        output.Int32(session.Secret);
        output.End();
        output.Ready(session.Status);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // Every statement re-checks the key and database, like every HTTP request: revocation and deletion apply at once.
    // `database` is a name in the key's project, or project/name for a key that reaches the whole organization.
    private async ValueTask<Access> AuthorizeAsync(string secret, string database, IPAddress? client, CancellationToken cancellationToken)
    {
        if (!services.GetRequiredService<TerraBase.Features.Health.IPeerTopology>().IsCurrent)
        {
            return new PgError("57P03", "this machine's peer topology is not verified; retry");
        }

        var slash = database.IndexOf('/', StringComparison.Ordinal);
        if (TsAuthKeyExchange.IsKey(secret))
        {
            return await AuthorizeDashboardKeyAsync(secret, database, slash, client, cancellationToken).ConfigureAwait(false);
        }

        if (await Keys.FindAsync(secret, cancellationToken).ConfigureAwait(false) is not ApiKey key
            || !key.Scopes.Contains(ApiKeyScopes.DocumentsRead)
            || (slash < 0 ? key.ProjectId : database[..slash]) is not { } project
            || !EntityIds.IsValid(project)
            || !DatabaseName.TryParse(database[(slash + 1)..], out var name))
        {
            return Denied;
        }

        return await Tenants.FindDatabaseAsync(key.OrganizationId, project, name.Value, cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext tenant when key.Reaches(tenant) => new Authorized(tenant, key.Scopes.Contains(ApiKeyScopes.DocumentsWrite)),
            Unavailable => new PgError("57P03", "the database is unavailable; retry"),
            _ => Denied,
        };
    }

    // A dashboard API key, as ts-auth issues it: its access token names the project, an optional database and the scopes,
    // and the project resolves to its storage namespace like a bearer token's on the client listener.
    private async ValueTask<Access> AuthorizeDashboardKeyAsync(string secret, string database, int slash, IPAddress? client, CancellationToken cancellationToken)
    {
        var grant = await services.GetRequiredService<TsAuthKeyExchange>().ExchangeAsync(secret, cancellationToken).ConfigureAwait(false);
        if (grant is Unavailable)
        {
            return new PgError("57P03", "authentication is unavailable; retry");
        }

        if (grant is not TsAuthKeyGrant { } key
            || !key.Scopes.Contains(ApiKeyScopes.DocumentsRead)
            || (slash >= 0 && !string.Equals(database[..slash], key.ProjectId, StringComparison.Ordinal))
            || !DatabaseName.TryParse(database[(slash + 1)..], out var name)
            || (key.Database is not null && !string.Equals(key.Database, name.Value, StringComparison.Ordinal)))
        {
            return Denied;
        }

        if (!IpAllowlists.Admits(key.Allowlists, client))
        {
            return new PgError("28000", "ip_not_allowed: " + IpAllowlists.Refusal(client));
        }

        var organization = await services.GetRequiredService<IProjectCatalog>()
            .ResolveProjectAsync(key.ProjectId, key.Subject, createIn: null, cancellationToken).ConfigureAwait(false);
        if (organization is Unavailable)
        {
            return new PgError("57P03", "the database is unavailable; retry");
        }

        return organization is string found
            ? await Tenants.FindDatabaseAsync(found, key.ProjectId, name.Value, cancellationToken).ConfigureAwait(false) switch
            {
                TenantContext tenant => new Authorized(tenant, key.Scopes.Contains(ApiKeyScopes.DocumentsWrite)),
                Unavailable => new PgError("57P03", "the database is unavailable; retry"),
                _ => Denied,
            }
            : Denied;
    }

    private async Task ServeAsync(Session session, PipeReader input, PgWriter output, CancellationToken cancellationToken)
    {
        // After an error in the extended protocol, messages are discarded until Sync.
        var failed = false;
        while (true)
        {
            var (type, body, reservation) = await ReadMessageAsync(input, cancellationToken).ConfigureAwait(false);
            using (reservation)
            {
                if (type == 'X')
                {
                    return;
                }

                if (!services.GetRequiredService<TerraBase.Features.Health.IPeerTopology>().IsCurrent)
                {
                    output.Error("57P03", "this machine's peer topology is not verified; retry", fatal: true);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var message = new PgReader(body);
                switch ((char)type)
                {
                    case 'Q':
                        if (await QueryAsync(session, message.CString(), output, cancellationToken).ConfigureAwait(false) is { } error)
                        {
                            Fail(session, output, error);
                        }

                        await SyncAsync(session, output, cancellationToken).ConfigureAwait(false);
                        failed = false;
                        break;
                    case 'S':
                        await SyncAsync(session, output, cancellationToken).ConfigureAwait(false);
                        failed = false;
                        break;
                    case 'H':
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case 'P' or 'B' or 'D' or 'E' or 'C' when !failed:
                        if (await ExtendedAsync(session, (char)type, message, output, cancellationToken).ConfigureAwait(false) is { } refused)
                        {
                            Fail(session, output, refused);
                            failed = true;
                        }

                        break;
                    case 'P' or 'B' or 'D' or 'E' or 'C':
                        break;
                    default:
                        throw new InvalidDataException("unsupported message type");
                }
            }
        }
    }

    // An error inside a transaction aborts it, as in PostgreSQL: only ROLLBACK or COMMIT (which rolls back) run next.
    private static void Fail(Session session, PgWriter output, PgError error)
    {
        output.Error(error.Code, error.Message);
        session.Failed = !session.Sql.IsAutocommit;
    }

    private static async ValueTask SyncAsync(Session session, PgWriter output, CancellationToken cancellationToken)
    {
        await session.Sql.EndRequestAsync().ConfigureAwait(false);
        output.Ready(session.Status);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // A simple query runs its statements in order and stops at the first error.
    private async ValueTask<PgError?> QueryAsync(Session session, string sql, PgWriter output, CancellationToken cancellationToken)
    {
        if (PgCatalog.IsTypeLoad(sql))
        {
            Array.ForEach(PgCatalog.TypeLoad(), result => Write(output, result, [], describe: true));
            return null;
        }

        switch (PostgresStatements.Parse(sql))
        {
            case PgError error:
                return error;
            case PgStatement[] { Length: 0 }:
                output.Empty('I');
                return null;
            case PgStatement[] statements:
                foreach (var statement in statements)
                {
                    switch (await RunAsync(session, statement, [], cancellationToken).ConfigureAwait(false))
                    {
                        case PgError error:
                            return error;
                        case PgResult[] results:
                            Array.ForEach(results, result => Write(output, result, [], describe: true));
                            break;
                    }
                }

                return null;
        }

        return null;
    }

    private async ValueTask<PgError?> ExtendedAsync(Session session, char type, PgReader message, PgWriter output, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case 'P':
                var (statementName, sql) = (message.CString(), message.CString());
                var oids = Enumerable.Range(0, message.Count()).Select(_ => message.Int32()).ToList();
                var parse = PostgresStatements.Parse(sql);
                if (parse is PgError parseError)
                {
                    return parseError;
                }

                if (parse is not PgStatement[] { Length: <= 1 } parsed)
                {
                    return new PgError("42601", "cannot insert multiple commands into a prepared statement");
                }

                if (statementName.Length > 0 && session.Statements.ContainsKey(statementName))
                {
                    return new PgError("42P05", "prepared statement already exists");
                }

                if (session.Statements.Count >= MaxNamed && !session.Statements.ContainsKey(statementName))
                {
                    return new PgError("54000", "at most 256 prepared statements per session");
                }

                var parsedStatement = parsed is [var single] ? single : new PgStatement(PgKind.Empty, "", "", 0);
                oids.AddRange(Enumerable.Repeat(0, Math.Max(0, parsedStatement.Parameters - oids.Count)));
                session.Statements[statementName] = new Prepared(parsedStatement, [.. oids]);
                output.Empty('1');
                return null;
            case 'B':
                var (portalName, boundName) = (message.CString(), message.CString());
                var formats = Enumerable.Range(0, message.Count()).Select(_ => message.Int16()).ToArray();
                var values = Enumerable.Range(0, message.Count()).Select(_ => message.Bytes()).ToArray();
                var resultFormats = Enumerable.Range(0, message.Count()).Select(_ => message.Int16()).ToArray();
                if (!session.Statements.TryGetValue(boundName, out var bound))
                {
                    return new PgError("26000", "prepared statement does not exist");
                }

                if (values.Length != bound.Oids.Length || formats.Length > 1 && formats.Length != values.Length)
                {
                    return new PgError("08P01", $"bind message supplies {values.Length} parameters, but prepared statement requires {bound.Oids.Length}");
                }

                var args = new SqlValue[values.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    if (PostgresStatements.Decode(bound.Oids[i], formats.Length == 0 ? 0 : formats[Math.Min(i, formats.Length - 1)], values[i]) is not { } value)
                    {
                        return new PgError("22P02", $"invalid value for parameter ${i + 1}, or an unsupported binary type");
                    }

                    args[i] = value;
                }

                if (session.Portals.Count >= MaxNamed && !session.Portals.ContainsKey(portalName))
                {
                    return new PgError("54000", "at most 256 portals per session");
                }

                session.Portals[portalName] = new Portal(bound.Statement, args, resultFormats) { Columns = bound.Columns, Described = bound.Described };
                output.Empty('2');
                return null;
            case 'D':
                var (kind, described) = (message.Byte(), message.CString());
                if (kind == 'S' && session.Statements.TryGetValue(described, out var statement))
                {
                    var (columns, _, error) = await DescribeAsync(session, statement.Statement, cancellationToken).ConfigureAwait(false);
                    if (error is not null)
                    {
                        return error;
                    }

                    (statement.Columns, statement.Described) = (columns, true);
                    if (statement.Statement.Kind == PgKind.Sql && Array.Exists(statement.Oids, oid => oid == 0))
                    {
                        // Drivers that prepare (pgx) encode each parameter in the type this reports.
                        _ = await GuardAsync(session, async (database, token) =>
                        {
                            PostgresStatements.InferParameters(statement.Statement, statement.Oids,
                                await services.GetRequiredService<ISqlService>().ListTablesAsync(database, token).ConfigureAwait(false));
                            return Array.Empty<PgResult>();
                        }, cancellationToken).ConfigureAwait(false);
                    }

                    output.Begin('t');
                    output.Int16(statement.Oids.Length);
                    Array.ForEach(statement.Oids, oid => output.Int32(oid == 0 ? PostgresStatements.TextOid : oid));
                    output.End();
                    Describe(output, columns, []);
                    return null;
                }

                if (kind == 'P' && session.Portals.TryGetValue(described, out var portal))
                {
                    if (!portal.Described)
                    {
                        var (columns, readOnly, error) = await DescribeAsync(session, portal.Statement, cancellationToken).ConfigureAwait(false);
                        if (error is null && readOnly)
                        {
                            // A read runs now, so its columns carry the types of its values; Execute sends the kept rows.
                            var outcome = await RunAsync(session, portal.Statement, portal.Args, cancellationToken).ConfigureAwait(false);
                            error = outcome is PgError failure ? failure : null;
                            columns = outcome is PgResult[] { Length: > 0 } results ? results[0].Columns : null;
                            portal.Pending = outcome;
                        }

                        if (error is not null)
                        {
                            return error;
                        }

                        (portal.Columns, portal.Described) = (columns, true);
                    }

                    Describe(output, portal.Columns, portal.Formats);
                    return null;
                }

                return new PgError(kind == 'S' ? "26000" : "34000", "prepared statement or portal does not exist");
            case 'E':
                var (executed, maxRows) = (message.CString(), message.Int32());
                if (!session.Portals.TryGetValue(executed, out var running))
                {
                    return new PgError("34000", "portal does not exist");
                }

                if (maxRows > 0)
                {
                    return new PgError("0A000", "Execute with a row limit is not supported yet");
                }

                var executedOutcome = running.Pending ?? await RunAsync(session, running.Statement, running.Args, cancellationToken).ConfigureAwait(false);
                running.Pending = null;
                if (executedOutcome is PgError executeError)
                {
                    return executeError;
                }

                foreach (var result in executedOutcome is PgResult[] results ? results : [])
                {
                    if (running.Described && (result.Columns?.Length ?? 0) != (running.Columns?.Length ?? 0))
                    {
                        return new PgError("0A000", "cached plan must not change result type");
                    }

                    if (Write(output, result, running.Formats, describe: false, running.Described ? running.Columns : null) is { } encodeError)
                    {
                        return encodeError;
                    }
                }

                return null;
            default:
                var (closed, closedName) = (message.Byte(), message.CString());
                _ = closed == 'S' ? session.Statements.Remove(closedName) : session.Portals.Remove(closedName);
                output.Empty('3');
                return null;
        }
    }

    // A statement's result columns before it runs, and whether it only reads; SHOW and catalog reads run to find out.
    private async ValueTask<(PgColumn[]? Columns, bool ReadOnly, PgError? Error)> DescribeAsync(Session session, PgStatement statement, CancellationToken cancellationToken)
    {
        switch (statement.Kind)
        {
            case PgKind.Show or PgKind.Catalog:
                var outcome = await RunAsync(session, statement, [], cancellationToken).ConfigureAwait(false);
                return (outcome is PgResult[] { Length: > 0 } results ? results[0].Columns : null, true, outcome is PgError error ? error : null);
            case PgKind.Sql:
                SqlDescription? shape = null;
                var guarded = await GuardAsync(session, async (_, token) =>
                {
                    switch (await session.Sql.DescribeAsync(new SqlCommand(statement.Sql, [], NoNamed), token).ConfigureAwait(false))
                    {
                        case SqlError error:
                            return PostgresStatements.Error(error);
                        case SqlDescription description:
                            shape = description;
                            break;
                    }

                    return Array.Empty<PgResult>();
                }, cancellationToken).ConfigureAwait(false);
                return guarded is PgError failure || shape is null
                    ? (null, false, guarded is PgError refused ? refused : new PgError("XX000", "internal error"))
                    : (shape.Columns.Count == 0 ? null : [.. shape.Columns.Select(column => new PgColumn(column.Name, PostgresStatements.DescribedOid(column)))],
                        shape.IsReadOnly, null);
            default:
                return (null, false, null);
        }
    }

    private async ValueTask<PgOutcome> RunAsync(Session session, PgStatement statement, IReadOnlyList<SqlValue> args, CancellationToken cancellationToken)
    {
        if (session.Failed && statement.Kind is not (PgKind.Commit or PgKind.Rollback or PgKind.Empty))
        {
            return new PgError("25P02", "current transaction is aborted, commands ignored until end of transaction block");
        }

        switch (statement.Kind)
        {
            case PgKind.Empty:
                return new[] { new PgResult(null, [], null) };
            case PgKind.Discard:
                session.Statements.Clear();
                session.Portals.Clear();
                return new[] { new PgResult(null, [], "DISCARD ALL") };
            case PgKind.Deallocate:
                // psycopg deallocates its prepared statements after a rollback.
                if (PostgresStatements.Deallocated(statement.Text) is { } deallocated)
                {
                    session.Statements.Remove(deallocated);
                    return new[] { new PgResult(null, [], "DEALLOCATE") };
                }

                session.Statements.Clear();
                return new[] { new PgResult(null, [], "DEALLOCATE ALL") };
            case PgKind.Set or PgKind.Show:
                return PostgresStatements.RunCommand(statement.Kind, statement.Text, session.Settings);
            case PgKind.Catalog when PgCatalog.Functions(statement.Text, session.Database, session.User) is { } functions:
                return new[] { functions };
        }

        return await GuardAsync(session, async (database, token) => statement.Kind switch
        {
            PgKind.Catalog => PgCatalog.Answer(statement.Text, session.Database, session.User,
                await services.GetRequiredService<ISqlService>().ListTablesAsync(database, token).ConfigureAwait(false)),
            PgKind.Begin or PgKind.Commit or PgKind.Rollback => await TransactionAsync(session, statement.Kind, token).ConfigureAwait(false),
            _ => await session.Sql.ExecuteAsync(new SqlCommand(statement.Sql, args, NoNamed), token).ConfigureAwait(false) switch
            {
                SqlResult result => new[] { PostgresStatements.Result(statement, result) with { ReadCharge = result.ReadCharge } },
                SqlError error => PostgresStatements.Error(error),
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    // PostgreSQL's transaction verbs on the session: BEGIN inside a transaction and COMMIT or ROLLBACK outside one only
    // warn, and COMMIT of an aborted transaction rolls it back.
    private static async ValueTask<PgOutcome> TransactionAsync(Session session, PgKind kind, CancellationToken cancellationToken)
    {
        async ValueTask<PgOutcome> Run(string verb)
        {
            return await session.Sql.ExecuteAsync(new SqlCommand(verb, [], NoNamed), cancellationToken).ConfigureAwait(false) is SqlError error
                ? PostgresStatements.Error(error)
                : new[] { new PgResult(null, [], verb) };
        }

        var verb = kind switch { PgKind.Begin => "BEGIN", PgKind.Commit => "COMMIT", _ => "ROLLBACK" };
        if (session.Sql.IsAutocommit != (kind == PgKind.Begin))
        {
            return new[] { new PgResult(null, [], verb, kind == PgKind.Begin
                ? new PgError("25001", "there is already a transaction in progress")
                : new PgError("25P01", "there is no transaction in progress")) };
        }

        if (kind == PgKind.Begin)
        {
            return await Run("BEGIN").ConfigureAwait(false);
        }

        var aborted = session.Failed;
        session.Failed = false;
        var outcome = await Run(aborted ? "ROLLBACK" : verb).ConfigureAwait(false);
        if (outcome is PgError && !session.Sql.IsAutocommit)
        {
            // A failed COMMIT ends the transaction, as in PostgreSQL.
            _ = await Run("ROLLBACK").ConfigureAwait(false);
        }

        return outcome;
    }

    // Every statement that reaches the database re-checks the key and runs under the HTTP pipeline's admission.
    private async ValueTask<PgOutcome> GuardAsync(Session session, Func<TenantContext, CancellationToken, ValueTask<PgOutcome>> run, CancellationToken cancellationToken)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        session.Track(running);
        try
        {
            if (await AuthorizeAsync(session.ApiKey, session.Database, session.Client, running.Token).ConfigureAwait(false) is not Authorized { Database: var database })
            {
                return Denied;
            }

            // The HTTP pipeline's admission (QosExtensions): one organization lease, then the tenant's memory and budgets.
            var tenant = new TenantContext(database.OrganizationId, database.Plan);
            using var lease = services.GetRequiredService<IQosAdmission>().TryEnter(tenant, out var retryAfter);
            if (lease is null)
            {
                return new PgError("53300", $"tenant busy, retry after {retryAfter}s");
            }

            using var scope = QosContext.Enter(tenant, Memory);
            return await run(database, running.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new PgError("57014", "canceling statement due to user request");
        }
        catch (QosMemoryException)
        {
            return new PgError("53200", "organization memory budget exceeded");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            services.GetRequiredService<ILogger<PostgresConnectionHandler>>().ZLogError(error, $"A PostgreSQL wire statement failed");
            return new PgError("XX000", "internal error");
        }
        finally
        {
            session.Track(null);
        }
    }

    private static short Format(short[] formats, int index) => formats.Length == 0 ? (short)0 : formats[Math.Min(index, formats.Length - 1)];

    private static void Describe(PgWriter output, PgColumn[]? columns, short[] formats)
    {
        if (columns is null)
        {
            output.Empty('n');
            return;
        }

        output.Begin('T');
        output.Int16(columns.Length);
        for (var i = 0; i < columns.Length; i++)
        {
            output.CString(columns[i].Name);
            output.Int32(0);
            output.Int16(0);
            output.Int32(columns[i].Oid);
            output.Int16(-1);
            output.Int32(-1);
            output.Int16(Format(formats, i) == 1 && PostgresStatements.Binary(columns[i].Oid) ? 1 : 0);
        }

        output.End();
    }

    // Rows in the described types; an error when a value does not fit its column's binary type.
    private static PgError? Write(PgWriter output, PgResult result, short[] formats, bool describe, PgColumn[]? columns = null)
    {
        if (result.Tag is null)
        {
            output.Empty('I');
            return null;
        }

        columns ??= result.Columns;
        if (describe && columns is not null)
        {
            Describe(output, columns, formats);
        }

        foreach (var row in result.Rows)
        {
            output.Begin('D');
            output.Int16(row.Count);
            for (var i = 0; i < row.Count; i++)
            {
                var oid = columns is not null && i < columns.Length ? columns[i].Oid : PostgresStatements.TextOid;
                if (!PostgresStatements.TryEncode(row[i], oid, Format(formats, i) == 1 && PostgresStatements.Binary(oid), out var bytes))
                {
                    return new PgError(row[i].Kind == SqlValueKind.Integer ? "22003" : "22P02", $"a value of column {i + 1} does not fit its type");
                }

                output.Value(bytes);
            }

            output.End();
        }

        if (result.Notice is not null)
        {
            output.Notice(result.Notice);
        }

        output.Begin('C');
        output.CString(result.Tag);
        output.End();
        result.ReadCharge?.Accept();
        return null;
    }

    private async ValueTask<(byte Type, byte[] Body, IMemoryReservation Reservation)> ReadMessageAsync(PipeReader input, CancellationToken cancellationToken)
    {
        var header = await ReadAsync(input, 5, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length is < 4 or > MaxMessageBytes)
        {
            // Before allocating: the unread body cannot be skipped safely.
            throw new InvalidDataException("message length out of bounds");
        }

        var reservation = Memory.TryReserve(MemoryLane.Foreground, length) ?? throw new QosMemoryException();
        try
        {
            return (header[0], await ReadAsync(input, length - 4, cancellationToken).ConfigureAwait(false), reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private static async ValueTask<byte[]> ReadAsync(PipeReader input, int count, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return [];
        }

        var read = await input.ReadAtLeastAsync(count, cancellationToken).ConfigureAwait(false);
        if (read.Buffer.Length < count)
        {
            input.AdvanceTo(read.Buffer.Start, read.Buffer.End);
            throw new EndOfStreamException();
        }

        var bytes = read.Buffer.Slice(0, count).ToArray();
        input.AdvanceTo(read.Buffer.GetPosition(count));
        return bytes;
    }

    private sealed record Authorized(TenantContext Database, bool CanWrite);

    // Columns are fixed once described, so later executions send the types the client was told.
    private sealed class Prepared(PgStatement statement, int[] oids)
    {
        public PgStatement Statement => statement;

        public int[] Oids => oids;

        public PgColumn[]? Columns { get; set; }

        public bool Described { get; set; }
    }

    private sealed class Portal(PgStatement statement, SqlValue[] args, short[] formats)
    {
        public PgStatement Statement => statement;

        public SqlValue[] Args => args;

        public short[] Formats => formats;

        public PgColumn[]? Columns { get; set; }

        public bool Described { get; set; }

        // The outcome of a read run at Describe, sent by the next Execute.
        public PgOutcome? Pending { get; set; }
    }

    // The running statement's cancellation, swapped under a lock so CancelRequest never cancels a disposed source.
    private sealed record Session(int ProcessId, int Secret, string ApiKey, string Database, string User, IPAddress? Client, ISqlSession Sql, Dictionary<string, string> Settings)
    {
        private readonly Lock _gate = new();
        private CancellationTokenSource? _running;

        public Dictionary<string, Prepared> Statements { get; } = [];

        public Dictionary<string, Portal> Portals { get; } = [];

        // Whether an error aborted the open transaction.
        public bool Failed { get; set; }

        // The ReadyForQuery status: idle, in a transaction, or in an aborted transaction.
        public char Status => Sql.IsAutocommit ? 'I' : Failed ? 'E' : 'T';

        public void Track(CancellationTokenSource? running)
        {
            lock (_gate)
            {
                _running = running;
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _running?.Cancel();
            }
        }
    }

    // Reads a frontend message body; running past its end is a framing error.
    private sealed class PgReader(byte[] data)
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private int _position;

        public byte Byte() => Take(1)[0];

        public short Int16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));

        public int Int32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

        public int Count() => Int16() is >= 0 and var count ? count : throw new InvalidDataException("negative count");

        public byte[]? Bytes() => Int32() is var length and >= 0 ? Take(length).ToArray() : null;

        public string CString()
        {
            var end = Array.IndexOf(data, (byte)0, _position);
            if (end < 0)
            {
                throw new InvalidDataException("unterminated string");
            }

            try
            {
                return Utf8.GetString(Take(end - _position + 1)[..^1]);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("invalid UTF-8");
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - _position)
            {
                throw new InvalidDataException("message shorter than its contents");
            }

            _position += count;
            return data.AsSpan(_position - count, count);
        }
    }

    // Builds backend messages: type, big-endian length, body.
    private sealed class PgWriter(PipeWriter pipe)
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly ArrayBufferWriter<byte> _body = new();
        private byte _type;

        public void Begin(char type)
        {
            _type = (byte)type;
            _body.ResetWrittenCount();
        }

        public void Int16(int value)
        {
            BinaryPrimitives.WriteInt16BigEndian(_body.GetSpan(2), (short)value);
            _body.Advance(2);
        }

        public void Int32(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(_body.GetSpan(4), value);
            _body.Advance(4);
        }

        public void CString(string value)
        {
            Utf8.GetBytes(value, _body);
            _body.Write([(byte)0]);
        }

        public void Value(byte[]? value)
        {
            Int32(value?.Length ?? -1);
            _body.Write(value);
        }

        public void End()
        {
            var length = _body.WrittenCount;
            var span = pipe.GetSpan(length + 5);
            span[0] = _type;
            BinaryPrimitives.WriteInt32BigEndian(span[1..], length + 4);
            _body.WrittenSpan.CopyTo(span[5..]);
            pipe.Advance(length + 5);
        }

        public void Empty(char type)
        {
            Begin(type);
            End();
        }

        public void Raw(byte value)
        {
            pipe.GetSpan(1)[0] = value;
            pipe.Advance(1);
        }

        public void Ready(char status)
        {
            Begin('Z');
            _body.Write([(byte)status]);
            End();
        }

        public void Error(string code, string message, bool fatal = false) => Fields('E', fatal ? "FATAL" : "ERROR", code, message);

        public void Notice(PgError notice) => Fields('N', "WARNING", notice.Code, notice.Message);

        public ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken) => pipe.FlushAsync(cancellationToken);

        private void Fields(char type, string severity, string code, string message)
        {
            Begin(type);
            foreach (var (field, value) in new[] { ('S', severity), ('V', severity), ('C', code), ('M', message) })
            {
                _body.Write([(byte)field]);
                CString(value);
            }

            _body.Write([(byte)0]);
            End();
        }
    }
}
