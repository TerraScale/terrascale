using System.Buffers;
using System.Net.WebSockets;
using System.Text;
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

internal readonly record struct SocketAnswer(IReadOnlyList<SqlResultSet> Results);

internal readonly record struct SocketMessage(byte[] Buffer, int Length, long Started, long Completed);

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

    private const int MaxRetainedBufferBytes = 64 * 1024;

    private const int SqlCacheSlots = 32;

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
    private readonly SqlResultSet[] _singleResult = new SqlResultSet[1];
    private readonly (ulong Hash, byte[]? Utf8, string? Sql)[] _sqlCache = new (ulong, byte[]?, string?)[SqlCacheSlots];
    private ArrayBufferWriter<byte> _sendBuffer = new(4096);
    private Utf8JsonWriter? _sendWriter;
    private ArrayBufferWriter<byte>? _multiFrame;
    private Channel<SocketMessage>? _inbox;
    private Task _pump = Task.CompletedTask;
    private ReceiveDeadline? _waitingDeadline;
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
    private CausalBookmark _encodedBookmarkKey = CausalBookmark.Empty;
    private string _encodedBookmarkValue = "";
    private byte[] _encodedBookmarkUtf8 = [];
    private bool _hasEncodedBookmark;

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

        _waitingDeadline = new ReceiveDeadline(time, ExpireAsync, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var received = _target is null ? await ReceiveAsync() : await NextAsync(Inbox());
            if (received is not { } message)
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
        if (_inbox is { } inbox)
        {
            while (inbox.Reader.TryRead(out var leftover))
            {
                ReturnPayload(leftover.Buffer);
            }
        }

        _waitingDeadline?.Dispose();
        _inFlight?.Dispose();
        _sendWriter?.Dispose();
        if (_transaction is { } transaction)
        {
            _transaction = null;
            await transaction.DisposeAsync();
        }

        EndPreauthentication();
        _tenantSession?.Dispose();
    }

    private async ValueTask<bool> HandleAsync(SocketMessage message, SqlCausality causality, CancellationToken cancellationToken)
    {
        ParsedSocketFrame? parsed;
        try
        {
            parsed = Parse(message.Buffer.AsSpan(0, message.Length));
        }
        finally
        {
            ReturnPayload(message.Buffer);
        }

        if (parsed is not { } frame)
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

            _bookmark = _bookmark.IsEmpty ? bookmark : _bookmark.Merge(bookmark);
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

    private async Task<bool> AuthenticateAsync(ParsedSocketFrame frame, SqlCausality causality, CancellationToken cancellationToken)
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

    private async ValueTask<SocketAnswerOutcome> ExecuteAsync(ParsedSocketFrame frame, ResolvedDatabase target, CancellationToken cancellationToken)
    {
        var mode = Mode(frame.Consistency);
        if (frame.StatementsError is { } statementsError)
        {
            return statementsError;
        }

        if (frame.Statements is not { Length: > 0 } statements)
        {
            return SqlRefusal.Invalid("statements holds 1 to 256 statements", 0);
        }

        if (statements.Length == 1
            && !InTransaction
            && target.EndUser is null
            && SqlPlan.TryClassifyCached(statements[0].Sql, out var kind)
            && kind is not (StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback))
        {
            var outcome = await runner.RunSingleAutocommitAsync(target, statements, kind, mode ?? _mode, _bookmark, cancellationToken);
            return outcome switch
            {
                SqlRun run => Answer(run),
                SqlRefusal refusal => refusal,
            };
        }

        return SqlPlan.Batch(statements, allowTransactionControl: true) switch
        {
            SqlBatch { Statements: [{ Kind: StatementKind.Begin } begin] } => await BeginAsync(begin.BeginMode ?? mode ?? _mode, cancellationToken),
            SqlBatch { Statements: [{ Kind: StatementKind.Commit }] } => await EndTransactionAsync(commit: true, cancellationToken),
            SqlBatch { Statements: [{ Kind: StatementKind.Rollback }] } => await EndTransactionAsync(commit: false, cancellationToken),
            SqlBatch batch when InTransaction => await InTransactionAsync(target, batch, cancellationToken),
            SqlBatch batch => await AutocommitAsync(target, batch, mode, cancellationToken),
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<SocketAnswerOutcome> AutocommitAsync(ResolvedDatabase target, SqlBatch batch, ConsistencyMode? mode, CancellationToken cancellationToken)
    {
        for (var index = 0; index < batch.Statements.Count; index++)
        {
            if (batch.Statements[index].Kind is StatementKind.Begin or StatementKind.Commit or StatementKind.Rollback)
            {
                return new SqlRefusal("transaction_state", "BEGIN, COMMIT and ROLLBACK go in their own frame", "25000", 0, StatusCodes.Status400BadRequest);
            }
        }

        var outcome = await runner.RunPlannedAsync(target, batch, new SqlCausality(batch.Mode ?? mode ?? _mode, _bookmark), serviceRead: false, cancellationToken);
        return outcome switch
        {
            SqlRun run => Answer(run),
            SqlRefusal refusal => refusal,
        };
    }

    private SocketAnswer Answer(SqlRun run)
    {
        _bookmark = _bookmark.IsEmpty ? run.Bookmark : _bookmark.Merge(run.Bookmark);
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

        var outcome = await runner.PrepareBatchAsync(target, batch, transaction, cancellationToken);
        return outcome switch
        {
            EnforcedSql enforced => await RunInTransactionAsync(target, transaction, enforced.Statements, batch.Writes, cancellationToken),
            SqlRefusal denied => await FailAsync(denied),
        };
    }

    private async ValueTask<SocketAnswerOutcome> RunInTransactionAsync(
        ResolvedDatabase target, ISqlTransaction transaction, IReadOnlyList<SqlStatement> statements, bool writes, CancellationToken cancellationToken)
    {
        var results = statements.Count == 1 ? _singleResult : new SqlResultSet[statements.Count];
        for (var index = 0; index < statements.Count; index++)
        {
            var failureOutcome = await transaction.ExecuteAsync(statements[index], cancellationToken);
            var failure = failureOutcome switch
            {
                SqlResultSet result => AssignResult(results, index, result),
                SqlError error => SqlRefusal.From(error),
                StorageBusy busy => SqlRefusal.From(busy),
                StorageTimeout timeout => SqlRefusal.From(timeout),
            };
            if (failure is not null)
            {
                if (ReferenceEquals(results, _singleResult))
                {
                    Array.Clear(_singleResult);
                }

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
        _bookmark = _bookmark.IsEmpty ? commit.Bookmark : _bookmark.Merge(commit.Bookmark);
        return new SocketAnswer([]);
    }

    private static SqlRefusal? AssignResult(SqlResultSet[] results, int index, SqlResultSet result)
    {
        results[index] = result;
        return null;
    }

    private static ConsistencyMode? Mode(string? value) => value is not null && ConsistencyHeaders.TryParse(value, out var mode) ? mode : null;

    private ParsedSocketFrame? Parse(ReadOnlySpan<byte> payload)
    {
        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            long? id = null;
            string? type = null;
            SqlStatement[]? statements = null;
            SqlRefusal? statementsError = null;
            string? token = null;
            string? consistency = null;
            string? bookmark = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return null;
                }

                if (SqlWire.PropertyEquals(ref reader, "id"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        id = null;
                    }
                    else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var parsedId))
                    {
                        id = parsedId;
                    }
                    else
                    {
                        return null;
                    }
                }
                else if (SqlWire.PropertyEquals(ref reader, "type"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        type = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        type = ReadFrameType(ref reader);
                    }
                    else
                    {
                        return null;
                    }
                }
                else if (SqlWire.PropertyEquals(ref reader, "statements"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        statements = null;
                        statementsError = null;
                    }
                    else if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        if (!SqlWire.TryReadStatements(ref reader, out statements, out statementsError, _sqlCache))
                        {
                            return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }
                else if (SqlWire.PropertyEquals(ref reader, "token"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        token = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        token = reader.GetString();
                    }
                    else
                    {
                        return null;
                    }
                }
                else if (SqlWire.PropertyEquals(ref reader, "consistency"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        consistency = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        consistency = ReadConsistency(ref reader);
                    }
                    else
                    {
                        return null;
                    }
                }
                else if (SqlWire.PropertyEquals(ref reader, "bookmark"u8))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        bookmark = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        bookmark = reader.GetString();
                    }
                    else
                    {
                        return null;
                    }
                }
                else
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    reader.Skip();
                }
            }

            if (reader.Read())
            {
                return null;
            }

            return new ParsedSocketFrame(id, type, statements, statementsError, token, consistency, bookmark);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadFrameType(ref Utf8JsonReader reader) =>
        reader.ValueTextEquals("execute"u8) ? "execute"
        : reader.ValueTextEquals("batch"u8) ? "batch"
        : reader.ValueTextEquals("begin"u8) ? "begin"
        : reader.ValueTextEquals("commit"u8) ? "commit"
        : reader.ValueTextEquals("rollback"u8) ? "rollback"
        : reader.ValueTextEquals("auth"u8) ? "auth"
        : reader.GetString();

    private static string? ReadConsistency(ref Utf8JsonReader reader) =>
        reader.ValueTextEquals("fast"u8) ? "fast"
        : reader.ValueTextEquals("strong"u8) ? "strong"
        : reader.GetString();

    private readonly record struct ParsedSocketFrame(
        long? Id,
        string? Type,
        SqlStatement[]? Statements,
        SqlRefusal? StatementsError,
        string? Token,
        string? Consistency,
        string? Bookmark);

    private ReadOnlySpan<byte> EncodedBookmarkUtf8()
    {
        if (_hasEncodedBookmark && ReferenceEquals(_encodedBookmarkKey, _bookmark))
        {
            return _encodedBookmarkUtf8;
        }

        if (_hasEncodedBookmark && _encodedBookmarkKey.Equals(_bookmark))
        {
            _encodedBookmarkKey = _bookmark;
            return _encodedBookmarkUtf8;
        }

        _encodedBookmarkKey = _bookmark;
        _encodedBookmarkValue = SqlRunner.EncodeBookmark(_bookmark);
        _encodedBookmarkUtf8 = _encodedBookmarkValue.Length == 0 ? [] : Encoding.ASCII.GetBytes(_encodedBookmarkValue);
        _hasEncodedBookmark = true;
        return _encodedBookmarkUtf8;
    }

    private string EncodedBookmark()
    {
        _ = EncodedBookmarkUtf8();
        return _encodedBookmarkValue;
    }

    private Utf8JsonWriter ResetSendWriter()
    {
        _sendBuffer.ResetWrittenCount();
        if (_sendWriter is { } writer)
        {
            writer.Reset(_sendBuffer);
            return writer;
        }

        writer = new Utf8JsonWriter(_sendBuffer);
        _sendWriter = writer;
        return writer;
    }

    private ValueTask ResultAsync(long? id, IReadOnlyList<SqlResultSet> results, CancellationToken cancellationToken)
    {
        _sendBuffer.ResetWrittenCount();
        if (v2)
        {
            WriteV2Result(_sendBuffer, id ?? 0, results, EncodedBookmarkUtf8(), InTransaction);
        }
        else
        {
            WriteLegacyResult(_sendBuffer, id, results, InTransaction);
        }

        if (ReferenceEquals(results, _singleResult))
        {
            Array.Clear(_singleResult);
        }

        return SendBufferedAsync(cancellationToken);
    }

    private static void WriteV2Result(IBufferWriter<byte> output, long id, IReadOnlyList<SqlResultSet> results, ReadOnlySpan<byte> bookmarkUtf8, bool inTransaction)
    {
        var json = new RawJsonBuffer(output);
        json.Write("{\"type\":\"result\",\"id\":"u8);
        json.WriteInt64(id);
        json.Write(",\"results\":["u8);
        for (var i = 0; i < results.Count; i++)
        {
            if (i > 0)
            {
                json.WriteByte((byte)',');
            }

            var result = results[i];
            json.Write("{\"columns\":["u8);
            var columns = result.Columns;
            for (var j = 0; j < columns.Count; j++)
            {
                if (j > 0)
                {
                    json.WriteByte((byte)',');
                }

                var column = columns[j];
                json.Write("{\"name\":"u8);
                json.WriteJsonString(column.Name);
                if (column.DeclaredType is { } declaredType)
                {
                    json.Write(",\"type\":"u8);
                    json.WriteJsonString(declaredType);
                }

                json.WriteByte((byte)'}');
            }

            json.Write("],\"rows\":"u8);
            SqlRowsConverter.WriteRows(ref json, result.Rows);
            json.Write(",\"rowsAffected\":"u8);
            json.WriteInt64(result.RowsAffected);
            json.Write(",\"lastInsertRowId\":"u8);
            json.WriteInt64(result.LastInsertRowId);
            json.WriteByte((byte)'}');
        }

        json.Write("],\"bookmark\":\""u8);
        json.Write(bookmarkUtf8);
        json.Write(inTransaction ? "\",\"transaction\":true}"u8 : "\",\"transaction\":false}"u8);
        json.Flush();
    }

    private static void WriteLegacyResult(IBufferWriter<byte> output, long? id, IReadOnlyList<SqlResultSet> results, bool inTransaction)
    {
        var json = new RawJsonBuffer(output);
        if (id is { } frameId)
        {
            json.Write("{\"id\":"u8);
            json.WriteInt64(frameId);
            json.Write(",\"type\":\"result\",\"results\":["u8);
        }
        else
        {
            json.Write("{\"type\":\"result\",\"results\":["u8);
        }

        for (var i = 0; i < results.Count; i++)
        {
            if (i > 0)
            {
                json.WriteByte((byte)',');
            }

            var result = results[i];
            json.Write("{\"columns\":["u8);
            var columns = result.Columns;
            for (var j = 0; j < columns.Count; j++)
            {
                if (j > 0)
                {
                    json.WriteByte((byte)',');
                }

                var column = columns[j];
                json.Write("{\"name\":"u8);
                json.WriteJsonString(column.Name);
                if (column.DeclaredType is { } decltype)
                {
                    json.Write(",\"decltype\":"u8);
                    json.WriteJsonString(decltype);
                }

                json.WriteByte((byte)'}');
            }

            json.Write("],\"rows\":"u8);
            SqlRowsConverter.WriteRows(ref json, result.Rows);
            json.Write(",\"rows_affected\":"u8);
            json.WriteInt64(result.RowsAffected);
            json.Write(",\"last_insert_rowid\":"u8);
            json.WriteInt64(result.LastInsertRowId);
            json.WriteByte((byte)'}');
        }

        json.Write(inTransaction ? "],\"transaction\":true}"u8 : "],\"transaction\":false}"u8);
        json.Flush();
    }

    private ValueTask ErrorAsync(long? id, SqlRefusal refusal, CancellationToken cancellationToken)
    {
        var writer = ResetSendWriter();
        if (v2)
        {
            JsonSerializer.Serialize(writer, new SocketServerFrame("error", id ?? 0, null, EncodedBookmark(), InTransaction, refusal.Body),
                SqlSocketJsonContext.Default.SocketServerFrame);
        }
        else
        {
            JsonSerializer.Serialize(writer, new LegacySocketError(id, "error",
                new LegacySocketErrorBody(refusal.Code, refusal.Message, refusal.Statement, refusal.SqlState), InTransaction),
                SqlSocketJsonContext.Default.LegacySocketError);
        }

        writer.Flush();
        return SendBufferedAsync(cancellationToken);
    }

    private async ValueTask SendBufferedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await socket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            if (_sendBuffer.Capacity > MaxRetainedBufferBytes)
            {
                _sendBuffer = new ArrayBufferWriter<byte>(4096);
                _sendWriter?.Reset(_sendBuffer);
            }
        }
    }

    private TimeSpan IdleBudget() =>
        _target is null ? AuthTimeout - time.GetElapsedTime(_accepted)
        : _transaction is not null ? QosPolicy.MaxTransaction - time.GetElapsedTime(_transactionStarted)
        : QosPolicy.IdleTimeout;

    private async ValueTask<SocketMessage?> ReceiveAsync()
    {
        if (_waitingDeadline is not { } deadline)
        {
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        deadline.Arm(IdleBudget());
        try
        {
            if (await ReadMessageAsync(buffer, MaxAuthFrameBytes, "the auth frame is at most 32 KiB", deadline.Token) is not { } payload)
            {
                return null;
            }

            var arrived = input.Complete() ?? new ArrivedMessage(time.GetTimestamp(), time.GetTimestamp(), 0);
            if (!deadline.Complete())
            {
                ReturnPayload(payload.Buffer);
                return null;
            }

            return new SocketMessage(payload.Buffer, payload.Length, arrived.Started, arrived.Completed);
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
        _inFlight = new ReceiveDeadline(time, ExpirePartialAsync);
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
            _inFlight?.ArmPartial(started, bytes);
            _waiting?.Progress(started, bytes);
        }
    }

    private async Task ExpirePartialAsync(bool partial)
    {
        MarkTerminal();
        await CloseAsync(WebSocketCloseStatus.PolicyViolation, "incomplete message timeout exceeded");
        _pumpStop.CancelAfter(PartialDrainBudget);
    }

    private ValueTask<bool> ArrivedOnTimeAsync()
    {
        if (_inFlight is not { } inFlight)
        {
            return ValueTask.FromResult(true);
        }

        if (inFlight.Complete())
        {
            return ValueTask.FromResult(true);
        }

        return AwaitInFlightExpiryAsync(inFlight);
    }

    private static async ValueTask<bool> AwaitInFlightExpiryAsync(ReceiveDeadline inFlight)
    {
        await inFlight.Expiry;
        return false;
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
                    ReturnPayload(payload.Buffer);
                    return;
                }

                var arrived = input.Complete() ?? new ArrivedMessage(time.GetTimestamp(), time.GetTimestamp(), 0);
                var message = new SocketMessage(payload.Buffer, payload.Length, arrived.Started, arrived.Completed);

                bool published;
                lock (_progress)
                {
                    published = Interlocked.Add(ref _pendingBytes, message.Length) <= MaxPendingBytes && inbox.TryWrite(message);
                    if (published && _partial is { } partial && partial.Sequence == arrived.Sequence)
                    {
                        _partial = null;
                    }
                }

                if (!published)
                {
                    ReturnPayload(message.Buffer);
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

    private ValueTask<SocketMessage?> NextAsync(ChannelReader<SocketMessage> inbox)
    {
        if (_waitingDeadline is not { } deadline)
        {
            return ValueTask.FromResult<SocketMessage?>(null);
        }

        lock (_progress)
        {
            if (inbox.TryRead(out var ready))
            {
                Interlocked.Add(ref _pendingBytes, -ready.Length);
                if (Volatile.Read(ref _terminal) == 0)
                {
                    return ValueTask.FromResult<SocketMessage?>(ready);
                }

                ReturnPayload(ready.Buffer);
                return ValueTask.FromResult<SocketMessage?>(null);
            }

            deadline.Arm(IdleBudget(), _partial is { } partial ? (partial.Started, partial.Bytes) : null);
            _waiting = deadline;
        }

        return WaitNextAsync(inbox, deadline);
    }

    private async ValueTask<SocketMessage?> WaitNextAsync(ChannelReader<SocketMessage> inbox, ReceiveDeadline deadline)
    {
        try
        {
            if (!await inbox.WaitToReadAsync(deadline.Token) || !inbox.TryRead(out var message))
            {
                return null;
            }

            Interlocked.Add(ref _pendingBytes, -message.Length);
            if (deadline.Complete() && Volatile.Read(ref _terminal) == 0)
            {
                return message;
            }

            ReturnPayload(message.Buffer);
            return null;
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

    private async ValueTask<(byte[] Buffer, int Length)?> ReadMessageAsync(byte[] buffer, int limit, string limitReason, CancellationToken cancellationToken)
    {
        try
        {
            var first = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (first.MessageType == WebSocketMessageType.Close)
            {
                MarkTerminal();
                await CloseAsync(WebSocketCloseStatus.NormalClosure, "closed");
                return null;
            }

            if (first.MessageType == WebSocketMessageType.Binary)
            {
                MarkTerminal();
                await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "frames are JSON text");
                return null;
            }

            if (first.Count > limit)
            {
                MarkTerminal();
                await CloseAsync(WebSocketCloseStatus.MessageTooBig, limitReason);
                return null;
            }

            if (first.EndOfMessage)
            {
                if (first.Count == 0)
                {
                    return ([], 0);
                }

                var rented = ArrayPool<byte>.Shared.Rent(first.Count);
                buffer.AsSpan(0, first.Count).CopyTo(rented);
                return (rented, first.Count);
            }

            var writer = _multiFrame ??= new ArrayBufferWriter<byte>(Math.Max(first.Count * 2, 4096));
            writer.ResetWrittenCount();
            writer.Write(buffer.AsSpan(0, first.Count));
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

                if (writer.WrittenCount + read.Count > limit)
                {
                    MarkTerminal();
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, limitReason);
                    return null;
                }

                writer.Write(buffer.AsSpan(0, read.Count));
                if (read.EndOfMessage)
                {
                    var total = writer.WrittenCount;
                    if (total == 0)
                    {
                        return ([], 0);
                    }

                    var rented = ArrayPool<byte>.Shared.Rent(total);
                    writer.WrittenSpan.CopyTo(rented);
                    if (writer.Capacity > MaxRetainedBufferBytes)
                    {
                        _multiFrame = null;
                    }
                    else
                    {
                        writer.ResetWrittenCount();
                    }

                    return (rented, total);
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
            MarkTerminal();
            return null;
        }
    }

    private static void ReturnPayload(byte[] buffer)
    {
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
