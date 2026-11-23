using System.Buffers;
using System.Net.WebSockets;
using TerraScale.Contracts;

namespace TerraScale.Platform.Features.Functions;

public readonly union OpenFunctionSqlSessionOutcome(FunctionSqlSession, PlatformUnavailable);

public readonly union SendFunctionSqlFrameOutcome(SqlWsServerFrame, PlatformUnavailable);

public sealed class FunctionSqlSession(ClientWebSocket socket) : IAsyncDisposable
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;
    private long _nextId;

    public static async ValueTask<OpenFunctionSqlSessionOutcome> OpenAsync(TerraBaseClient terraBase, Ulid database, CancellationToken cancellationToken)
    {
        var target = new UriBuilder(new Uri(terraBase.BaseAddress, TerraBaseRoutes.InternalSqlSocket(database)))
        {
            Scheme = terraBase.BaseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        }.Uri;
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(SqlWsFrameTypes.Subprotocol);
        socket.Options.SetRequestHeader(PlatformOptions.PeerCredentialHeader, terraBase.PeerSecret);
        (socket.Options.HttpVersion, socket.Options.HttpVersionPolicy) = TerraBaseProtocol.For(terraBase.BaseAddress);
        try
        {
            await socket.ConnectAsync(target, terraBase.Invoker(), cancellationToken);
            return new FunctionSqlSession(socket);
        }
        catch (WebSocketException error)
        {
            socket.Dispose();
            return new PlatformUnavailable($"TerraBase refused the SQL session: {error.Message}", "sql_session_unavailable");
        }
        catch (HttpRequestException error)
        {
            socket.Dispose();
            return new PlatformUnavailable($"TerraBase is unreachable: {error.Message}", "terrabase_unreachable");
        }
    }

    public async ValueTask<SendFunctionSqlFrameOutcome> SendAsync(
        string type,
        IReadOnlyList<SqlHttpStatement>? statements,
        string bookmark,
        CancellationToken cancellationToken)
    {
        var id = ++_nextId;
        var frame = new SqlWsClientFrame(type, id, statements, null, bookmark.Length > 0 ? bookmark : null);
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, TerraScaleContractsJsonContext.Default.SqlWsClientFrame);
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            while (true)
            {
                var memory = buffer.GetMemory(16 * 1024);
                var received = await socket.ReceiveAsync(memory, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return new PlatformUnavailable("TerraBase closed the SQL session", "sql_session_closed");
                }

                buffer.Advance(received.Count);
                if (buffer.WrittenCount > MaxFrameBytes)
                {
                    return new PlatformUnavailable("TerraBase sent an oversized SQL frame", "terrabase_protocol");
                }

                if (received.EndOfMessage)
                {
                    break;
                }
            }
        }
        catch (WebSocketException error)
        {
            return new PlatformUnavailable($"the SQL session broke: {error.Message}", "sql_session_broken");
        }

        return TerraBaseAnswers.Parse(buffer.WrittenSpan, TerraScaleContractsJsonContext.Default.SqlWsServerFrame) is { } answer && answer.Id == id
            ? answer
            : new PlatformUnavailable("TerraBase answered an unreadable SQL frame", "terrabase_protocol");
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "function finished", deadline.Token);
            }
            catch (WebSocketException)
            {
                socket.Abort();
            }
            catch (OperationCanceledException)
            {
                socket.Abort();
            }
        }

        socket.Dispose();
    }
}
