using System.Net.WebSockets;
using System.Text.Json;

namespace TerraBase.Testing;

internal sealed class SqlSocketClient : IAsyncDisposable
{
    public const string Protocol = "terrascale.sql.v1";

    private readonly ClientWebSocket _socket;
    private long _next;

    private SqlSocketClient(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    public static Uri Address(ManagementApi api, string databasePath) =>
        new UriBuilder(new Uri(api.Server.ClientAddress!, databasePath + "/sql/ws")) { Scheme = "ws" }.Uri;

    public static async Task<SqlSocketClient> ConnectAsync(ManagementApi api, string databasePath, string? apiKey)
    {
        var socket = new ClientWebSocket();
        if (apiKey is not null)
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        }

        socket.Options.AddSubProtocol(Protocol);
        try
        {
            await socket.ConnectAsync(Address(api, databasePath), CancellationToken.None);
            return new SqlSocketClient(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task<JsonElement> ExecuteAsync(params object[] statements) => SendAsync(new { id = ++_next, type = "execute", statements });

    public Task<JsonElement> AuthAsync(string token) => SendAsync(new { id = ++_next, type = "auth", token });

    public async Task<JsonElement> SendAsync(object frame)
    {
        await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        return await ReceiveAsync() ?? throw new InvalidOperationException("the server closed the socket: " + _socket.CloseStatus);
    }

    public async Task<JsonElement?> ReceiveAsync()
    {
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await _socket.ReceiveAsync(buffer, CancellationToken.None);
            if (read.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, read.Count);
            if (read.EndOfMessage)
            {
                return JsonElement.Parse(message.ToArray());
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _socket.Dispose();
        }
    }
}
