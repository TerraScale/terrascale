using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Qos;
using TerraBase.Features.Rules;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.Sql;

internal readonly union SocketAnswerOutcome(SocketAnswer, SqlRefusal);

internal sealed record SocketAnswer(IReadOnlyList<SqlResultSet> Results);

internal sealed record SocketMessage(byte[] Payload, long Started);

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

    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    private readonly SqlRunner runner = services.Runner;
    private readonly DatabaseAccess access = services.Access;
    private readonly IClusterCoordinator coordinator = services.Coordinator;
    private readonly TimeProvider time = services.Time;
    private readonly long _accepted = services.Time.GetTimestamp();

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

    public async Task RunAsync(ResolvedDatabase? target, IDisposable? preauthentication, SqlCausality causality, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(causality);
        _bookmark = causality.After;
        _mode = causality.Mode ?? target?.Database.DefaultConsistency ?? ConsistencyMode.Fast;
        if (target is null)
        {
            _preauthentication = preauthentication;
        }
        else if (await AdmitAsync(null, target, causality, cancellationToken) is { } refused)
        {
            await RefuseAsync(null, refused, cancellationToken);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveAsync(cancellationToken);
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

        if (_transaction is not null && time.GetElapsedTime(_transactionStarted, message.Started) > QosPolicy.MaxTransaction)
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
        SqlRefusal? resolved;
        try
        {
            resolved = await ResolveAsync(frame.Id, token, causality, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "authenticate within 5 seconds");
            return false;
        }

        if (resolved is { } refusal)
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
                ResolvedDatabase target => await AdmitAsync(id, target, causality, cancellationToken),
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

    private async ValueTask<SqlRefusal?> AdmitAsync(long? id, ResolvedDatabase target, SqlCausality causality, CancellationToken cancellationToken)
    {
        var tenant = await services.Tenants.ForAsync(target.Project, cancellationToken);
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
        var authenticating = _target is null;
        input.Progress = authenticating ? null : deadline.Progress;
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(), linked.Token);
                if (read.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync(WebSocketCloseStatus.NormalClosure, "closed");
                    return null;
                }

                if (read.MessageType == WebSocketMessageType.Binary)
                {
                    await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "frames are JSON text");
                    return null;
                }

                if (authenticating && message.Length + read.Count > MaxAuthFrameBytes)
                {
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, "the auth frame is at most 32 KiB");
                    return null;
                }

                if (message.Length + read.Count > MaxFrameBytes)
                {
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, "frames are at most 1 MiB");
                    return null;
                }

                message.Write(buffer, 0, read.Count);
                if (read.EndOfMessage)
                {
                    var started = input.Complete() ?? time.GetTimestamp();
                    return deadline.Complete() ? new SocketMessage(message.ToArray(), started) : null;
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            input.Progress = null;
            deadline.Complete();
            await deadline.Expiry;
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

        try
        {
            await socket.CloseOutputAsync(status, reason, CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
    }
}
