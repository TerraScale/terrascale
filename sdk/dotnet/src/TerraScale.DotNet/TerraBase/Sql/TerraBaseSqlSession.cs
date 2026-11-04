using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// A SQL WebSocket session (<c>terrascale.sql.v1</c>). Outside a transaction every call runs as its own transaction;
/// <see cref="BeginAsync"/> opens one that lasts at most 5 seconds. Calls may overlap; the server answers them in order.
/// Nothing is resent. Dispose closes the socket and rolls back an open transaction.
/// </summary>
public sealed class TerraBaseSqlSession : IAsyncDisposable
{
    /// <summary>The WebSocket subprotocol.</summary>
    public const string Subprotocol = "terrascale.sql.v1";

    private const int MaxFrameBytes = 1024 * 1024;

    private readonly WebSocket socket;
    private readonly int maxAnswerBytes;
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<TerraBaseSqlSocketAnswer>> pending = new();
    private readonly CancellationTokenSource closing = new();
    private readonly Task receiving;
    private long nextId;
    private volatile bool transaction;
    private Exception? failure;

    private TerraBaseSqlSession(WebSocket socket, int maxAnswerBytes)
    {
        this.socket = socket;
        this.maxAnswerBytes = maxAnswerBytes;
        receiving = Task.Run(ReceiveAsync);
    }

    /// <summary>Whether a transaction is open, as of the last answer.</summary>
    public bool InTransaction => transaction;

    internal static async Task<TerraBaseSqlSession> ConnectAsync(TerraBaseConnection connection, string path, CancellationToken cancellationToken) =>
        new(await connection.ConnectWebSocketAsync(path, Subprotocol, cancellationToken).ConfigureAwait(false), connection.MaxResponseBytes);

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
            if (transaction && socket.State == WebSocketState.Open) await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>Closes the socket normally; an open transaction rolls back on the server.</summary>
    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
                await receiving.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException)
            {
            }
        }

        await closing.CancelAsync().ConfigureAwait(false);
        socket.Abort();
        await receiving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        socket.Dispose();
        closing.Dispose();
        sending.Dispose();
    }

    private async Task<TerraBaseSqlSocketAnswer> SendAsync(string type, IReadOnlyList<TerraBaseSqlStatement>? statements, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref failure) is { } failed) throw Closed(failed);
        var id = Interlocked.Increment(ref nextId);
        var frame = TerraBaseRequestJson.Serialize(new TerraBaseSqlSocketRequest(id, type, statements), TerraBaseSqlJsonContext.Wire.TerraBaseSqlSocketRequest);
        if (frame.Length > MaxFrameBytes) throw new ArgumentException("A SQL frame is at most 1 MiB.", nameof(statements));
        var answer = new TaskCompletionSource<TerraBaseSqlSocketAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = answer;
        try
        {
            await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(frame, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sending.Release();
            }

            if (Volatile.Read(ref failure) is { } lost) answer.TrySetException(Closed(lost));
            var received = await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return received.Error is { } error
                ? throw new TerraBaseSqlException(null, error.Message, error.Code, error.Statement, error.Sqlstate)
                : received;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new ArrayBufferWriter<byte>();
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

                transaction = answer.Transaction;
                if (answer.Id is { } id && pending.TryGetValue(id, out var waiter)) waiter.TrySetResult(answer);
                else if (answer.Error is { } error) throw new TerraBaseSqlException(null, error.Message, error.Code, error.Statement, error.Sqlstate);
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref failure, error);
            transaction = false;
            foreach (var waiter in pending.Values) waiter.TrySetException(Closed(error));
        }
    }

    private static Exception Closed(Exception cause) => cause switch
    {
        TerraBaseException known => known,
        OperationCanceledException => new ObjectDisposedException(nameof(TerraBaseSqlSession)),
        _ => new WebSocketException(WebSocketError.ConnectionClosedPrematurely, cause.Message, cause),
    };
}
