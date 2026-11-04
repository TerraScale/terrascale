using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authorization;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

internal sealed class SqlSocket(HttpContext context, WebSocket socket) : IDisposable
{
    private const int FirstBufferBytes = 16 * 1024;
    private const int MaxTokenLength = 8 * 1024;

    public static readonly TimeSpan AuthDeadline = TimeSpan.FromSeconds(5);

    private static readonly SqlError AuthFirst = new(SqlErrors.Unauthorized, "the first frame is {\"type\":\"auth\",\"token\":\"…\"}");
    private static readonly SqlError UnknownDatabase = new(SqlErrors.Unauthorized, "the database does not exist or the credential does not reach it");

    private static readonly SqlError CredentialRevoked = new(SqlErrors.Unauthorized, "the credential no longer grants this access to the database");
    private static readonly SqlError FailedCommit = new(SqlErrors.TransactionRolledBack, "the transaction was rolled back after a failed statement");
    private static readonly SqlError AlreadyOpen = new(SqlErrors.TransactionState, "there is already a transaction in progress");
    private static readonly SqlError NoneOpen = new(SqlErrors.TransactionState, "there is no transaction in progress");
    private static readonly SqlError UnknownType = new(SqlErrors.InvalidRequest, "type is execute, begin, commit or rollback");

    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly IServiceProvider _services = context.RequestServices;
    private readonly CancellationTokenSource _revoked = new();
    private readonly List<IDisposable> _watches = [];
    private ISqlSession? _session;
    private TenantContext? _database;
    private bool _canWrite;
    private bool _failed;
    private bool _closing;

    private ISqlSession Session => _session!;

    private TenantContext Database => _database!;

    public async Task RunAsync(TenantContext? database, bool canWrite, CancellationToken cancellationToken)
    {
        var stopping = _services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await using var leaving = stopping.Register(() => _ = CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "the server is restarting")).ConfigureAwait(false);
        await using var cutOff = _revoked.Token.Register(() => _ = CloseAsync(WebSocketCloseStatus.PolicyViolation, "the credential was revoked")).ConfigureAwait(false);
        (_database, _canWrite) = (database, canWrite);
        if (_database is null && !await AuthenticateAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _session = _services.GetRequiredService<ISqlService>().Open(Database, _canWrite);
        await using (_session.ConfigureAwait(false))
        {
            await ServeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested && !_revoked.IsCancellationRequested)
        {
            var frame = await ReceiveAsync(SqlBatch.MaxBytes, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            try
            {
                if (!await AnswerAsync(frame.Value.Bytes.AsMemory(0, frame.Value.Length), cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame.Value.Bytes);
                frame.Value.Reservation.Dispose();
            }
        }
    }

    private async ValueTask<bool> AuthenticateAsync(CancellationToken cancellationToken)
    {
        (byte[] Bytes, int Length, IMemoryReservation Reservation)? frame;
        using (var deadline = new CancellationTokenSource(AuthDeadline))
        await using (deadline.Token.Register(() => _ = CloseAsync(WebSocketCloseStatus.PolicyViolation, "no auth frame within 5 seconds")).ConfigureAwait(false))
        {
            frame = await ReceiveAsync(FirstBufferBytes, cancellationToken).ConfigureAwait(false);
            if (frame is null || _closing)
            {
                if (frame is { } late)
                {
                    ArrayPool<byte>.Shared.Return(late.Bytes);
                    late.Reservation.Dispose();
                }

                return false;
            }
        }

        SqlSocketRequest? request;
        try
        {
            request = RequestJson.Read<SqlSocketRequest>(frame.Value.Bytes.AsSpan(0, frame.Value.Length), SqlErrors.InvalidRequest) is SqlSocketRequest read ? read : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame.Value.Bytes);
            frame.Value.Reservation.Dispose();
        }

        if (request is not { Type: "auth", Token: { Length: > 0 and <= MaxTokenLength } token } || token.Any(char.IsControl) || token.Any(char.IsWhiteSpace))
        {
            return await RefuseAsync(request?.Id, AuthFirst, cancellationToken).ConfigureAwait(false);
        }

        context.Request.Headers.Authorization = "Bearer " + token;
        var authenticated = await ApiKeysExtensions.AuthenticateAgainAsync(context).ConfigureAwait(false);
        if (!authenticated.Succeeded)
        {
            return await RefuseAsync(request.Id, CredentialRevoked, cancellationToken).ConfigureAwait(false);
        }

        context.User = authenticated.Principal;
        if (await UserAuth.UserAuthExtensions.AdmitSocketAsync(context, _revoked, _watches.Add).ConfigureAwait(false) is { } refused)
        {
            return await RefuseAsync(request.Id, new SqlError(SqlErrors.Unauthorized, refused.Code + ": " + refused.Message), cancellationToken).ConfigureAwait(false);
        }

        var authorization = _services.GetRequiredService<IAuthorizationService>();
        if (!(await authorization.AuthorizeAsync(context.User, context, ApiKeyScopes.DocumentsRead).ConfigureAwait(false)).Succeeded)
        {
            return await RefuseAsync(request.Id, SqlErrors.Auth with { Message = "SQL needs the documents:read scope" }, cancellationToken).ConfigureAwait(false);
        }

        _canWrite = (await authorization.AuthorizeAsync(context.User, context, ApiKeyScopes.DocumentsWrite).ConfigureAwait(false)).Succeeded;
        switch (await DocumentRoute.CheckTenantAsync(context, _services.GetRequiredService<ITenantDirectory>(), cancellationToken).ConfigureAwait(false))
        {
            case TenantContext found:
                _database = found;
                break;
            case Unavailable unavailable:
                return await RefuseAsync(request.Id, new SqlError(SqlErrors.Unavailable, unavailable.Message), cancellationToken).ConfigureAwait(false);
            default:
                return await RefuseAsync(request.Id, UnknownDatabase, cancellationToken).ConfigureAwait(false);
        }

        var region = _services.GetRequiredService<Configuration.TerraBaseOptions>().Region;
        switch (await Management.DatabaseRegions.RecordAsync(context, (string)context.Request.RouteValues["project"]!, (string)context.Request.RouteValues["database"]!)
            .ConfigureAwait(false))
        {
            case Management.ManagedDatabase record when !record.PlacedIn(region) && !record.Replicas.Contains(region):
                return await RefuseAsync(request.Id, new SqlError(SqlErrors.WrongRegion,
                    $"the database is served in {string.Join(", ", record.Regions(region))}, not {region}; connect through its home region {record.Home(region)}"),
                    cancellationToken).ConfigureAwait(false);
            case Unavailable unavailable:
                return await RefuseAsync(request.Id, new SqlError(SqlErrors.Unavailable, unavailable.Message), cancellationToken).ConfigureAwait(false);
        }

        await SendAsync(new SqlSocketResult(request.Id, "result", [], false), TerraBaseJsonContext.Default.SqlSocketResult, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> RefuseAsync(long? id, SqlError error, CancellationToken cancellationToken)
    {
        await SendAsync(new SqlSocketError(id, "error", SqlBatch.Body(new SqlBatchFailure(error, 0)).Error, false), TerraBaseJsonContext.Default.SqlSocketError,
            cancellationToken).ConfigureAwait(false);
        await CloseAsync(WebSocketCloseStatus.PolicyViolation, error.Code).ConfigureAwait(false);
        return false;
    }

    private async ValueTask<(byte[] Bytes, int Length, IMemoryReservation Reservation)?> ReceiveAsync(int maxBytes, CancellationToken cancellationToken)
    {
        var memory = _services.GetRequiredService<IMemoryBudget>();
        var reservation = memory.TryReserve(MemoryLane.Foreground, FirstBufferBytes, _database);
        if (reservation is null)
        {
            await CloseAsync(WebSocketCloseStatus.InternalServerError, "organization memory budget exceeded").ConfigureAwait(false);
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(FirstBufferBytes);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == Math.Min(buffer.Length, maxBytes))
                {
                    if (length >= maxBytes)
                    {
                        await CloseAsync(WebSocketCloseStatus.MessageTooBig, maxBytes == SqlBatch.MaxBytes ? "a frame holds at most 1 MiB" : "the auth frame holds at most 16 KiB")
                            .ConfigureAwait(false);
                        Release();
                        return null;
                    }

                    var size = Math.Min(buffer.Length * 2, maxBytes);
                    var grown = memory.TryReserve(MemoryLane.Foreground, size, _database);
                    if (grown is null)
                    {
                        await CloseAsync(WebSocketCloseStatus.InternalServerError, "organization memory budget exceeded").ConfigureAwait(false);
                        Release();
                        return null;
                    }

                    var larger = ArrayPool<byte>.Shared.Rent(size);
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    reservation.Dispose();
                    (buffer, reservation) = (larger, grown);
                }

                var read = await socket.ReceiveAsync(buffer.AsMemory(length, Math.Min(buffer.Length, maxBytes) - length), cancellationToken).ConfigureAwait(false);
                if (read.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync(WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
                    Release();
                    return null;
                }

                if (read.MessageType != WebSocketMessageType.Text)
                {
                    await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "frames are JSON text").ConfigureAwait(false);
                    Release();
                    return null;
                }

                length += read.Count;
                if (read.EndOfMessage)
                {
                    return (buffer, length, reservation);
                }
            }
        }
        catch
        {
            Release();
            throw;
        }

        void Release()
        {
            ArrayPool<byte>.Shared.Return(buffer);
            reservation.Dispose();
        }
    }

    private async ValueTask<bool> AnswerAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        var parsed = RequestJson.Read<SqlSocketRequest>(frame.Span, SqlErrors.InvalidRequest);
        if (parsed is not SqlSocketRequest request)
        {
            var invalid = parsed is Validation error ? error.Message : "the frame is not a request";
            await SendErrorAsync(null, new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, invalid), 0), cancellationToken).ConfigureAwait(false);
            return true;
        }

        var time = _services.GetRequiredService<TimeProvider>();
        var started = time.GetTimestamp();
        switch (await AuthorizeAsync(cancellationToken).ConfigureAwait(false))
        {
            case SqlError refused:
                await SendErrorAsync(request.Id, new SqlBatchFailure(refused, 0), cancellationToken).ConfigureAwait(false);
                await CloseAsync(WebSocketCloseStatus.PolicyViolation, refused.Message).ConfigureAwait(false);
                return false;
            case Unavailable unavailable:
                await SendErrorAsync(request.Id, new SqlBatchFailure(new SqlError(SqlErrors.Unavailable, unavailable.Message), 0), cancellationToken).ConfigureAwait(false);
                return true;
        }

        var operations = new RequestLogOperations();
        var previous = RequestLogOperations.Current.Value;
        RequestLogOperations.Current.Value = operations;
        try
        {
            var tenant = new TenantContext(Database.OrganizationId, Database.Plan);
            using var lease = _services.GetRequiredService<IQosAdmission>().TryEnter(tenant, out var retryAfter);
            if (lease is null)
            {
                var busy = new SqlError(SqlErrors.TenantBusy, $"organization request budget exceeded; retry after {retryAfter}s");
                return await FinishAsync(request.Id, new SqlBatchFailure(busy, 0), frame.Length, time, started, operations, cancellationToken).ConfigureAwait(false);
            }

            using var scope = QosContext.Enter(tenant, _services.GetRequiredService<IMemoryBudget>());
            Session.BeginRequest();
            SqlBatchOutcome outcome;
            try
            {
                outcome = await RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (QosMemoryException)
            {
                outcome = new SqlBatchFailure(new SqlError(SqlErrors.TenantBusy, "organization memory budget exceeded"), 0);
            }
            finally
            {
                await Session.EndRequestAsync().ConfigureAwait(false);
            }

            return await FinishAsync(request.Id, outcome, frame.Length, time, started, operations, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RequestLogOperations.Current.Value = previous;
        }
    }

    private async ValueTask<object?> AuthorizeAsync(CancellationToken cancellationToken)
    {
        var authenticated = await ApiKeysExtensions.AuthenticateAgainAsync(context).ConfigureAwait(false);
        if (!authenticated.Succeeded)
        {
            return CredentialRevoked;
        }

        context.User = authenticated.Principal;
        var authorization = _services.GetRequiredService<IAuthorizationService>();
        if (!(await authorization.AuthorizeAsync(context.User, context, ApiKeyScopes.DocumentsRead).ConfigureAwait(false)).Succeeded
            || (await authorization.AuthorizeAsync(context.User, context, ApiKeyScopes.DocumentsWrite).ConfigureAwait(false)).Succeeded != _canWrite)
        {
            return CredentialRevoked;
        }

        return await DocumentRoute.CheckTenantAsync(context, _services.GetRequiredService<ITenantDirectory>(), cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext found when found.DatabaseId == Database.DatabaseId && found.OrganizationId == Database.OrganizationId => null,
            Unavailable error => error,
            _ => CredentialRevoked,
        };
    }

    private async ValueTask<SqlBatchOutcome> RunAsync(SqlSocketRequest request, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case "execute":
                if (_failed)
                {
                    return new SqlBatchFailure(SqlErrors.InFailedTransaction, 0);
                }

                var outcome = SqlBatch.Commands(request.Statements) switch
                {
                    List<SqlCommand> commands => await SqlBatch.RunAsync(Session, commands, cancellationToken).ConfigureAwait(false),
                    SqlBatchFailure failure => failure,
                };
                _failed = outcome is SqlBatchFailure && !Session.IsAutocommit;
                return outcome;
            case "begin":
                return Session.IsAutocommit ? await ControlAsync("BEGIN", cancellationToken).ConfigureAwait(false) : new SqlBatchFailure(AlreadyOpen, 0);
            case "commit" when Session.IsAutocommit:
            case "rollback" when Session.IsAutocommit:
                return new SqlBatchFailure(NoneOpen, 0);
            case "commit" when _failed:
                _failed = false;
                _ = await ControlAsync("ROLLBACK", cancellationToken).ConfigureAwait(false);
                return new SqlBatchFailure(FailedCommit, 0);
            case "commit":
            case "rollback":
                _failed = false;
                var ended = await ControlAsync(request.Type == "commit" ? "COMMIT" : "ROLLBACK", cancellationToken).ConfigureAwait(false);
                if (ended is SqlBatchFailure && !Session.IsAutocommit)
                {
                    _ = await ControlAsync("ROLLBACK", cancellationToken).ConfigureAwait(false);
                }

                return ended;
            default:
                return new SqlBatchFailure(UnknownType, 0);
        }
    }

    private async ValueTask<SqlBatchOutcome> ControlAsync(string verb, CancellationToken cancellationToken) =>
        await Session.ExecuteAsync(SqlBatch.Control(verb), cancellationToken).ConfigureAwait(false) switch
        {
            SqlError error => new SqlBatchFailure(error, 0),
            SqlResult => new SqlExecuteBody([]),
        };

    private async ValueTask<bool> FinishAsync(
        long? id, SqlBatchOutcome outcome, long received, TimeProvider time, long started, RequestLogOperations operations, CancellationToken cancellationToken)
    {
        var (sent, status) = outcome switch
        {
            SqlExecuteBody body => (await SendAsync(new SqlSocketResult(id, "result", body.Results, !Session.IsAutocommit),
                TerraBaseJsonContext.Default.SqlSocketResult, cancellationToken).ConfigureAwait(false), StatusCodes.Status200OK),
            SqlBatchFailure failure => (await SendErrorAsync(id, failure, cancellationToken).ConfigureAwait(false), failure.Error.Status),
        };
        var elapsed = time.GetElapsedTime(started);
        _services.GetRequiredService<IUsageMeter>().RecordRequest(Database, "OTHER", status, elapsed, received, sent);
        if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route })
        {
            var (statements, errors) = operations.Statements(Database);
            _services.GetRequiredService<IRequestLogs>().Record(Database, "OTHER", route, status, elapsed, operations.Count(Database),
                EntityIds.New().ToString(), statements, errors);
        }

        return true;
    }

    private ValueTask<long> SendErrorAsync(long? id, SqlBatchFailure failure, CancellationToken cancellationToken) =>
        SendAsync(new SqlSocketError(id, "error", SqlBatch.Body(failure).Error, !Session.IsAutocommit), TerraBaseJsonContext.Default.SqlSocketError, cancellationToken);

    private async ValueTask<long> SendAsync<T>(T frame, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, type);
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_closing && socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _send.Release();
        }

        return bytes.Length;
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string? reason)
    {
        await _send.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closing || socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                return;
            }

            _closing = true;
            await socket.CloseOutputAsync(status, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _send.Release();
        }
    }

    public void Dispose()
    {
        _watches.ForEach(watch => watch.Dispose());
        _revoked.Dispose();
        _send.Dispose();
    }
}
