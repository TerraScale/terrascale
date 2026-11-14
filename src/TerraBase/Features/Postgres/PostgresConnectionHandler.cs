using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;
using TerraBase.Configuration;
using TerraBase.Features.Catalog;
using TerraBase.Features.Sql.Routing;

namespace TerraBase.Features.Postgres;

internal sealed class PostgresConnectionHandler(IServiceProvider services) : ConnectionHandler
{
    private const int CancelCode = 80877102, SslCode = 80877103, GssCode = 80877104;
    private const int CleartextPassword = 3;
    private const int MaxStartupBytes = 10_000, MaxMessageBytes = 1024 * 1024, MaxNamed = 256;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly PgError Denied = new("28P01", "password authentication failed");
    private static ReadOnlySpan<byte> ProxySignature => [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];
    private readonly ConcurrentDictionary<int, Session> _sessions = new();

    private static readonly ReadOnlyDictionary<string, SqlValue> NoNamed = ReadOnlyDictionary<string, SqlValue>.Empty;

    private readonly union Access(Authorized, RemoteAuthorized, PgError);

    private sealed record RemoteAuthorized(TenantContext Database);

    private sealed record Relay(HomeNode Home, TenantContext Tenant, byte[] Startup, IPAddress? Client, IDisposable Admission, ProxyRequestBudget Budget);
    private IApiKeyStore Keys => services.GetRequiredService<IApiKeyStore>();

    private ITenantDirectory Tenants => services.GetRequiredService<ITenantDirectory>();

    private IMemoryBudget Memory => services.GetRequiredService<IMemoryBudget>();

    private TimeProvider Time => services.GetRequiredService<TimeProvider>();
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var peer = (connection.RemoteEndPoint as IPEndPoint)?.Address;
        var output = new SessionOutput(connection.Transport.Output, connection.Features.GetRequiredFeature<IConnectionSocketFeature>().Socket, Time);
        await ServeConnectionAsync(connection.Transport.Input, output, peer, IpAllowlists.IsEdge(peer), relayed: false, new ProxyRequestBudget(Time, HandshakeTimeout),
            () => connection.Abort(new ConnectionAbortedException("the API key was refused")), connection.ConnectionClosed).ConfigureAwait(false);
    }

    internal Task ServeRelayedAsync(PipeReader input, PipeWriter output, IPAddress client, ProxyRequestBudget budget, Action abort, CancellationToken closed) =>
        ServeConnectionAsync(input, output, client, edge: true, relayed: true, budget, abort, closed);

    private async Task ServeConnectionAsync(PipeReader input, PipeWriter pipe, IPAddress? peer, bool edge, bool relayed, ProxyRequestBudget budget, Action abortConnection, CancellationToken closed)
    {
        var output = new PgWriter(pipe);
        Session? session = null;
        try
        {
            Relay? relay;
            using (var handshake = budget.Link(closed))
            {
                var client = edge && !relayed ? await ReadProxyHeaderAsync(input, peer, handshake.Token).ConfigureAwait(false) : peer;
                using var preauthentication = services.GetRequiredService<QosSessions>().TryPreauthenticate(client, HandshakeTimeout, handshake.Token);
                if (preauthentication is null)
                {
                    output.Error("53300", QosSessions.ConnectionAttemptsMessage, fatal: true);
                    await output.FlushAsync(closed).ConfigureAwait(false);
                    return;
                }

                (session, relay) = await StartAsync(input, output, client, edge, relayed, preauthentication.Token).ConfigureAwait(false);
            }

            if (relay is not null)
            {
                using var lease = services.GetRequiredService<QosSessions>().TryEnter(relay.Tenant, relay: true);
                if (lease is null)
                {
                    relay.Admission.Dispose();
                    output.Error("53300", "organization session limit exceeded", fatal: true);
                    await output.FlushAsync(closed).ConfigureAwait(false);
                    return;
                }

                await services.GetRequiredService<PostgresRelay>().RunAsync(relay.Home, relay.Tenant, relay.Startup, relay.Client, input, pipe, relay.Admission, relay.Budget, closed).ConfigureAwait(false);
            }

            if (session is not null)
            {
                using var refused = new CancellationTokenSource();
                using var abort = refused.Token.Register(abortConnection);
                using var expiry = session.TokenExpiry is { } end
                    ? new CancellationTokenSource(end - Time.GetUtcNow() is { Ticks: > 0 } left ? left : TimeSpan.Zero, Time)
                    : null;
                using var expired = expiry?.Token.Register(abortConnection);
                using var watch = KeyWatch.Start(services.GetRequiredService<IKeyAuthority>(), session.Key is not null, session.Until,
                    async token => await services.GetRequiredService<TsAuthKeyExchange>().ExchangeAsync(session.ApiKey, session.Project, token).ConfigureAwait(false) is TsAuthKeyGrant grant ? grant.Key : null,
                    refused, services.GetRequiredService<TimeProvider>());
                using var moving = CancellationTokenSource.CreateLinkedTokenSource(closed);
                using var moved = services.GetRequiredService<DatabaseFences>().WatchMoved(session.DatabaseId, session.PreviousHome, moving.Cancel);
                try
                {
                    await ServeAsync(session, input, output, relayed, moving.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (moving.IsCancellationRequested && !closed.IsCancellationRequested)
                {
                    output.Error("57P01", "the database writer moved; reconnect", fatal: true);
                    await output.FlushAsync(closed).ConfigureAwait(false);
                }
            }
        }
        catch (StorageCopyFaultedException)
        {
            output.Error("57P03", "copy_faulted: Storage requires recovery.", fatal: true);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (StorageProofPendingException)
        {
            output.Error("57P03", "recovery_pending: Storage recovery proof is pending; retry.", fatal: true);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidDataException or QosMemoryException)
        {
            output.Error(error is QosMemoryException ? "53200" : "08P01", error.Message, fatal: true);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
        }
        finally
        {
            if (session is not null)
            {
                _sessions.TryRemove(session.ProcessId, out _);
                using var lease = session.Lease;
                await session.Sql.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
    private static async ValueTask<IPAddress?> ReadProxyHeaderAsync(PipeReader input, IPAddress? peer, CancellationToken cancellationToken)
    {
        var read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
        var first = read.Buffer.IsEmpty ? (byte)0 : read.Buffer.FirstSpan[0];
        input.AdvanceTo(read.Buffer.Start);
        if (first != ProxySignature[0])
        {
            return peer;
        }

        var header = await ReadAsync(input, 16, null, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14));
        if (!header.AsSpan(0, 12).SequenceEqual(ProxySignature) || header[12] >> 4 != 2 || length > 512)
        {
            throw new InvalidDataException("invalid PROXY protocol header");
        }

        var addresses = await ReadAsync(input, length, null, cancellationToken).ConfigureAwait(false);
        return (header[12] & 0x0F, header[13]) switch
        {
            (1, 0x11) when length >= 12 => new IPAddress(addresses.AsSpan(0, 4)),
            (1, 0x21) when length >= 36 => new IPAddress(addresses.AsSpan(0, 16)),
            _ => peer,
        };
    }

    private async ValueTask<(Session? Session, Relay? Relay)> StartAsync(PipeReader input, PgWriter output, IPAddress? client, bool edge, bool relayed, CancellationToken cancellationToken)
    {
        for (var negotiated = 0; ; negotiated++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(await ReadAsync(input, 4, null, cancellationToken).ConfigureAwait(false));
            if (length is < 8 or > MaxStartupBytes)
            {
                throw new InvalidDataException("invalid startup packet length");
            }

            var packet = new PgReader(await ReadAsync(input, length - 4, null, cancellationToken).ConfigureAwait(false));
            var code = packet.Int32();
            if (code is SslCode or GssCode && negotiated < 2)
            {
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

                return default;
            }

            if (!edge)
            {
                output.Error("28000", "TLS is required: use sslmode=require", fatal: true);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return default;
            }

            if (code >> 16 != 3)
            {
                output.Error("0A000", "unsupported frontend protocol: TerraBase speaks protocol 3.0", fatal: true);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return default;
            }

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var name = packet.CString(); name.Length > 0; name = packet.CString())
            {
                parameters[name] = packet.CString();
            }

            if (code != PostgresStatements.ProtocolVersion)
            {
                var unknown = parameters.Keys.Where(name => name.StartsWith("_pq_.", StringComparison.Ordinal)).ToList();
                output.Begin('v');
                output.Int32(PostgresStatements.ProtocolVersion);
                output.Int32(unknown.Count);
                unknown.ForEach(output.CString);
                output.End();
            }

            return await AuthenticateAsync(input, output, parameters, client, relayed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<(Session? Session, Relay? Relay)> AuthenticateAsync(PipeReader input, PgWriter output, Dictionary<string, string> parameters, IPAddress? client, bool relayed, CancellationToken cancellationToken)
    {
        output.Begin('R');
        output.Int32(CleartextPassword);
        output.End();
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var (type, body, reservation) = await ReadMessageAsync(input, null, cancellationToken).ConfigureAwait(false);
        using (reservation)
        {
            if (type != 'p')
            {
                throw new InvalidDataException("expected a password message");
            }

            var secret = new PgReader(body).CString();
            var database = parameters.GetValueOrDefault("database") ?? parameters.GetValueOrDefault("user") ?? "";
            var access = await AuthorizeAsync(secret, database, client, cancellationToken).ConfigureAwait(false);
            switch (access)
            {
                case PgError error:
                    output.Error(error.Code, error.Message, fatal: true);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return default;
                case RemoteAuthorized remote:
                    return await RelayAsync(remote.Database, parameters, secret, client, relayed, output, cancellationToken).ConfigureAwait(false);
                case Authorized authorized:
                    if (await AwayAsync(authorized.Database, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        return await RelayAsync(authorized.Database, parameters, secret, client, relayed, output, cancellationToken).ConfigureAwait(false);
                    }

                    var lease = services.GetRequiredService<QosSessions>().TryEnter(authorized.Database);
                    if (lease is null)
                    {
                        output.Error("53300", "organization session limit exceeded", fatal: true);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        return default;
                    }

                    var previousHome = services.GetRequiredService<DatabaseFences>().MovedTo(authorized.Database.DatabaseId!);
                    ISqlSession? sql = null;
                    Session? session = null;
                    try
                    {
                        sql = services.GetRequiredService<ISqlService>().Open(authorized.Database, authorized.CanWrite);
                        var project = database.IndexOf('/', StringComparison.Ordinal) is var slash && slash >= 0 ? database[..slash] : null;
                        do
                        {
                            session = new Session(RandomNumberGenerator.GetInt32(1, int.MaxValue), RandomNumberGenerator.GetInt32(int.MaxValue), secret, database,
                                parameters.GetValueOrDefault("user") ?? "", client, sql, PostgresStatements.Settings(parameters.GetValueOrDefault("application_name") ?? ""))
                            {
                                DatabaseId = authorized.Database.DatabaseId!,
                                PreviousHome = previousHome,
                                Key = authorized.Key,
                                Until = authorized.Until,
                                TokenExpiry = authorized.TokenExpiry,
                                Project = project,
                                Lease = lease,
                            };
                        }
                        while (!_sessions.TryAdd(session.ProcessId, session));

                        await GreetAsync(session, output, cancellationToken).ConfigureAwait(false);
                        return (session, null);
                    }
                    catch
                    {
                        if (session is not null)
                        {
                            _sessions.TryRemove(session.ProcessId, out _);
                        }

                        using var releasing = lease;
                        if (sql is not null)
                        {
                            await sql.DisposeAsync().ConfigureAwait(false);
                        }

                        throw;
                    }
                default:
                    return default;
            }
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
    private async ValueTask<(Session? Session, Relay? Relay)> RelayAsync(TenantContext tenant, Dictionary<string, string> parameters,
        string secret, IPAddress? client, bool relayed, PgWriter output, CancellationToken cancellationToken)
    {
        if (relayed)
        {
            output.Error("57P03", HomeForwarding.PlacementMissMessage, fatal: true);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return default;
        }
        var admission = services.GetRequiredService<IQosAdmission>().TryEnter(tenant, out var retryAfter);
        if (admission is null)
        {
            output.Error("53300", $"tenant busy, retry after {retryAfter}s", fatal: true);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return default;
        }
        var budget = new ProxyRequestBudget(Time, SqlLimits.MaxRequestTime);
        using var deadline = budget.Link(cancellationToken);
        try
        {
            var home = await services.GetRequiredService<IHomeLocator>()
                .LocateAsync(tenant.ProjectId!, tenant.DatabaseId, deadline.Token).ConfigureAwait(false);
            if (home is null)
            {
                admission.Dispose();
                output.Error("08001", "the database region could not be reached; retry", fatal: true);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return default;
            }
            return (null, new Relay(home, tenant, PostgresRelay.Startup(parameters, secret), client, admission, budget));
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }

    private async ValueTask<Access> UnheldAsync(string organization, string project, string database, string? restriction, CancellationToken cancellationToken)
    {
        if (restriction is not null && restriction != database)
        {
            return Denied;
        }
        return await Tenants.PlanAsync(organization, cancellationToken).ConfigureAwait(false) switch
        {
            PlanTier plan => new RemoteAuthorized(new TenantContext(organization, plan, project, database)),
            Unavailable => new PgError("57P03", "the database is unavailable; retry"),
        };
    }

    private async ValueTask<Access> AuthorizeDashboardKeyAsync(string secret, string database, int slash, IPAddress? client, CancellationToken cancellationToken)
    {
        var grant = await services.GetRequiredService<TsAuthKeyExchange>().ExchangeAsync(secret, slash >= 0 ? database[..slash] : null, cancellationToken).ConfigureAwait(false);
        if (grant is Unavailable)
        {
            return new PgError("57P03", "authentication is unavailable; retry");
        }

        if (grant is not TsAuthKeyGrant { } key
            || !key.Scopes.Contains(ApiKeyScopes.SqlRead)
            || (slash >= 0 && !string.Equals(database[..slash], key.ProjectId, StringComparison.Ordinal))
            || (!EntityIds.IsValid(database[(slash + 1)..]) && !DatabaseName.TryParse(database[(slash + 1)..], out _)))
        {
            return Denied;
        }

        if (!IpAllowlists.Admits(key.Allowlists, client))
        {
            return new PgError("28000", "ip_not_allowed: " + IpAllowlists.Refusal(client));
        }
        var answer = key.Key is null ? null : (await services.GetRequiredService<IKeyAuthority>().CheckAsync(key.Key, cancellationToken).ConfigureAwait(false)).Value;
        var until = (answer as KeyActive)?.Until;
        switch (answer)
        {
            case KeyRefused refused:
                return new PgError("28000", refused.Code + ": " + KeyAuthorityMessages.Refused(refused.Code), Fatal: true);
            case Unavailable:
                return new PgError("57P03", "auth_unavailable: " + KeyAuthorityMessages.Unavailable, Fatal: true);
        }

        switch (await ForwardingAuthorization.AuthorizeAsync(services,
            new ApiKey(key.Subject, "project grant", [.. key.Scopes], key.OrganizationId, key.ProjectId, key.Database),
            key.ProjectId, database[(slash + 1)..], [ApiKeyScopes.SqlRead], key.Allowlists, client, cancellationToken).ConfigureAwait(false))
        {
            case NotFound or Forbidden:
                return Denied;
            case Unavailable:
                return new PgError("57P03", "the database is unavailable; retry");
        }

        var organization = await services.GetRequiredService<IProjectCatalog>()
            .ResolveProjectAsync(key.ProjectId, key.Subject, createIn: null, cancellationToken).ConfigureAwait(false);
        if (organization is Unavailable)
        {
            return new PgError("57P03", "the database is unavailable; retry");
        }

        if (organization is NotFound)
        {
            return await UnheldAsync(key.OrganizationId, key.ProjectId, database[(slash + 1)..], key.Database, cancellationToken).ConfigureAwait(false);
        }

        if (organization is not string found)
        {
            return Denied;
        }
        var restriction = key.Database;
        if (restriction is not null && !EntityIds.IsValid(restriction))
        {
            var narrowed = await Tenants.FindDatabaseAsync(found, key.ProjectId, restriction, cancellationToken).ConfigureAwait(false);
            if (narrowed is Unavailable)
            {
                return new PgError("57P03", "the database is unavailable; retry");
            }
            if (narrowed is NotFound)
            {
                return Denied;
            }

            if (narrowed is not TenantContext restricted)
            {
                return Denied;
            }
            restriction = restricted.DatabaseId;
        }
        return await Tenants.FindDatabaseAsync(found, key.ProjectId, database[(slash + 1)..], cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext tenant when restriction is null || restriction == tenant.DatabaseId => new Authorized(tenant, key.Scopes.Contains(ApiKeyScopes.SqlWrite), key.Key, until, key.Key is null ? key.ValidTo : null),
            Unavailable => new PgError("57P03", "the database is unavailable; retry"),
            NotFound => await UnheldAsync(found, key.ProjectId, database[(slash + 1)..], restriction, cancellationToken).ConfigureAwait(false),
            _ => Denied,
        };
    }

    private async ValueTask<Access> AuthorizeAsync(string secret, string database, IPAddress? client, CancellationToken cancellationToken)
    {
        if (!services.GetRequiredService<ICatalogGate>().IsOpen)
        {
            return new PgError("57P03", "this machine's catalog is not verified; retry");
        }

        var slash = database.IndexOf('/', StringComparison.Ordinal);
        if (TsAuthKeyExchange.IsKey(secret))
        {
            return await AuthorizeDashboardKeyAsync(secret, database, slash, client, cancellationToken).ConfigureAwait(false);
        }

        if (await Keys.FindAsync(secret, cancellationToken).ConfigureAwait(false) is not ApiKey key
            || !key.Scopes.Contains(ApiKeyScopes.SqlRead)
            || (slash < 0 ? key.ProjectId : database[..slash]) is not { } project
            || !EntityIds.IsValid(project)
            || (!EntityIds.IsValid(database[(slash + 1)..]) && !DatabaseName.TryParse(database[(slash + 1)..], out _)))
        {
            return Denied;
        }

        switch (await ForwardingAuthorization.AuthorizeAsync(services, key, project, database[(slash + 1)..],
            [ApiKeyScopes.SqlRead], [], client, cancellationToken).ConfigureAwait(false))
        {
            case NotFound or Forbidden:
                return Denied;
            case Unavailable:
                return new PgError("57P03", "the database is unavailable; retry");
        }

        return await Tenants.FindDatabaseAsync(key.OrganizationId, project, database[(slash + 1)..], cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext tenant when key.Reaches(tenant) => new Authorized(tenant, key.Scopes.Contains(ApiKeyScopes.SqlWrite)),
            Unavailable => new PgError("57P03", "the database is unavailable; retry"),
            NotFound => await UnheldAsync(key.OrganizationId, project, database[(slash + 1)..], key.DatabaseId, cancellationToken).ConfigureAwait(false),
            _ => Denied,
        };
    }

    private async ValueTask<DatabasePlacement?> AwayAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        var region = services.GetRequiredService<TerraBaseOptions>().Region;
        return await services.GetRequiredService<IReplicaCatalog>().FindAsync(tenant, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
            && !placement.PlacedIn(region) ? placement : null;
    }

    private async Task ServeAsync(Session session, PipeReader input, PgWriter output, bool relayed, CancellationToken cancellationToken)
    {
        var executing = relayed ? services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping : cancellationToken;
        var failed = false;
        while (true)
        {
            (byte type, byte[] body, IMemoryReservation reservation) messageRead;
            var transaction = session.Sql.TransactionRemaining;
            using (var deadline = new SessionMessageDeadline(Time, transaction))
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
            {
                try
                {
                    var first = await input.ReadAtLeastAsync(1, waiting.Token).ConfigureAwait(false);
                    input.AdvanceTo(first.Buffer.Start, first.Buffer.Start);
                    if (first.Buffer.IsEmpty && first.IsCompleted)
                    {
                        return;
                    }

                    messageRead = await ReadMessageAsync(input, deadline, waiting.Token).ConfigureAwait(false);
                    try
                    {
                        deadline.Complete();
                    }
                    catch
                    {
                        messageRead.reservation.Dispose();
                        throw;
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    output.Error(transaction is not null && !deadline.IsPartial ? SqlErrors.Sqlstate(SqlErrors.TransactionExpired.Code) : "57P05",
                        transaction is not null && !deadline.IsPartial ? SqlErrors.TransactionExpired.Message : deadline.IsPartial ? "incomplete message timeout exceeded" : "session idle timeout exceeded", fatal: true);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            var (type, body, reservation) = messageRead;
            using (reservation)
            {
                if (type == 'X')
                {
                    return;
                }

                var exchange = session.Exchange ??= new Exchange(Time.GetTimestamp(), output.Written, EntityIds.New(Time).ToString());
                RequestLogOperations.Current.Value = exchange.Operations;
                exchange.Received += 5 + body.Length;
                if (!services.GetRequiredService<ICatalogGate>().IsOpen)
                {
                    output.Error("57P03", "this machine's catalog is not verified; retry", fatal: true);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var message = new PgReader(body);
                switch ((char)type)
                {
                    case 'Q':
                        if (await QueryAsync(session, message.CString(), output, executing).ConfigureAwait(false) is { } error)
                        {
                            Fail(session, output, error);
                            if (error.Fatal)
                            {
                                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                                Meter(session, output);
                                return;
                            }
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
                    case 'P' or 'B' or 'D' or 'E' or 'C':
                        if (!failed && await ExtendedAsync(session, (char)type, message, output, executing).ConfigureAwait(false) is { } refused)
                        {
                            Fail(session, output, refused);
                            if (refused.Fatal)
                            {
                                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                                Meter(session, output);
                                return;
                            }

                            failed = true;
                        }

                        break;
                    default:
                        throw new InvalidDataException("unsupported message type");
                }
            }
        }
    }
    private static void Fail(Session session, PgWriter output, PgError error)
    {
        output.Error(error.Code, error.Message, error.Fatal, session.Exchange is { } exchange ? $"request id {exchange.RequestId}" : null);
        session.Failed = !session.Sql.IsAutocommit;
        session.Exchange?.Failed = true;
    }

    private async ValueTask SyncAsync(Session session, PgWriter output, CancellationToken cancellationToken)
    {
        await session.Sql.EndRequestAsync().ConfigureAwait(false);
        output.Ready(session.Status);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        Meter(session, output);
    }
    private void Meter(Session session, PgWriter output)
    {
        if (session.Exchange is not { } exchange)
        {
            return;
        }

        session.Exchange = null;
        if (exchange.Database is { } database)
        {
            var (status, elapsed) = (exchange.Failed ? 400 : 200, Time.GetElapsedTime(exchange.Started));
            services.GetRequiredService<IUsageMeter>().RecordRequest(database, "OTHER", status, elapsed, exchange.Received, output.Written - exchange.Written);
            var (statements, errors) = exchange.Operations.Statements(database);
            services.GetRequiredService<IRequestLogs>().Record(database, "OTHER", "postgres", status, elapsed, exchange.Operations.Count(database),
                exchange.RequestId, statements, errors);
        }
    }
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
                var atomic = statements is { Length: > 1 } && session.Sql.IsAutocommit
                    && !Array.Exists(statements, statement => statement.Kind is PgKind.Begin or PgKind.Commit or PgKind.Rollback);
                PgError? failure = null;
                if (atomic && await ControlAsync(session, PgKind.Begin, cancellationToken).ConfigureAwait(false) is { } beginError)
                {
                    return beginError;
                }

                foreach (var statement in statements)
                {
                    switch (await RunAsync(session, statement, [], cancellationToken).ConfigureAwait(false))
                    {
                        case PgError error:
                            failure = error;
                            break;
                        case PgResult[] results:
                            foreach (var result in results)
                            {
                                if (Write(output, result, [], describe: true) is { } encodeError)
                                {
                                    failure = encodeError;
                                    break;
                                }
                            }

                            break;
                    }

                    if (failure is not null)
                    {
                        break;
                    }
                }

                if (failure is null && atomic && await ControlAsync(session, PgKind.Commit, cancellationToken).ConfigureAwait(false) is { } commitError)
                {
                    failure = commitError;
                }

                if (failure is not null)
                {
                    if (atomic && !session.Sql.IsAutocommit)
                    {
                        _ = await ControlAsync(session, PgKind.Rollback, cancellationToken).ConfigureAwait(false);
                    }

                    return failure;
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
                    return new PgError("54000", $"at most {MaxNamed} prepared statements per session");
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
                    if (PostgresStatements.Decode(bound.Oids[i], Format(formats, i), values[i]) is not { } value)
                    {
                        return new PgError("22P02", $"invalid value for parameter ${i + 1}, or an unsupported binary type");
                    }

                    args[i] = value;
                }

                if (session.Portals.Count >= MaxNamed && !session.Portals.ContainsKey(portalName))
                {
                    return new PgError("54000", $"at most {MaxNamed} portals per session");
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
                        if (await GuardAsync(session, async (database, token) =>
                        {
                            PostgresStatements.InferParameters(statement.Statement, statement.Oids,
                                await services.GetRequiredService<ISqlService>().ListTablesAsync(database, token).ConfigureAwait(false));
                            return Array.Empty<PgResult>();
                        }, cancellationToken).ConfigureAwait(false) is PgError { Fatal: true } inferring)
                        {
                            return inferring;
                        }
                    }

                    output.Begin('t');
                    output.Int16(statement.Oids.Length);
                    Array.ForEach(statement.Oids, oid => output.Int32(oid == 0 ? PgOid.Text : oid));
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
                if (session.TokenExpiry is { } expiredAt && Time.GetUtcNow() >= expiredAt)
                {
                    return Denied;
                }

                var (executed, maxRows) = (message.CString(), message.Int32());
                if (!session.Portals.TryGetValue(executed, out var running))
                {
                    return new PgError("34000", "portal does not exist");
                }

                if (maxRows > 0)
                {
                    return new PgError("0A000", "Execute with a row limit is not supported yet");
                }
                if (running.Pending is not null && await AuthorizeAsync(session.ApiKey, session.Database, session.Client, cancellationToken).ConfigureAwait(false) is PgError stale)
                {
                    running.Pending = null;
                    return stale;
                }

                var executedOutcome = running.Pending ?? await RunAsync(session, running.Statement, running.Args, cancellationToken).ConfigureAwait(false);
                running.Pending = null;
                if (executedOutcome is PgError executeError)
                {
                    return executeError;
                }

                foreach (var result in executedOutcome is PgResult[] results ? results : [])
                {
                    if (Write(output, result, running.Formats, describe: false, running.Described ? running.Columns : null) is { } encodeError)
                    {
                        return encodeError;
                    }
                }

                return null;
            default:
                var (closed, closedName) = (message.Byte(), message.CString());
                if (closed == 'S')
                {
                    session.Statements.Remove(closedName);
                }
                else if (session.Portals.Remove(closedName, out var closedPortal))
                {
                    ChargePending(closedPortal);
                }

                output.Empty('3');
                return null;
        }
    }
    private async ValueTask<(PgColumn[]? Columns, bool ReadOnly, PgError? Error)> DescribeAsync(Session session, PgStatement statement, CancellationToken cancellationToken)
    {
        switch (statement.Kind)
        {
            case PgKind.Show or PgKind.Catalog:
                var outcome = await RunAsync(session, statement, [], cancellationToken, Counting.Failures).ConfigureAwait(false);
                return (outcome is PgResult[] { Length: > 0 } results ? results[0].Columns : null, true, outcome is PgError error ? error : null);
            case PgKind.Sql:
                SqlDescription? shape = null;
                var guarded = await GuardAsync(session, async (_, token) =>
                {
                    switch (await DescribeStatementAsync(session, statement, token).ConfigureAwait(false))
                    {
                        case SqlError error:
                            return PostgresStatements.Error(error);
                        case SqlDescription description:
                            shape = description;
                            break;
                    }

                    return Array.Empty<PgResult>();
                }, cancellationToken, Counting.Failures).ConfigureAwait(false);
                return guarded is PgError failure || shape is null
                    ? (null, false, guarded is PgError refused ? refused : new PgError("XX000", "internal error"))
                    : (shape.Columns.Count == 0 ? null : [.. shape.Columns.Select(column => new PgColumn(column.Name, PostgresStatements.DescribedOid(column)))],
                        shape.IsReadOnly, null);
            default:
                return (null, false, null);
        }
    }

    private async ValueTask<PgOutcome> RunAsync(Session session, PgStatement statement, IReadOnlyList<SqlValue> args, CancellationToken cancellationToken,
        Counting counting = Counting.All)
    {
        return await GuardAsync(session, async (database, token) =>
        {
            if (session.Failed && statement.Kind is not (PgKind.Commit or PgKind.Rollback or PgKind.Empty)
                && !(statement.Kind == PgKind.Savepoint && PostgresStatements.RollsBackTo(statement.Text)))
            {
                return PostgresStatements.Error(SqlErrors.InFailedTransaction);
            }

            switch (statement.Kind)
            {
                case PgKind.Empty:
                    return new[] { new PgResult(null, [], null) };
                case PgKind.Discard:
                    session.Statements.Clear();
                    foreach (var discarded in session.Portals.Values)
                    {
                        ChargePending(discarded);
                    }

                    session.Portals.Clear();
                    return new[] { new PgResult(null, [], "DISCARD ALL") };
                case PgKind.Deallocate:
                    if (PostgresStatements.Deallocated(statement.Text) is { } deallocated)
                    {
                        session.Statements.Remove(deallocated);
                        return new[] { new PgResult(null, [], "DEALLOCATE") };
                    }

                    session.Statements.Clear();
                    return new[] { new PgResult(null, [], "DEALLOCATE ALL") };
                case PgKind.Set or PgKind.Show:
                    session.Settings["terrascale.write_route"] = session.Sql.LastRoute?.Header ?? "none";
                    var answered = PostgresStatements.RunCommand(statement.Kind, statement.Text, session.Settings);
                    session.Sql.Prefer(WritePreference.Parse(session.Settings["terrascale.write"]) ?? WriteOverride.Auto);
                    return answered;
                case PgKind.Catalog when PgCatalog.Functions(statement.Text, session.Database, session.User) is { } functions:
                    return new[] { functions };
            }

            return statement.Kind switch
            {
                PgKind.Catalog => PgCatalog.Answer(statement.Text, session.Database, session.User,
                    await services.GetRequiredService<ISqlService>().ListTablesAsync(database, token).ConfigureAwait(false)),
                PgKind.Begin or PgKind.Commit or PgKind.Rollback => await TransactionAsync(session, statement.Kind, statement.Text, token).ConfigureAwait(false),
                PgKind.Savepoint => await SavepointAsync(session, statement.Text, token).ConfigureAwait(false),
                _ => await StatementAsync(session, statement, args, token).ConfigureAwait(false),
            };
        }, cancellationToken, counting).ConfigureAwait(false);
    }

    private static async ValueTask<PgOutcome> StatementAsync(Session session, PgStatement statement, IReadOnlyList<SqlValue> args, CancellationToken token) =>
        SqlScript.Classify(statement.Sql) switch
        {
            SqlError error => PostgresStatements.Error(error),
            SqlStatementPlan plan => await session.Sql.ExecuteAsync(new SqlPlannedCommand(plan, new SqlCommand(statement.Sql, args, NoNamed)), token).ConfigureAwait(false) switch
            {
                SqlResult result => new[] { PostgresStatements.Result(statement, result) with { ReadCharge = result.ReadCharge } },
                SqlError error => PostgresStatements.Error(error),
            },
        };

    private static async ValueTask<SqlDescribeOutcome> DescribeStatementAsync(Session session, PgStatement statement, CancellationToken token) =>
        SqlScript.Classify(statement.Sql) switch
        {
            SqlError error => error,
            SqlStatementPlan plan => await session.Sql.DescribeAsync(plan, new SqlCommand(statement.Sql, [], NoNamed), token).ConfigureAwait(false),
        };

    private static async ValueTask<PgOutcome> SavepointAsync(Session session, string text, CancellationToken cancellationToken)
    {
        switch (SqlScript.Classify(text))
        {
            case SqlError error:
                return PostgresStatements.Error(error);
            case SqlStatementPlan plan:
                if (await session.Sql.ExecuteAsync(new SqlPlannedCommand(plan, new SqlCommand(text, [], NoNamed)), cancellationToken).ConfigureAwait(false) is SqlError failed)
                {
                    return PostgresStatements.Error(failed);
                }

                break;
        }

        session.Failed &= !PostgresStatements.RollsBackTo(text);
        return new[] { new PgResult(null, [], text.Split(' ', 2)[0].ToUpperInvariant()) };
    }

    private static async ValueTask<PgOutcome> TransactionAsync(Session session, PgKind kind, string text, CancellationToken cancellationToken)
    {
        async ValueTask<PgOutcome> Run(string verb)
        {
            return await session.Sql.ExecuteAsync(SqlBatch.Control(verb), cancellationToken).ConfigureAwait(false) is SqlError error
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
            var mode = PostgresStatements.BeginReadOnly(text) ? "BEGIN READ ONLY" : verb;
            return await session.Sql.ExecuteAsync(SqlBatch.Control(mode), cancellationToken).ConfigureAwait(false) is SqlError error
                ? PostgresStatements.Error(error)
                : new[] { new PgResult(null, [], verb) };
        }

        var aborted = session.Failed;
        session.Failed = false;
        var outcome = await Run(aborted ? "ROLLBACK" : verb).ConfigureAwait(false);
        if (outcome is PgError && !session.Sql.IsAutocommit)
        {
            _ = await Run("ROLLBACK").ConfigureAwait(false);
        }

        return outcome;
    }

    private async ValueTask<PgError?> ControlAsync(Session session, PgKind kind, CancellationToken cancellationToken)
    {
        var statement = new PgStatement(kind, "", "", 0);
        return await RunAsync(session, statement, [], cancellationToken, Counting.None).ConfigureAwait(false) is PgError error ? error : null;
    }
    private async ValueTask<PgOutcome> GuardAsync(Session session, Func<TenantContext, CancellationToken, ValueTask<PgOutcome>> run, CancellationToken cancellationToken,
        Counting counting = Counting.None)
    {
        var started = Time.GetTimestamp();
        TenantContext? reached = null;
        var outcome = await AdmitAsync(session, run, authorized => reached = authorized, cancellationToken).ConfigureAwait(false);
        if (reached is { } owner && (counting == Counting.All || counting == Counting.Failures && outcome is PgError))
        {
            services.GetRequiredService<IUsageMeter>().RecordStatement(owner, "postgres", outcome is PgError, Time.GetElapsedTime(started));
        }

        return outcome;
    }

    private async ValueTask<PgOutcome> AdmitAsync(Session session, Func<TenantContext, CancellationToken, ValueTask<PgOutcome>> run,
        Action<TenantContext> authorized, CancellationToken cancellationToken)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        session.Track(running);
        IDatabaseStore? store = null;
        try
        {
            if (session.TokenExpiry is { } until && Time.GetUtcNow() >= until)
            {
                return Denied;
            }

            var access = await AuthorizeAsync(session.ApiKey, session.Database, session.Client, running.Token).ConfigureAwait(false);
            if (access is not Authorized { Database: var database })
            {
                return access is PgError refusal ? refusal : Denied;
            }

            session.Exchange?.Database = database;
            var engine = services.GetRequiredService<IStorageEngine>();
            store = await engine.OpenDatabaseAsync(database, running.Token).ConfigureAwait(false);
            authorized(database);
            var tenant = new TenantContext(database.OrganizationId, database.Plan);
            var admission = services.GetRequiredService<IQosAdmission>();
            using var lease = admission.TryEnter(tenant, out var retryAfter);
            if (lease is null)
            {
                return new PgError("53300", $"tenant busy, retry after {retryAfter}s");
            }

            using var scope = QosContext.Enter(tenant, Memory, admission);
            using var statement = CancellationTokenSource.CreateLinkedTokenSource(running.Token, store?.Refused ?? CancellationToken.None);
            var outcome = await run(database, statement.Token).ConfigureAwait(false);
            _ = await engine.OpenDatabaseAsync(database, running.Token).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException) when (store?.Refused.IsCancellationRequested == true)
        {
            return new PgError("57P03", "copy_faulted: Storage requires recovery.");
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new PgError("57014", "canceling statement due to user request");
        }
        catch (StorageCopyFaultedException)
        {
            return new PgError("57P03", "copy_faulted: Storage requires recovery.");
        }
        catch (StorageProofPendingException)
        {
            return new PgError("57P03", "recovery_pending: Storage recovery proof is pending; retry.");
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
            output.Int16(Format(formats, i) == 1 && PgOid.Binary(columns[i].Oid) ? 1 : 0);
        }

        output.End();
    }
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
                var oid = columns is not null && i < columns.Length ? columns[i].Oid : PgOid.Text;
                if (!PostgresStatements.TryEncode(row[i], oid, Format(formats, i) == 1 && PgOid.Binary(oid), out var bytes))
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

    private async ValueTask<(byte Type, byte[] Body, IMemoryReservation Reservation)> ReadMessageAsync(PipeReader input, SessionMessageDeadline? deadline, CancellationToken cancellationToken)
    {
        var header = await ReadAsync(input, 5, deadline, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length is < 4 or > MaxMessageBytes)
        {
            throw new InvalidDataException("message length out of bounds");
        }

        var reservation = Memory.TryReserve(length) ?? throw new QosMemoryException();
        try
        {
            return (header[0], await ReadAsync(input, length - 4, deadline, cancellationToken).ConfigureAwait(false), reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private static async ValueTask<byte[]> ReadAsync(PipeReader input, int count, SessionMessageDeadline? deadline, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return [];
        }

        var bytes = new byte[count];
        var received = 0;
        while (received < count)
        {
            var read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var take = (int)Math.Min(read.Buffer.Length, count - received);
            read.Buffer.Slice(0, take).CopyTo(bytes.AsSpan(received));
            input.AdvanceTo(read.Buffer.GetPosition(take));
            received += take;
            if (take > 0)
            {
                deadline?.Progress(take);
            }

            if (read.IsCompleted && received < count)
            {
                throw new EndOfStreamException();
            }
        }

        return bytes;
    }

    private sealed record Authorized(TenantContext Database, bool CanWrite, KeyToken? Key = null, DateTimeOffset? Until = null, DateTimeOffset? TokenExpiry = null);
    private enum Counting
    {
        None,
        Failures,
        All,
    }

    private sealed record Exchange(long Started, long Written, string RequestId)
    {
        public RequestLogOperations Operations { get; } = new();

        public long Received { get; set; }

        public bool Failed { get; set; }

        public TenantContext? Database { get; set; }
    }

    private sealed record Prepared(PgStatement Statement, int[] Oids)
    {
        public PgColumn[]? Columns { get; set; }

        public bool Described { get; set; }
    }

    private static void ChargePending(Portal portal)
    {
        if (portal.Pending is PgResult[] results)
        {
            Array.ForEach(results, result => result.ReadCharge?.Accept());
        }
    }

    private sealed record Portal(PgStatement Statement, SqlValue[] Args, short[] Formats)
    {
        public PgColumn[]? Columns { get; set; }

        public bool Described { get; set; }

        public PgOutcome? Pending { get; set; }
    }
    private sealed record Session(int ProcessId, int Secret, string ApiKey, string Database, string User, IPAddress? Client, ISqlSession Sql, Dictionary<string, string> Settings)
    {
        public required IDisposable Lease { get; init; }

        public required string DatabaseId { get; init; }

        public string? PreviousHome { get; init; }

        public KeyToken? Key { get; init; }
        public DateTimeOffset? Until { get; init; }
        public DateTimeOffset? TokenExpiry { get; init; }

        public string? Project { get; init; }

        private readonly Lock _gate = new();
        private CancellationTokenSource? _running;

        public Dictionary<string, Prepared> Statements { get; } = [];

        public Dictionary<string, Portal> Portals { get; } = [];
        public bool Failed { get; set; }
        public Exchange? Exchange { get; set; }
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
}
