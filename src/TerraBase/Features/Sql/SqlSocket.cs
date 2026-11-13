using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sql.Routing;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

internal sealed class SqlSocket(
    HttpContext context,
    WebSocket socket,
    IHostApplicationLifetime lifetime,
    ISqlService sql,
    IAuthorizationService authorization,
    ITenantDirectory tenants,
    TerraBaseOptions options,
    IMemoryBudget memory,
    TimeProvider time,
    IQosAdmission admission,
    QosSessions sessions,
    IUsageMeter usage,
    IRequestLogs logs,
    RegionalIngress ingress) : IDisposable
{
    private const int FirstBufferBytes = 16 * 1024;
    private const int MaxTokenLength = 8 * 1024;
    private const WebSocketCloseStatus ServiceRestart = (WebSocketCloseStatus)1012;
    private const string WriterMoving = "the region's writer is moving; reconnect";

    public static readonly TimeSpan AuthDeadline = TimeSpan.FromSeconds(5);

    private static readonly SqlError AuthFirst = new(SqlErrors.Unauthorized, "the first frame is {\"type\":\"auth\",\"token\":\"…\"}");
    private static readonly SqlError UnknownDatabase = new(SqlErrors.Unauthorized, "the database does not exist or the credential does not reach it");

    private static readonly SqlError CredentialRevoked = new(SqlErrors.Unauthorized, "the credential no longer grants this access to the database");
    private static readonly SqlError FailedCommit = new(SqlErrors.TransactionRolledBack, "the transaction was rolled back after a failed statement");
    private static readonly SqlError AlreadyOpen = new(SqlErrors.TransactionState, "there is already a transaction in progress");
    private static readonly SqlError NoneOpen = new(SqlErrors.TransactionState, "there is no transaction in progress");
    private static readonly SqlError UnknownType = new(SqlErrors.InvalidRequest, "type is execute, begin, commit or rollback");
    private static readonly SqlError NodeFull = new(SqlErrors.Unavailable, "the node is at its request limit; retry");

    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly SemaphoreSlim _frame = new(1, 1);
    private readonly CancellationTokenSource _revoked = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly List<IDisposable> _watches = [];
    private ISqlSession? _session;
    private TenantContext? _database;
    private CancellationToken _fence;
    private CancellationToken _admitting;
    private long? _authId;
    private bool _canWrite;
    private bool _failed;
    private bool _closing;
    private DateTimeOffset? _tokenExpiry;

    private ISqlSession Session => _session!;

    private TenantContext Database => _database!;

    public async Task RunAsync(TenantContext? database, bool canWrite, CancellationToken fence, CancellationToken cancellationToken)
    {
        var stopping = lifetime.ApplicationStopping;
        await using var aborting = _closed.Token.Register(socket.Abort).ConfigureAwait(false);
        await using var leaving = stopping.Register(() => _ = CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "the server is restarting")).ConfigureAwait(false);
        await using var moving = fence.Register(() => _ = CloseAfterFrameAsync()).ConfigureAwait(false);
        await using var cutOff = _revoked.Token.Register(() => _ = CloseAsync(WebSocketCloseStatus.PolicyViolation, "the credential was revoked")).ConfigureAwait(false);
        using var admitting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, fence);
        (_database, _canWrite, _fence, _admitting) = (database, canWrite, fence, admitting.Token);
        var frameAuth = _database is null;
        if (frameAuth && !await AuthenticateAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        using var lease = sessions.TryEnter(Database);
        if (lease is null)
        {
            await RefuseAsync(_authId, new SqlError(SqlErrors.TenantBusy, "organization session limit exceeded"), cancellationToken).ConfigureAwait(false);
            return;
        }

        _tokenExpiry = context.Features.Get<ProjectToken>()?.ExpiresAt;
        using var expiry = _tokenExpiry is { } end
            ? new CancellationTokenSource(end - time.GetUtcNow() is { Ticks: > 0 } left ? left : TimeSpan.Zero, time)
            : new CancellationTokenSource();
        await using var expired = expiry.Token.Register(() => _ = CloseAsync(WebSocketCloseStatus.PolicyViolation, "the token expired")).ConfigureAwait(false);
        _session = sql.Open(Database, _canWrite);
        await using (_session.ConfigureAwait(false))
        {
            _session.Prefer(WritePreference.Parse(context.Request.Headers[WritePreference.Header]) ?? WriteOverride.Auto);
            if (frameAuth)
            {
                await SendAsync(new SqlSocketResult(_authId, "result", [], false), SqlJsonContext.Default.SqlSocketResult, cancellationToken).ConfigureAwait(false);
            }

            await ServeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (socket.State is (WebSocketState.Open or WebSocketState.CloseSent) && !cancellationToken.IsCancellationRequested && !_revoked.IsCancellationRequested)
        {
            var frame = await ReceiveAsync(SqlBatch.MaxBytes, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            try
            {
                if (_closing)
                {
                    continue;
                }

                await _frame.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!await AnswerAsync(frame.Value.Bytes.AsMemory(0, frame.Value.Length), cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                finally
                {
                    _frame.Release();
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
            return await RefuseAsync(request.Id, TokenKeysPending.On(context)
                ? new SqlError(SqlErrors.Unavailable, TokenKeysPending.Code + ": " + TokenKeysPending.Message)
                : CredentialRevoked, cancellationToken).ConfigureAwait(false);
        }

        context.User = authenticated.Principal;
        if (await SocketAdmission.AdmitSocketAsync(context, _revoked, _watches.Add).ConfigureAwait(false) is { } refused)
        {
            return await RefuseAsync(request.Id, new SqlError(SqlErrors.Unauthorized, refused.Code + ": " + refused.Message), cancellationToken).ConfigureAwait(false);
        }

        if (!(await authorization.AuthorizeAsync(context.User, context, ApiKeyScopes.SqlRead).ConfigureAwait(false)).Succeeded)
        {
            return await RefuseAsync(request.Id, SqlErrors.Auth with { Message = "SQL needs the sql:read scope" }, cancellationToken).ConfigureAwait(false);
        }

        _canWrite = (await authorization.AuthorizeAsync(context.User, context, ApiKeyScopes.SqlWrite).ConfigureAwait(false)).Succeeded;
        switch (await CheckTenantAsync(cancellationToken).ConfigureAwait(false))
        {
            case TenantContext found:
                _database = found;
                break;
            case Unavailable unavailable:
                return await RefuseAsync(request.Id, new SqlError(unavailable.Code, unavailable.Message), cancellationToken).ConfigureAwait(false);
            default:
                return await RefuseAsync(request.Id, UnknownDatabase, cancellationToken).ConfigureAwait(false);
        }

        var region = options.Region;
        switch (await DatabaseRegions.RecordAsync(context, context.RouteValue("project"), context.RouteValue("database"))
            .ConfigureAwait(false))
        {
            case ManagedDatabase record when !record.PlacedIn(region) && !record.AllRegions && !record.Replicas.Contains(region):
                return await RefuseAsync(request.Id, new SqlError(SqlErrors.WrongRegion,
                    $"the database is served in {string.Join(", ", record.Regions(region))}, not {region}; connect through its home region {record.Home(region)}"),
                    cancellationToken).ConfigureAwait(false);
            case Unavailable unavailable:
                return await RefuseAsync(request.Id, new SqlError(unavailable.Code, unavailable.Message), cancellationToken).ConfigureAwait(false);
        }

        _authId = request.Id;
        return true;
    }

    private async ValueTask<bool> RefuseAsync(long? id, SqlError error, CancellationToken cancellationToken)
    {
        await SendAsync(new SqlSocketError(id, "error", SqlBatch.Body(new SqlBatchFailure(error, 0), context.TraceIdentifier).Error, false), SqlJsonContext.Default.SqlSocketError,
            cancellationToken).ConfigureAwait(false);
        await CloseAsync(WebSocketCloseStatus.PolicyViolation, error.Code).ConfigureAwait(false);
        return false;
    }

    private async ValueTask<(byte[] Bytes, int Length, IMemoryReservation Reservation)?> ReceiveAsync(int maxBytes, CancellationToken cancellationToken)
    {
        var reservation = memory.TryReserve(FirstBufferBytes, _database);
        if (reservation is null)
        {
            await CloseAsync(WebSocketCloseStatus.InternalServerError, "organization memory budget exceeded").ConfigureAwait(false);
            return null;
        }

        var partial = false;
        var transaction = _session?.TransactionRemaining;
        using var idle = new CancellationTokenSource(transaction ?? QosSessions.IdleTimeout, time);
        var closing = Task.CompletedTask;
        var expired = idle.Token.Register(() => closing = ExpireAsync());
        var input = context.Features.GetRequiredFeature<SqlSocketInput>();
        input.Started = StartMessage;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
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
                    var grown = memory.TryReserve(size, _database);
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

                var read = await socket.ReceiveAsync(buffer.AsMemory(length, Math.Min(buffer.Length, maxBytes) - length), waiting.Token).ConfigureAwait(false);
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
                if (!read.EndOfMessage)
                {
                    StartMessage();
                }

                if (read.EndOfMessage)
                {
                    return (buffer, length, reservation);
                }
            }
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Release();
            return null;
        }
        catch
        {
            Release();
            throw;
        }
        finally
        {
            input.Started = null;
            await expired.DisposeAsync().ConfigureAwait(false);
            await closing.ConfigureAwait(false);
        }

        async Task ExpireAsync()
        {
            _closed.CancelAfter(Shutdown.DrainBudget);
            if (transaction is not null)
            {
                await SendErrorAsync(null, new SqlBatchFailure(SqlErrors.TransactionExpired, 0), _closed.Token).ConfigureAwait(false);
            }

            await CloseAsync(WebSocketCloseStatus.PolicyViolation, transaction is not null ? SqlErrors.TransactionExpired.Message
                : partial ? "incomplete message timeout exceeded" : "session idle timeout exceeded").ConfigureAwait(false);
        }

        void StartMessage()
        {
            if (!partial && _session is not { IsAutocommit: false })
            {
                partial = true;
                idle.CancelAfter(QosSessions.MessageTimeout);
            }
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

        using var running = await AdmitAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (_fence.IsCancellationRequested)
        {
            await CloseAsync(ServiceRestart, WriterMoving).ConfigureAwait(false);
            return true;
        }

        if (running is null)
        {
            return true;
        }

        var started = time.GetTimestamp();
        switch (await AuthorizeAsync(cancellationToken).ConfigureAwait(false))
        {
            case SqlError refused:
                await SendErrorAsync(request.Id, new SqlBatchFailure(refused, 0), cancellationToken).ConfigureAwait(false);
                await CloseAsync(WebSocketCloseStatus.PolicyViolation, refused.Message).ConfigureAwait(false);
                return false;
            case Unavailable unavailable:
                await SendErrorAsync(request.Id, new SqlBatchFailure(new SqlError(unavailable.Code, unavailable.Message), 0), cancellationToken).ConfigureAwait(false);
                return true;
        }

        var operations = new RequestLogOperations();
        var previous = RequestLogOperations.Current.Value;
        RequestLogOperations.Current.Value = operations;
        try
        {
            var tenant = new TenantContext(Database.OrganizationId, Database.Plan);
            using var lease = admission.TryEnter(tenant, out var retryAfter);
            if (lease is null)
            {
                var busy = new SqlError(SqlErrors.TenantBusy, $"organization request budget exceeded; retry after {retryAfter}s");
                return await FinishAsync(request.Id, new SqlBatchFailure(busy, 0), frame.Length, started, operations, cancellationToken).ConfigureAwait(false);
            }

            using var scope = QosContext.Enter(tenant, memory);
            Session.BeginRequest();
            SqlBatchOutcome outcome;
            try
            {
                outcome = await RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (StorageCopyFaultedException)
            {
                outcome = new SqlBatchFailure(new SqlError(SqlErrors.CopyFaulted, "copy_faulted: Storage requires recovery."), 0);
            }
            catch (StorageProofPendingException)
            {
                outcome = new SqlBatchFailure(new SqlError(SqlErrors.RecoveryPending, "recovery_pending: Storage recovery proof is pending; retry."), 0);
            }
            catch (QosMemoryException)
            {
                outcome = new SqlBatchFailure(new SqlError(SqlErrors.TenantBusy, "organization memory budget exceeded"), 0);
            }
            finally
            {
                await Session.EndRequestAsync().ConfigureAwait(false);
                running.Dispose();
            }

            return await FinishAsync(request.Id, outcome, frame.Length, started, operations, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RequestLogOperations.Current.Value = previous;
        }
    }

    private async ValueTask<RegionalIngress.Lease?> AdmitAsync(long? id, CancellationToken cancellationToken)
    {
        try
        {
            return await ingress.EnterAsync(parkWhileHeld: true, _admitting).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_fence.IsCancellationRequested)
        {
            return null;
        }
        catch (QosMemoryException)
        {
            await SendErrorAsync(id, new SqlBatchFailure(NodeFull, 0), cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private async ValueTask<object?> AuthorizeAsync(CancellationToken cancellationToken)
    {
        if (_tokenExpiry is { } expires && time.GetUtcNow() >= expires)
        {
            return CredentialRevoked;
        }

        var authenticated = await ApiKeysExtensions.AuthenticateAgainAsync(context).ConfigureAwait(false);
        if (!authenticated.Succeeded)
        {
            return TokenKeysPending.On(context)
                ? new Unavailable(TokenKeysPending.Code + ": " + TokenKeysPending.Message)
                : CredentialRevoked;
        }

        context.User = authenticated.Principal;
        if (!(await authorization.AuthorizeAsync(context.User, context, ApiKeyScopes.SqlRead).ConfigureAwait(false)).Succeeded
            || (await authorization.AuthorizeAsync(context.User, context, ApiKeyScopes.SqlWrite).ConfigureAwait(false)).Succeeded != _canWrite)
        {
            return CredentialRevoked;
        }

        return await CheckTenantAsync(cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext found when found.DatabaseId == Database.DatabaseId && found.OrganizationId == Database.OrganizationId => null,
            Unavailable error => error,
            _ => CredentialRevoked,
        };
    }

    private async ValueTask<TenantResult> CheckTenantAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await DatabaseRoute.CheckTenantAsync(context, tenants, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageCopyFaultedException)
        {
            return new Unavailable("Storage requires recovery.", SqlErrors.CopyFaulted);
        }
        catch (StorageProofPendingException)
        {
            return new Unavailable("Storage recovery proof is pending; retry.", SqlErrors.RecoveryPending);
        }
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
                    List<SqlPlannedCommand> commands => await SqlBatch.RunAsync(Session, commands, cancellationToken).ConfigureAwait(false),
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
        long? id, SqlBatchOutcome outcome, long received, long started, RequestLogOperations operations, CancellationToken cancellationToken)
    {
        var (sent, status) = outcome switch
        {
            SqlExecuteBody body => (await SendAsync(new SqlSocketResult(id, "result", body.Results, !Session.IsAutocommit),
                SqlJsonContext.Default.SqlSocketResult, cancellationToken).ConfigureAwait(false), StatusCodes.Status200OK),
            SqlBatchFailure failure => (await SendErrorAsync(id, failure, cancellationToken).ConfigureAwait(false), failure.Error.Status),
        };
        var elapsed = time.GetElapsedTime(started);
        usage.RecordRequest(Database, "OTHER", status, elapsed, received, sent);
        if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route })
        {
            var (statements, errors) = operations.Statements(Database);
            logs.Record(Database, "OTHER", route, status, elapsed, operations.Count(Database),
                EntityIds.New().ToString(), statements, errors);
        }

        return true;
    }

    private ValueTask<long> SendErrorAsync(long? id, SqlBatchFailure failure, CancellationToken cancellationToken) =>
        SendAsync(new SqlSocketError(id, "error", SqlBatch.Body(failure, context.TraceIdentifier).Error, !Session.IsAutocommit), SqlJsonContext.Default.SqlSocketError, cancellationToken);

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
        if (status is WebSocketCloseStatus.InvalidMessageType or WebSocketCloseStatus.PolicyViolation or WebSocketCloseStatus.MessageTooBig
            or WebSocketCloseStatus.InternalServerError)
        {
            _closed.CancelAfter(Shutdown.DrainBudget);
        }

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
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task CloseAfterFrameAsync()
    {
        await _frame.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseAsync(ServiceRestart, WriterMoving).ConfigureAwait(false);
        }
        finally
        {
            _frame.Release();
        }
    }

    public void Dispose()
    {
        _watches.ForEach(watch => watch.Dispose());
        _revoked.Dispose();
        _closed.Dispose();
        _frame.Dispose();
        _send.Dispose();
    }
}
