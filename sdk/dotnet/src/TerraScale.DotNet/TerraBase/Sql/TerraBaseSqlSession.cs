using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

public sealed class TerraBaseSqlSession : IAsyncDisposable
{
    public const string Subprotocol = "terrascale.sql.v2";

    private const int MaxFrameBytes = 1024 * 1024;

    private static readonly TimeSpan FrameDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(20);

    private static readonly TerraBaseSqlSocketAnswer Cleared = new(null, "result", false, []);
    private static readonly TerraBaseSqlRequestOptions NoOptions = new();

    private readonly TerraBaseConnection connection;
    private readonly string path;
    private readonly TerraBaseBookmarkTracker bookmarks;
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

    private TerraBaseSqlSession(TerraBaseConnection connection, string path, TerraBaseBookmarkTracker bookmarks, TerraBaseConsistencyMode? consistency, WebSocket socket)
    {
        this.connection = connection;
        this.path = path;
        this.bookmarks = bookmarks;
        Consistency = consistency;
        maxAnswerBytes = connection.MaxResponseBytes;
        link = Start(socket);
    }

    public bool InTransaction => transaction;

    public TerraBaseConsistencyMode? Consistency { get; }

    public string Bookmark => bookmarks.Current;

    public void ObserveBookmark(string bookmark) => bookmarks.Observe(bookmark, nameof(bookmark));

    internal static async Task<TerraBaseSqlSession> ConnectAsync(TerraBaseConnection connection, string path, TerraBaseBookmarkTracker bookmarks,
        TerraBaseConsistencyMode? consistency, CancellationToken cancellationToken) =>
        new(connection, path, bookmarks, consistency,
            await connection.ConnectWebSocketAsync(path, Subprotocol, cancellationToken, UpgradeHeaders(bookmarks, consistency), KeepAliveTimeout).ConfigureAwait(false));

    public Task<TerraBaseSqlStatementResult> ExecuteAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new TerraBaseSqlStatement(sql, args), cancellationToken);

    public Task<TerraBaseSqlStatementResult> ExecuteAsync(string sql, IReadOnlyDictionary<string, TerraBaseSqlValue> named, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new TerraBaseSqlStatement(sql, Named: named), cancellationToken);

    public async Task<TerraBaseSqlStatementResult> ExecuteAsync(TerraBaseSqlStatement statement, CancellationToken cancellationToken = default) =>
        (await BatchAsync([statement], cancellationToken).ConfigureAwait(false))[0];

    public async Task<TerraBaseSqlStatementResult> ExecuteAsync(TerraBaseSqlStatement statement, TerraBaseSqlRequestOptions options, CancellationToken cancellationToken = default) =>
        (await BatchAsync([statement], options, cancellationToken).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<TerraBaseSqlRecord>> QueryAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args = null, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, args, cancellationToken).ConfigureAwait(false)).Rows;

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyList<TerraBaseSqlValue>? args, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, args, cancellationToken).ConfigureAwait(false)).As(typeInfo);

    public Task<IReadOnlyList<TerraBaseSqlStatementResult>> BatchAsync(IReadOnlyList<TerraBaseSqlStatement> statements, CancellationToken cancellationToken = default) =>
        BatchAsync(statements, NoOptions, cancellationToken);

    public async Task<IReadOnlyList<TerraBaseSqlStatementResult>> BatchAsync(IReadOnlyList<TerraBaseSqlStatement> statements, TerraBaseSqlRequestOptions options,
        CancellationToken cancellationToken = default)
    {
        TerraBaseSqlStatement.Validate(statements);
        ArgumentNullException.ThrowIfNull(options);
        var answer = await SendAsync("execute", statements, options.Consistency, options.Bookmark, cancellationToken).ConfigureAwait(false);
        return answer.Results is { } results && results.Count == statements.Count
            ? [.. results.Select(result => result.ToResult())]
            : throw new TerraBaseException(null, "TerraBase returned a result count that does not match the statements.", "invalid_response", null);
    }

    public Task BeginAsync(CancellationToken cancellationToken = default) => SendAsync("begin", null, null, null, cancellationToken);

    public Task BeginAsync(TerraBaseConsistencyMode consistency, CancellationToken cancellationToken = default) =>
        SendAsync("begin", null, consistency, null, cancellationToken);

    public Task BeginStrongAsync(CancellationToken cancellationToken = default) => BeginAsync(TerraBaseConsistencyMode.Strong, cancellationToken);

    public Task CommitAsync(CancellationToken cancellationToken = default) => SendAsync("commit", null, null, null, cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) => SendAsync("rollback", null, null, null, cancellationToken);

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
                catch (Exception error) when (error is TerraBaseException or WebSocketException or TimeoutException or ObjectDisposedException)
                {
                }
            }

            throw;
        }

        await CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task TransactionAsync(Func<TerraBaseSqlSession, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return TransactionAsync<bool>(async (session, token) =>
        {
            await work(session, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await connecting.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = link;
            if (current.Socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), connection.TimeProvider);
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
        }
        finally
        {
            connecting.Release();
        }
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
        var started = new Link(socket, connection.TimeProvider, Expire);
        started.Receiving = Task.Run(() => ReceiveAsync(started));
        return started;
    }

    private void Expire(Link current)
    {
        lock (stateGate)
        {
            if (!current.Expired()) return;
            Drop(current, new TimeoutException($"TerraBase did not answer a SQL frame within {FrameDeadline.TotalSeconds:0} seconds."));
        }

        current.Socket.Abort();
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
            var socket = await connection.ConnectWebSocketAsync(path, Subprotocol, cancellationToken, UpgradeHeaders(bookmarks, Consistency), KeepAliveTimeout).ConfigureAwait(false);
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

    private async Task<TerraBaseSqlSocketAnswer> SendAsync(string type, IReadOnlyList<TerraBaseSqlStatement>? statements, TerraBaseConsistencyMode? consistency,
        string? bookmark, CancellationToken cancellationToken)
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

        var outgoing = bookmarks.Outgoing(bookmark);
        var id = Interlocked.Increment(ref nextId);
        var frame = TerraBaseRequestJson.Serialize(
            new TerraBaseSqlSocketRequest(id, type, statements, TerraBaseConsistency.Format(consistency), outgoing.Length > 0 ? outgoing : null),
            TerraBaseSqlJsonContext.Wire.TerraBaseSqlSocketRequest);
        if (frame.Length > MaxFrameBytes) throw new ArgumentException("A SQL frame is at most 1 MiB.", nameof(statements));
        var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        var kind = type == "execute" && statements is [{ Sql: var sql }] ? TerraBaseSqlScript.TransactionControl(sql) ?? type : type;
        var pending = new Pending(kind);
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
                    pending.Committing = kind == "commit" || kind == "execute" && (!transaction || current.Pending.Values.Any(other => other.Sent && other.Control));
                    if (!current.Pending.Values.Any(other => other.Sent)) current.Arm(FrameDeadline);
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
            return received.Error is { } error ? throw error.ToException() : received;
        }
        catch (Exception error) when (pending.Sent && !pending.Answer.Task.IsCompletedSuccessfully)
        {
            var canceled = error is OperationCanceledException && cancellationToken.IsCancellationRequested;
            Drop(current, canceled ? new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "A canceled SQL request closed the session's connection.", error) : error);
            current.Socket.Abort();
            if (canceled && !pending.Committing) throw;
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

                bookmarks.ObserveAnswer(answer.Bookmark, null);
                lock (stateGate)
                {
                    transaction = answer.Transaction;
                    if (answer.Error is { } refusal && TerraBaseSqlErrorCodes.OutcomeUncertain(refusal.Code))
                        uncertain = refusal.ToException();
                    if (answer.Id is { } id && current.Pending.TryRemove(id, out var pending)) pending.Answer.TrySetResult(answer);
                    else if (answer.Error is { } error) throw error.ToException();
                    if (uncertain is { } failure) throw failure;
                    current.Arm(current.Pending.Values.Any(other => other.Sent) ? FrameDeadline : Timeout.InfiniteTimeSpan);
                }
            }
        }
        catch (Exception error)
        {
            Drop(current, closing.IsCancellationRequested || error is TerraBaseException or WebSocketException
                ? error
                : new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The SQL session's connection closed.", error));
            socket.Abort();
        }
    }

    private void Drop(Link current, Exception error)
    {
        lock (stateGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref link), current)) return;
            current.Arm(Timeout.InfiniteTimeSpan);
            if (current.Pending.Values.Any(pending => pending.Sent && pending.Committing))
                uncertain ??= new TerraBaseSqlException(null, "The SQL session lost a committing answer; reconcile before repeating.", TerraBaseSqlErrorCodes.WriteOutcomeUnknown, 0, "08007", error);
            if (transaction && uncertain is null) lostTransaction = true;
            transaction = false;
            Volatile.Write(ref current.Failure, error);
            foreach (var pending in current.Pending.Values) pending.Answer.TrySetException(uncertain ?? Closed(error));
        }
    }

    private static FrozenDictionary<string, string> UpgradeHeaders(TerraBaseBookmarkTracker bookmarks, TerraBaseConsistencyMode? consistency)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        if (bookmarks.Current is { Length: > 0 } bookmark) headers[TerraBaseConsistency.BookmarkHeader] = bookmark;
        if (TerraBaseConsistency.Format(consistency) is { } mode) headers[TerraBaseConsistency.ConsistencyHeader] = mode;
        return headers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Pending(string type)
    {
        public readonly TaskCompletionSource<TerraBaseSqlSocketAnswer> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly bool Control = type is "begin" or "commit" or "rollback";
        public bool Sent;
        public bool Committing;
    }

    private Exception Closed(Exception cause) => cause switch
    {
        TerraBaseException or TimeoutException => cause,
        OperationCanceledException or ObjectDisposedException when disposed => new ObjectDisposedException(nameof(TerraBaseSqlSession)),
        _ => new WebSocketException(WebSocketError.ConnectionClosedPrematurely, cause.Message, cause),
    };

    private sealed class Link : IDisposable
    {
        public readonly WebSocket Socket;
        public readonly ConcurrentDictionary<long, Pending> Pending = new();
        public Task Receiving = Task.CompletedTask;
        public Exception? Failure;
        private readonly TimeProvider clock;
        private readonly ITimer watchdog;
        private long due = long.MaxValue;

        public Link(WebSocket socket, TimeProvider time, Action<Link> expire)
        {
            Socket = socket;
            clock = time;
            watchdog = time.CreateTimer(_ => expire(this), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public void Arm(TimeSpan deadline)
        {
            due = deadline == Timeout.InfiniteTimeSpan
                ? long.MaxValue
                : clock.GetTimestamp() + (long)(deadline.TotalSeconds * clock.TimestampFrequency);
            watchdog.Change(deadline, Timeout.InfiniteTimeSpan);
        }

        public bool Expired()
        {
            if (due == long.MaxValue) return false;
            var remaining = clock.GetElapsedTime(clock.GetTimestamp(), due);
            if (remaining <= TimeSpan.Zero) return true;
            watchdog.Change(remaining + TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
            return false;
        }

        public void Dispose()
        {
            watchdog.Dispose();
            Socket.Dispose();
        }
    }
}
