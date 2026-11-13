using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// A SQL WebSocket session (<c>terrascale.sql.v1</c>). Outside a transaction every call runs as its own transaction;
/// <see cref="BeginAsync"/> opens one that lasts at most 5 seconds. Calls may overlap; the server answers them in order.
/// Nothing is resent. A lost committing answer closes the session with <c>write_outcome_unknown</c> (08007),
/// retained for every later call. Without a committing request, a lost open transaction throws
/// <see cref="TerraBaseSqlErrorCodes.TransactionRolledBack"/> until <see cref="RollbackAsync"/>. Dispose closes the socket,
/// rolls back an open transaction and ends the session.
/// </summary>
public sealed class TerraBaseSqlSession : IAsyncDisposable
{
    /// <summary>The WebSocket subprotocol.</summary>
    public const string Subprotocol = "terrascale.sql.v1";

    private const int MaxFrameBytes = 1024 * 1024;

    private static readonly TerraBaseSqlSocketAnswer Cleared = new(null, "result", false, []);

    private readonly TerraBaseConnection connection;
    private readonly string path;
    private readonly int maxAnswerBytes;
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly SemaphoreSlim connecting = new(1, 1);
    private readonly CancellationTokenSource closing = new();
    private readonly Lock stateGate = new();
    private volatile TerraBaseSqlException? uncertain;
    private Link link;
    private long nextId;
    private volatile bool transaction;
    private volatile bool lostTransaction;
    private volatile bool disposed;

    private TerraBaseSqlSession(TerraBaseConnection connection, string path, WebSocket socket)
    {
        this.connection = connection;
        this.path = path;
        maxAnswerBytes = connection.MaxResponseBytes;
        link = Start(socket);
    }

    /// <summary>Whether a transaction is open, as of the last answer.</summary>
    public bool InTransaction => transaction;

    internal static async Task<TerraBaseSqlSession> ConnectAsync(TerraBaseConnection connection, string path, CancellationToken cancellationToken) =>
        new(connection, path, await connection.ConnectWebSocketAsync(path, Subprotocol, cancellationToken).ConfigureAwait(false));

    /// <summary>Runs one statement with positional arguments.</summary>
    public Task<TerraBaseSqlStatementResult> ExecuteAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new TerraBaseSqlStatement(sql, args), cancellationToken);

    /// <summary>Runs one statement with named arguments.</summary>
    public Task<TerraBaseSqlStatementResult> ExecuteAsync(string sql, IReadOnlyDictionary<string, TerraBaseSqlValue> named, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new TerraBaseSqlStatement(sql, Named: named), cancellationToken);

    /// <summary>Runs one statement.</summary>
    public async Task<TerraBaseSqlStatementResult> ExecuteAsync(TerraBaseSqlStatement statement, CancellationToken cancellationToken = default) =>
        (await BatchAsync([statement], cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Runs one statement and returns its rows.</summary>
    public async Task<IReadOnlyList<TerraBaseSqlRecord>> QueryAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args = null, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, args, cancellationToken).ConfigureAwait(false)).Rows;

    /// <summary>Runs one statement and maps its rows to <typeparamref name="T"/>; see <see cref="TerraBaseSqlRecord.As{T}"/>.</summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyList<TerraBaseSqlValue>? args, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, args, cancellationToken).ConfigureAwait(false)).As(typeInfo);

    /// <summary>
    /// Runs 1 to 256 statements: as one transaction, all or nothing, outside a transaction, or inside the open one.
    /// After a failed statement inside a transaction every call throws <see cref="TerraBaseSqlErrorCodes.TransactionAborted"/>
    /// until <see cref="RollbackAsync"/> or <see cref="CommitAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<TerraBaseSqlStatementResult>> BatchAsync(IReadOnlyList<TerraBaseSqlStatement> statements, CancellationToken cancellationToken = default)
    {
        TerraBaseSqlStatement.Validate(statements);
        var answer = await SendAsync("execute", statements, cancellationToken).ConfigureAwait(false);
        return answer.Results!.Count == statements.Count
            ? [.. answer.Results.Select(result => result.ToResult())]
            : throw new TerraBaseException(null, "TerraBase returned a result count that does not match the statements.", "invalid_response", null);
    }

    /// <summary>Opens a transaction of at most 5 seconds.</summary>
    public Task BeginAsync(CancellationToken cancellationToken = default) => SendAsync("begin", null, cancellationToken);

    /// <summary>Commits the open transaction; after a failed statement it rolls back and throws <see cref="TerraBaseSqlErrorCodes.TransactionRolledBack"/>.</summary>
    public Task CommitAsync(CancellationToken cancellationToken = default) => SendAsync("commit", null, cancellationToken);

    /// <summary>Rolls back the open transaction.</summary>
    public Task RollbackAsync(CancellationToken cancellationToken = default) => SendAsync("rollback", null, cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction: begins, runs, and commits; rolls back and rethrows when
    /// <paramref name="work"/> throws.
    /// </summary>
    public async Task<T> TransactionAsync<T>(Func<TerraBaseSqlSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await BeginAsync(cancellationToken).ConfigureAwait(false);
        T result;
        try
        {
            result = await work(this, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (transaction || lostTransaction)
            {
                try
                {
                    await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is TerraBaseException or WebSocketException or TimeoutException)
                {
                    // The work's own exception explains the failure; the server rolls back a transaction it loses.
                }
            }

            throw;
        }

        await CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc cref="TransactionAsync{T}(Func{TerraBaseSqlSession, CancellationToken, Task{T}}, CancellationToken)"/>
    public Task TransactionAsync(Func<TerraBaseSqlSession, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return TransactionAsync<bool>(async (session, token) =>
        {
            await work(session, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>Closes the socket normally; an open transaction rolls back on the server. The session stays closed.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await connecting.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        var current = link;
        if (current.Socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await current.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
                await current.Receiving.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException)
            {
            }
        }

        await closing.CancelAsync().ConfigureAwait(false);
        current.Socket.Abort();
        await current.Receiving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        current.Dispose();
        closing.Dispose();
        sending.Dispose();
        connecting.Dispose();
    }

    internal void Abort()
    {
        if (disposed) return;
        disposed = true;
        closing.Cancel();
        Volatile.Read(ref link).Socket.Abort();
    }

    private Link Start(WebSocket socket)
    {
        var started = new Link(socket);
        started.Receiving = Task.Run(() => ReceiveAsync(started));
        return started;
    }

    private async Task<Link> CurrentAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref link);
        if (Volatile.Read(ref current.Failure) is null) return current;
        await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (uncertain is { } failure) throw failure;
            ObjectDisposedException.ThrowIf(disposed, this);
            current = link;
            if (Volatile.Read(ref current.Failure) is null) return current;
            var socket = await connection.ConnectWebSocketAsync(path, Subprotocol, cancellationToken).ConfigureAwait(false);
            await current.Receiving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            current.Dispose();
            var next = Start(socket);
            Volatile.Write(ref link, next);
            return next;
        }
        finally
        {
            connecting.Release();
        }
    }

    private async Task<TerraBaseSqlSocketAnswer> SendAsync(string type, IReadOnlyList<TerraBaseSqlStatement>? statements, CancellationToken cancellationToken)
    {
        lock (stateGate)
        {
            if (uncertain is { } failure) throw failure;
            ObjectDisposedException.ThrowIf(disposed, this);
            if (lostTransaction)
            {
                if (type != "rollback")
                {
                    throw new TerraBaseSqlException(null, "The SQL session lost its connection during a transaction, which rolled back; call RollbackAsync.",
                        TerraBaseSqlErrorCodes.TransactionRolledBack, 0, "40000");
                }

                lostTransaction = false;
                return Cleared;
            }
        }

        var id = Interlocked.Increment(ref nextId);
        var frame = TerraBaseRequestJson.Serialize(new TerraBaseSqlSocketRequest(id, type, statements), TerraBaseSqlJsonContext.Wire.TerraBaseSqlSocketRequest);
        if (frame.Length > MaxFrameBytes) throw new ArgumentException("A SQL frame is at most 1 MiB.", nameof(statements));
        var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        var pending = new Pending(type);
        current.Pending[id] = pending;
        try
        {
            await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (stateGate)
                {
                    if (uncertain is { } failure) throw failure;
                    if (Volatile.Read(ref current.Failure) is { } dropped) throw Closed(dropped);
                    pending.Committing = type == "commit" || type == "execute" && (!transaction || current.Pending.Values.Any(other => other.Sent && other.Control));
                    pending.Sent = true;
                }
                await current.Socket.SendAsync(frame, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sending.Release();
            }

            if (Volatile.Read(ref current.Failure) is { } lost) pending.Answer.TrySetException(uncertain ?? Closed(lost));
            var received = await pending.Answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return received.Error is { } error
                ? throw new TerraBaseSqlException(null, error.Message, error.Code, error.Statement, error.Sqlstate)
                : received;
        }
        catch (Exception error) when (pending.Sent && !pending.Answer.Task.IsCompletedSuccessfully)
        {
            Drop(current, error);
            current.Socket.Abort();
            throw uncertain ?? Closed(error);
        }
        finally
        {
            current.Pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveAsync(Link current)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var socket = current.Socket;
        try
        {
            while (true)
            {
                buffer.ResetWrittenCount();
                ValueWebSocketReceiveResult result;
                do
                {
                    if (buffer.WrittenCount >= maxAnswerBytes) throw new TerraBaseException(null, "TerraBase sent a SQL frame larger than the client's limit.", "invalid_response", null);
                    result = await socket.ReceiveAsync(buffer.GetMemory(64 * 1024), closing.Token).ConfigureAwait(false);
                    buffer.Advance(result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely,
                        $"TerraBase closed the SQL session ({(int?)socket.CloseStatus}: {socket.CloseStatusDescription}).");
                }

                TerraBaseSqlSocketAnswer answer;
                try
                {
                    answer = JsonSerializer.Deserialize(buffer.WrittenSpan, TerraBaseSqlJsonContext.Wire.TerraBaseSqlSocketAnswer)
                        ?? throw new JsonException("A SQL frame cannot be null.");
                }
                catch (JsonException)
                {
                    throw new TerraBaseException(null, "TerraBase sent an invalid SQL frame.", "invalid_response", null);
                }

                lock (stateGate)
                {
                    transaction = answer.Transaction;
                    if (answer.Error is { Code: "write_outcome_unknown" or "commit_uncertain" } unknown)
                        uncertain = new TerraBaseSqlException(null, unknown.Message, unknown.Code, unknown.Statement, unknown.Sqlstate);
                    if (answer.Id is { } id && current.Pending.TryRemove(id, out var pending)) pending.Answer.TrySetResult(answer);
                    else if (answer.Error is { } error) throw new TerraBaseSqlException(null, error.Message, error.Code, error.Statement, error.Sqlstate);
                    if (uncertain is { } failure) throw failure;
                }
            }
        }
        catch (Exception error)
        {
            Drop(current, error);
            socket.Abort();
        }
    }

    private void Drop(Link current, Exception error)
    {
        lock (stateGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref link), current)) return;
            if (current.Pending.Values.Any(pending => pending.Sent && pending.Committing))
                uncertain ??= new TerraBaseSqlException(null, "The SQL session lost a committing answer; reconcile before repeating.", "write_outcome_unknown", 0, "08007", error);
            if (transaction && uncertain is null) lostTransaction = true;
            transaction = false;
            Volatile.Write(ref current.Failure, error);
            foreach (var pending in current.Pending.Values) pending.Answer.TrySetException(uncertain ?? Closed(error));
        }
    }

    private sealed class Pending(string type)
    {
        public readonly TaskCompletionSource<TerraBaseSqlSocketAnswer> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly bool Control = type is "begin" or "commit" or "rollback";
        public bool Sent;
        public bool Committing;
    }

    private static Exception Closed(Exception cause) => cause switch
    {
        TerraBaseException known => known,
        OperationCanceledException => new ObjectDisposedException(nameof(TerraBaseSqlSession)),
        _ => new WebSocketException(WebSocketError.ConnectionClosedPrematurely, cause.Message, cause),
    };

    private sealed class Link(WebSocket socket) : IDisposable
    {
        public readonly WebSocket Socket = socket;
        public readonly ConcurrentDictionary<long, Pending> Pending = new();
        public Task Receiving = Task.CompletedTask;
        public Exception? Failure;

        public void Dispose() => Socket.Dispose();
    }
}
