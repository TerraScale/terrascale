using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Qos;
using TerraBase.Features.Rules;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.Sql;

internal readonly union SocketAnswerOutcome(SocketAnswer, SqlRefusal);

internal sealed record SocketAnswer(IReadOnlyList<SqlResultSet> Results);

internal sealed record SocketMessage(byte[] Payload, long Started, long Completed);

internal sealed record SocketServices(
    SqlRunner Runner,
    DatabaseAccess Access,
    CallerAuthentication Authentication,
    IClusterCoordinator Coordinator,
    TenantDirectory Tenants,
    TenantSessions Sessions,
    TimeProvider Time);

internal sealed class SqlSocket(WebSocket socket, SocketInput input, bool v2, string? project, string? database, SocketServices services) : IAsyncDisposable
{
    public const int MaxFrameBytes = SqlFlow.MaxRequestBytes;

    private const string TransactionExpired = "the transaction expired: it was open for more than 5 seconds and was rolled back";

    private const int MaxAuthFrameBytes = 32 * 1024;

    private const int MaxPendingFrames = 512;

    private const long MaxPendingBytes = 4L * 1024 * 1024;

    private static readonly TimeSpan PartialDrainBudget = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PumpSettleBudget = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    private readonly SqlRunner runner = services.Runner;
    private readonly DatabaseAccess access = services.Access;
    private readonly IClusterCoordinator coordinator = services.Coordinator;
    private readonly TimeProvider time = services.Time;
    private readonly long _accepted = services.Time.GetTimestamp();
    private readonly CancellationTokenSource _pumpStop = new();
    private readonly Lock _progress = new();
    private Channel<SocketMessage>? _inbox;
    private Task _pump = Task.CompletedTask;
    private ReceiveDeadline? _waiting;
    private ReceiveDeadline? _inFlight;
    private (long Started, int Bytes, long Sequence)? _partial;
    private long _pendingBytes;
    private int _terminal;

    private ResolvedDatabase? _target;
    private ISqlDatabaseSession? _session;
    private ISqlTransaction? _transaction;
    private IDisposable? _preauthentication;
    private IDisposable? _tenantSession;
    private ConsistencyMode _mode;
    private bool _failed;
    private long _transactionStarted;
    private CausalBookmark _bookmark = CausalBookmark.Empty;

    private bool InTransaction => _transaction is not null || _failed;

    private bool ExpiredBeforeDispatch(SocketMessage message) =>
        time.GetElapsedTime(_transactionStarted, message.Started) > QosPolicy.MaxTransaction
        || (time.GetElapsedTime(_transactionStarted) > QosPolicy.MaxTransaction
            && time.GetElapsedTime(_transactionStarted, message.Completed) <= QosPolicy.MaxTransaction);

    public async Task RunAsync(ResolvedDatabase? target, IDisposable? preauthentication, SqlCausality causality, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(causality);
        _bookmark = causality.After;
        _mode = causality.Mode ?? target?.Database.DefaultConsistency ?? ConsistencyMode.Fast;
        if (target is null)
        {
            _preauthentication = preauthentication;
        }
        else if (await AdmitAsync(null, target, causality, inBand: false, cancellationToken) is { } refused)
        {
            await RefuseAsync(null, refused, cancellationToken);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var message = _target is null ? await ReceiveAsync(cancellationToken) : await NextAsync(Inbox(), cancellationToken);
            if (message is null)
            {
                return;
            }

            if (!await HandleAsync(message, causality, cancellationToken))
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (var settle = new CancellationTokenSource(PumpSettleBudget, time))
        {
            await _pump.WaitAsync(settle.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await _pumpStop.CancelAsync();
        await _pump;
        _pumpStop.Dispose();
        if (_transaction is { } transaction)
        {
            _transaction = null;
            await transaction.DisposeAsync();
        }

        EndPreauthentication();
        _tenantSession?.Dispose();
    }

    private async Task<bool> HandleAsync(SocketMessage message, SqlCausality causality, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        if (Parse(payload) is not { } frame)
        {
            await ErrorAsync(null, SqlRefusal.Invalid("frames are JSON objects with id and type", 0), cancellationToken);
            return true;
        }

        if (_target is null)
        {
            return await AuthenticateAsync(frame, causality, cancellationToken);
        }

        if (frame.Bookmark is { Length: > 0 } encoded)
        {
            if (!CausalBookmark.TryDecode(encoded, out var bookmark))
            {
                await ErrorAsync(frame.Id, new SqlRefusal("invalid_bookmark", "bookmark must be one bookmark returned by TerraScale", "22023", 0, StatusCodes.Status400BadRequest), cancellationToken);
                return true;
            }

            _bookmark = _bookmark.Merge(bookmark);
        }

        if (_transaction is not null && ExpiredBeforeDispatch(message))
        {
            await ExpireTransactionAsync(frame.Id);
            return false;
        }

        var outcome = frame.Type switch
        {
            "execute" or "batch" => await ExecuteAsync(frame, _target, cancellationToken),
            "begin" => await BeginAsync(Mode(frame.Consistency) ?? _mode, cancellationToken),
            "commit" => await EndTransactionAsync(commit: true, cancellationToken),
            "rollback" => await EndTransactionAsync(commit: false, cancellationToken),
            "auth" => SqlRefusal.Invalid("the socket is already authenticated", 0),
            _ => SqlRefusal.Invalid("type is execute, batch, begin, commit or rollback", 0),
        };
        await (outcome switch
        {
            SocketAnswer answer => ResultAsync(frame.Id, answer.Results, cancellationToken),
            SqlRefusal refusal => ErrorAsync(frame.Id, refusal, cancellationToken),
        });
        return true;
    }

    private async Task<bool> AuthenticateAsync(SocketFrame frame, SqlCausality causality, CancellationToken cancellationToken)
    {
        if (frame is not { Type: "auth", Token: { Length: > 0 and <= 16 * 1024 } token })
        {
            await ErrorAsync(frame.Id, new SqlRefusal("unauthorized", "send {\"type\":\"auth\",\"token\":\"…\"} first", "28000", 0, StatusCodes.Status401Unauthorized), cancellationToken);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized");
            return false;
        }

        var remaining = AuthTimeout - time.GetElapsedTime(_accepted);
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var resolving = ResolveAsync(frame.Id, token, causality, linked.Token).AsTask();
        await ((Task)resolving).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (resolving.IsCanceled && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "authenticate within 5 seconds");
            return false;
        }

        if (await resolving is { } refusal)
        {
            await RefuseAsync(frame.Id, refusal, cancellationToken);
            return false;
        }

        await ResultAsync(frame.Id, [], cancellationToken);
        return true;
    }

    private async ValueTask<SqlRefusal?> ResolveAsync(long? id, string token, SqlCausality causality, CancellationToken cancellationToken)
    {
        var resolvedOutcome = await services.Authentication.AuthenticateBearerAsync(token, cancellationToken);
        return resolvedOutcome switch
        {
            Caller caller => await access.DataAsync(caller, project, database, cancellationToken) switch
            {
                ResolvedDatabase target => await AdmitAsync(id, target, causality, inBand: true, cancellationToken),
                InvalidInput invalid => new SqlRefusal(invalid.Code, invalid.Message, "22023", 0, StatusCodes.Status400BadRequest),
                CredentialRejected unauthorized => new SqlRefusal("unauthorized", unauthorized.Message ?? "unauthorized", "28000", 0, StatusCodes.Status401Unauthorized),
                CallerForbidden forbidden => new SqlRefusal("forbidden_scope", forbidden.Message, "42501", 0, StatusCodes.Status403Forbidden),
                ResourceNotFound missing => new SqlRefusal(missing.Code, missing.Message ?? "the database does not exist", "3D000", 0, StatusCodes.Status404NotFound),
                BackendUnavailable unavailable => SqlRefusal.From(unavailable),
            },
            CredentialRejected unauthorized => new SqlRefusal("unauthorized", unauthorized.Message ?? "unauthorized", "28000", 0, StatusCodes.Status401Unauthorized),
            BackendUnavailable unavailable => SqlRefusal.From(unavailable),
        };
    }

    private async ValueTask<SqlRefusal?> AdmitAsync(long? id, ResolvedDatabase target, SqlCausality causality, bool inBand, CancellationToken cancellationToken)
    {
        var tenant = await services.Tenants.ForAsync(target.Project, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (inBand && time.GetElapsedTime(_accepted) >= AuthTimeout)
        {
            return new SqlRefusal("unauthorized", "authenticate within 5 seconds", "28000", 0, StatusCodes.Status401Unauthorized);
        }

        var admitted = tenant switch
        {
            Tenant known => services.Sessions.TryOpen(known) switch
            {
                TenantSessions.TenantSession session => Accept(target, causality, session),
                TenantBusy busy => new SqlRefusal("tenant_busy", TenantSessions.LimitMessage, "53300", 0, StatusCodes.Status429TooManyRequests, busy.RetryAfter),
            },
            NoTenant => Accept(target, causality, null),
            BackendUnavailable unavailable => SqlRefusal.From(unavailable),
        };
        return admitted;
    }

    private SqlRefusal? Accept(ResolvedDatabase target, SqlCausality causality, IDisposable? session)
    {
        EndPreauthentication();
        _tenantSession = session;
        _target = target;
        _mode = causality.Mode ?? target.Database.DefaultConsistency;
        return null;
    }

    private void EndPreauthentication()
    {
        if (_preauthentication is { } preauthentication)
        {
            _preauthentication = null;
            preauthentication.Dispose();
        }
    }

    private async Task RefuseAsync(long? id, SqlRefusal refusal, CancellationToken cancellationToken)
    {
        await ErrorAsync(id, refusal, cancellationToken);
        await CloseAsync(WebSocketCloseStatus.PolicyViolation, refusal.Code == "tenant_busy" ? TenantSessions.LimitMessage : refusal.Code);
    }

    private async ValueTask<SocketAnswerOutcome> ExecuteAsync(SocketFrame frame, ResolvedDatabase target, CancellationToken cancellationToken)
    {
        var mode = Mode(frame.Consistency);
        return SqlWire.Statements(frame.Statements) switch
        {
            IReadOnlyList<SqlStatement> statements => SqlPlan.Batch(statements, allowTransactionControl: true) switch
            {
                SqlBatch { Statements: [{ Kind: StatementKind.Begin } begin] } => await BeginAsync(begin.BeginMode ?? mode ?? _mode, cancellationToken),
                SqlBatch { Statements: [{ Kind: StatementKind.Commit }] } => await EndTransactionAsync(commit: true, cancellationToken),
                SqlBatch { Statements: [{ Kind: StatementKind.Rollback }] } => await EndTransactionAsync(commit: false, cancellationToken),
                SqlBatch batch when InTransaction => await InTransactionAsync(target, batch, cancellationToken),
                SqlBatch batch => await AutocommitAsync(target, batch, mode, cancellationToken),
                SqlRefusal refusal => refusal,
            },
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<SocketAnswerOutcome> AutocommitAsync(ResolvedDatabase target, SqlBatch batch, ConsistencyMode? mode, CancellationToken cancellationToken)
    {
        if (batch.Statements.Any(static statement => statement.Kind is StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback))
        {
            return new SqlRefusal("transaction_state", "BEGIN, COMMIT and ROLLBACK go in their own frame", "25000", 0, StatusCodes.Status400BadRequest);
        }

        var outcome = await runner.RunAsync(target, batch.Sql, new SqlCausality(batch.Mode ?? mode ?? _mode, _bookmark), cancellationToken);
        return outcome switch
        {
            SqlRun run => Answer(run),
            SqlRefusal refusal => refusal,
        };
    }

    private SocketAnswer Answer(SqlRun run)
    {
        _bookmark = _bookmark.Merge(run.Bookmark);
        return new SocketAnswer(run.Results);
    }

    private async ValueTask<SocketAnswerOutcome> BeginAsync(ConsistencyMode mode, CancellationToken cancellationToken)
    {
        if (InTransaction || _target is not { } target)
        {
            return new SqlRefusal("transaction_state", "a transaction is already open", "25001", 0, StatusCodes.Status400BadRequest);
        }

        var outcome = await runner.WaitAsync(target, _bookmark, cancellationToken);
        return outcome switch
        {
            Completed => await access.OpenAsync(target.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => await OpenTransactionAsync(session, mode, cancellationToken),
                BackendUnavailable unavailable => SqlRefusal.From(unavailable),
            },
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<SocketAnswerOutcome> OpenTransactionAsync(ISqlDatabaseSession session, ConsistencyMode mode, CancellationToken cancellationToken)
    {
        var outcome = await coordinator.BeginTransactionAsync(session, new SqlTransactionOptions(mode, SqlAccess.Write, _bookmark), cancellationToken);
        return outcome switch
        {
            ISqlTransaction transaction => Opened(session, transaction, mode),
            StorageBusy busy => SqlRefusal.From(busy),
            ClusterTimeout timeout => SqlRefusal.From(timeout),
        };
    }

    private SocketAnswer Opened(ISqlDatabaseSession session, ISqlTransaction transaction, ConsistencyMode mode)
    {
        _session = session;
        _transaction = transaction;
        _mode = mode;
        _failed = false;
        _transactionStarted = time.GetTimestamp();
        return new SocketAnswer([]);
    }

    private async ValueTask<SocketAnswerOutcome> InTransactionAsync(ResolvedDatabase target, SqlBatch batch, CancellationToken cancellationToken)
    {
        if (_failed || _transaction is not { } transaction)
        {
            return new SqlRefusal("transaction_aborted", "the transaction failed; only rollback is accepted", "25P02", 0, StatusCodes.Status400BadRequest);
        }

        var outcome = await runner.PrepareAsync(target, batch.Statements, cancellationToken, _session);
        return outcome switch
        {
            EnforcedSql enforced => await RunInTransactionAsync(target, transaction, enforced.Statements, batch.Writes, cancellationToken),
            SqlRefusal denied => await FailAsync(denied),
        };
    }

    private async ValueTask<SocketAnswerOutcome> RunInTransactionAsync(
        ResolvedDatabase target, ISqlTransaction transaction, IReadOnlyList<SqlStatement> statements, bool writes, CancellationToken cancellationToken)
    {
        List<SqlResultSet> results = [];
        foreach (var statement in statements)
        {
            var failureOutcome = await transaction.ExecuteAsync(statement, cancellationToken);
            var failure = failureOutcome switch
            {
                SqlResultSet result => Collect(results, result),
                SqlError error => SqlRefusal.From(error),
                StorageBusy busy => SqlRefusal.From(busy),
                StorageTimeout timeout => SqlRefusal.From(timeout),
            };
            if (failure is not null)
            {
                return await FailAsync(failure);
            }
        }

        runner.Meter(target, results, writes, _mode);
        return new SocketAnswer(results);
    }

    private async ValueTask<SqlRefusal> FailAsync(SqlRefusal refusal)
    {
        _failed = true;
        await RollbackAsync();
        return refusal;
    }

    private async ValueTask RollbackAsync()
    {
        if (_transaction is not { } transaction)
        {
            return;
        }

        _transaction = null;
        await using (transaction)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private async ValueTask<SocketAnswerOutcome> EndTransactionAsync(bool commit, CancellationToken cancellationToken)
    {
        if (_failed)
        {
            _failed = false;
            await RollbackAsync();
            return commit
                ? new SqlRefusal("transaction_rolled_back", "the transaction failed earlier and was rolled back", "40000", 0, StatusCodes.Status400BadRequest)
                : new SocketAnswer([]);
        }

        if (_transaction is not { } transaction || _session is not { } session)
        {
            return new SqlRefusal("transaction_state", "no transaction is open", "25P01", 0, StatusCodes.Status400BadRequest);
        }

        _transaction = null;
        await using (transaction)
        {
            if (!commit)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new SocketAnswer([]);
            }

            var outcome = await coordinator.CommitTransactionAsync(session, transaction, cancellationToken);
            return outcome switch
            {
                CommitMetadata committed => Committed(committed),
                SerializationConflict conflict => SqlRefusal.From(conflict),
                SchemaMismatch mismatch => SqlRefusal.From(mismatch),
                ClusterUnavailable unavailable => SqlRefusal.From(unavailable),
                ClusterTimeout timeout => SqlRefusal.From(timeout),
                StorageBusy busy => SqlRefusal.From(busy),
            };
        }
    }

    private SocketAnswer Committed(CommitMetadata commit)
    {
        _bookmark = _bookmark.Merge(commit.Bookmark);
        return new SocketAnswer([]);
    }

    private static SqlRefusal? Collect(List<SqlResultSet> results, SqlResultSet result)
    {
        results.Add(result);
        return null;
    }

    private static ConsistencyMode? Mode(string? value) => value is not null && ConsistencyHeaders.TryParse(value, out var mode) ? mode : null;

    private static SocketFrame? Parse(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, SqlSocketJsonContext.Default.SocketFrame);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task ResultAsync(long? id, IReadOnlyList<SqlResultSet> results, CancellationToken cancellationToken) => v2
        ? SendAsync(JsonSerializer.SerializeToUtf8Bytes(new SocketServerFrame("result", id ?? 0, SqlWire.Results(results), _bookmark.Encode(), InTransaction, null),
            SqlSocketJsonContext.Default.SocketServerFrame), cancellationToken)
        : SendAsync(JsonSerializer.SerializeToUtf8Bytes(new LegacySocketResult(id, "result", SqlWire.Legacy(results).Results, InTransaction),
            SqlSocketJsonContext.Default.LegacySocketResult), cancellationToken);

    private Task ErrorAsync(long? id, SqlRefusal refusal, CancellationToken cancellationToken) => v2
        ? SendAsync(JsonSerializer.SerializeToUtf8Bytes(new SocketServerFrame("error", id ?? 0, null, _bookmark.Encode(), InTransaction, refusal.Body),
            SqlSocketJsonContext.Default.SocketServerFrame), cancellationToken)
        : SendAsync(JsonSerializer.SerializeToUtf8Bytes(new LegacySocketError(id, "error",
            new LegacySocketErrorBody(refusal.Code, refusal.Message, refusal.Statement, refusal.SqlState), InTransaction),
            SqlSocketJsonContext.Default.LegacySocketError), cancellationToken);

    private async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        catch (WebSocketException)
        {
        }
    }

    private TimeSpan IdleBudget() =>
        _target is null ? AuthTimeout - time.GetElapsedTime(_accepted)
        : _transaction is not null ? QosPolicy.MaxTransaction - time.GetElapsedTime(_transactionStarted)
        : QosPolicy.IdleTimeout;

    private async Task<SocketMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var deadline = new ReceiveDeadline(time, IdleBudget(), ExpireAsync);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            if (await ReadMessageAsync(buffer, MaxAuthFrameBytes, "the auth frame is at most 32 KiB", linked.Token) is not { } payload)
            {
                return null;
            }

            var arrived = input.Complete() ?? new ArrivedMessage(time.GetTimestamp(), time.GetTimestamp(), 0);
            return deadline.Complete() ? new SocketMessage(payload, arrived.Started, arrived.Completed) : null;
        }
        finally
        {
            deadline.Complete();
            await deadline.Expiry;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private ChannelReader<SocketMessage> Inbox()
    {
        if (_inbox is { } existing)
        {
            return existing.Reader;
        }

        var inbox = Channel.CreateBounded<SocketMessage>(new BoundedChannelOptions(MaxPendingFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        _inbox = inbox;
        input.Failed = MarkTerminal;
        input.Progress = Progressed;
        _pump = PumpAsync(inbox.Writer, _pumpStop.Token);
        return inbox.Reader;
    }

    private void Progressed(long started, int bytes, long sequence)
    {
        lock (_progress)
        {
            _partial = (started, bytes, sequence);
            _inFlight ??= new ReceiveDeadline(time, QosPolicy.MessageBudget(bytes) - time.GetElapsedTime(started), ExpirePartialAsync);
            _inFlight.Progress(started, bytes);
            _waiting?.Progress(started, bytes);
        }
    }

    private async Task ExpirePartialAsync(bool partial)
    {
        MarkTerminal();
        await CloseAsync(WebSocketCloseStatus.PolicyViolation, "incomplete message timeout exceeded");
        _pumpStop.CancelAfter(PartialDrainBudget);
    }

    private async Task<bool> ArrivedOnTimeAsync()
    {
        ReceiveDeadline? inFlight;
        lock (_progress)
        {
            inFlight = _inFlight;
            _inFlight = null;
        }

        if (inFlight is null)
        {
            return true;
        }

        var onTime = inFlight.Complete();
        await inFlight.Expiry;
        inFlight.Dispose();
        return onTime;
    }

    private async Task PumpAsync(ChannelWriter<SocketMessage> inbox, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (await ReadMessageAsync(buffer, MaxFrameBytes, "frames are at most 1 MiB", cancellationToken) is { } payload)
            {
                if (!await ArrivedOnTimeAsync())
                {
                    return;
                }

                var arrived = input.Complete() ?? new ArrivedMessage(time.GetTimestamp(), time.GetTimestamp(), 0);
                var message = new SocketMessage(payload, arrived.Started, arrived.Completed);

                bool published;
                lock (_progress)
                {
                    published = Interlocked.Add(ref _pendingBytes, message.Payload.Length) <= MaxPendingBytes && inbox.TryWrite(message);
                    if (published && _partial is { } partial && partial.Sequence == arrived.Sequence)
                    {
                        _partial = null;
                    }
                }

                if (!published)
                {
                    MarkTerminal();
                    await CloseAsync(WebSocketCloseStatus.PolicyViolation, "too many frames are waiting; wait for answers before sending more");
                    return;
                }
            }
        }
        finally
        {
            MarkTerminal();
            input.Progress = null;
            input.Failed = null;
            inbox.TryComplete();
            await ArrivedOnTimeAsync();
            lock (_progress)
            {
                _partial = null;
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<SocketMessage?> NextAsync(ChannelReader<SocketMessage> inbox, CancellationToken cancellationToken)
    {
        ReceiveDeadline armed;
        lock (_progress)
        {
            if (inbox.TryRead(out var ready))
            {
                Interlocked.Add(ref _pendingBytes, -ready.Payload.Length);
                return Volatile.Read(ref _terminal) == 0 ? ready : null;
            }

            armed = new ReceiveDeadline(time, IdleBudget(), ExpireAsync, _partial is { } partial ? (partial.Started, partial.Bytes) : null);
            _waiting = armed;
        }

        using var deadline = armed;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            if (!await inbox.WaitToReadAsync(linked.Token) || !inbox.TryRead(out var message))
            {
                return null;
            }

            Interlocked.Add(ref _pendingBytes, -message.Payload.Length);
            return deadline.Complete() && Volatile.Read(ref _terminal) == 0 ? message : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (_progress)
            {
                _waiting = null;
            }

            deadline.Complete();
            await deadline.Expiry;
        }
    }

    private async Task<byte[]?> ReadMessageAsync(byte[] buffer, int limit, string limitReason, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (read.MessageType == WebSocketMessageType.Close)
                {
                    MarkTerminal();
                    await CloseAsync(WebSocketCloseStatus.NormalClosure, "closed");
                    return null;
                }

                if (read.MessageType == WebSocketMessageType.Binary)
                {
                    MarkTerminal();
                    await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "frames are JSON text");
                    return null;
                }

                if (message.Length + read.Count > limit)
                {
                    MarkTerminal();
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, limitReason);
                    return null;
                }

                message.Write(buffer, 0, read.Count);
                if (read.EndOfMessage)
                {
                    return message.ToArray();
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
            MarkTerminal();
            return null;
        }
    }

    private void MarkTerminal()
    {
        Volatile.Write(ref _terminal, 1);
        _inbox?.Writer.TryComplete();
    }

    private async Task ExpireAsync(bool partial)
    {
        if (_target is null)
        {
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "authenticate within 5 seconds");
        }
        else if (partial)
        {
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "incomplete message timeout exceeded");
        }
        else if (_transaction is not null)
        {
            await ExpireTransactionAsync(null);
        }
        else
        {
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "session idle timeout exceeded");
        }
    }

    private async Task ExpireTransactionAsync(long? id)
    {
        await RollbackAsync();
        await ErrorAsync(id, new SqlRefusal("query_canceled", TransactionExpired, "57014", 0, StatusCodes.Status400BadRequest), CancellationToken.None);
        await CloseAsync(WebSocketCloseStatus.PolicyViolation, TransactionExpired);
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var bounded = new CancellationTokenSource(CloseBudget, time);
        try
        {
            await socket.CloseOutputAsync(status, reason, bounded.Token);
        }
        catch (Exception error) when (error is WebSocketException || (error is OperationCanceledException && bounded.IsCancellationRequested))
        {
        }
    }
}
