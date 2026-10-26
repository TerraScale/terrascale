using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace TerraBase.Testing;

// The simple-query protocol as psql speaks it: one Query message, every response until ReadyForQuery.
internal sealed class PostgresWireClient(TcpClient tcp, NetworkStream stream) : IAsyncDisposable
{
    public static async Task<PostgresWireClient> ConnectAsync(int port, string key, string database)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var client = new PostgresWireClient(tcp, tcp.GetStream());
        await client.SendAsync(null, [0, 3, 0, 0, .. CString("user"), .. CString("wire"), .. CString("database"), .. CString(database), 0]);
        await client.ReadAsync(); // AuthenticationCleartextPassword
        await client.SendAsync('p', CString(key));
        while ((await client.ReadAsync()).Type != 'Z')
        {
        }

        return client;
    }

    public async Task<List<(char Type, string?[] Fields)>> QueryAsync(string sql)
    {
        await SendAsync('Q', CString(sql));
        return await ReadyAsync();
    }

    // Extended-protocol messages, then Sync: errors skip to it, so each refusal is its own call.
    public async Task<List<(char Type, string?[] Fields)>> SyncAsync(params (char Type, byte[] Body)[] messages)
    {
        foreach (var (type, body) in messages)
        {
            await SendAsync(type, body);
        }

        await SendAsync('S', []);
        return await ReadyAsync();
    }

    public static (char, byte[]) Parse(string name, string sql) => ('P', [.. CString(name), .. CString(sql), 0, 0]);

    public static (char, byte[]) Bind(string portal, string statement) => ('B', [.. CString(portal), .. CString(statement), 0, 0, 0, 0, 0, 0]);

    public static (char, byte[]) Describe(char kind, string name) => ('D', [(byte)kind, .. CString(name)]);

    public static (char, byte[]) Execute(string portal) => ('E', [.. CString(portal), 0, 0, 0, 0]);

    // Parse, bind and describe a portal, then either close it or execute with the requested row limit.
    public async Task<List<char>> PortalAsync(string sql, int? maxRows)
    {
        await SendAsync('P', [0, .. CString(sql), 0, 0]);
        await SendAsync('B', [0, 0, 0, 0, 0, 0, 0, 0]);
        await SendAsync('D', [(byte)'P', 0]);
        if (maxRows is { } limit)
        {
            var count = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(count, limit);
            await SendAsync('E', [0, .. count]);
        }
        else
        {
            await SendAsync('C', [(byte)'P', 0]);
        }

        await SendAsync('S', []);
        var messages = new List<char>();
        do
        {
            messages.Add((await ReadAsync()).Type);
        }
        while (messages[^1] != 'Z');

        return messages;
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        tcp.Dispose();
    }

    private async Task<List<(char Type, string?[] Fields)>> ReadyAsync()
    {
        var messages = new List<(char Type, string?[] Fields)>();
        for (var message = await ReadAsync(); ; message = await ReadAsync())
        {
            var body = message.Body;
            messages.Add((message.Type, message.Type switch
            {
                'D' => Values(body),
                'E' => [Encoding.UTF8.GetString(body).Split('\0').First(field => field.StartsWith('C'))[1..]],
                'C' => [Encoding.UTF8.GetString(body).TrimEnd('\0')],
                'Z' => [((char)body[0]).ToString()],
                _ => [null],
            }));
            if (message.Type == 'Z')
            {
                return [.. messages.Where(entry => entry.Type is 'D' or 'E' or 'C' or 'Z')];
            }
        }
    }

    private static string?[] Values(byte[] body)
    {
        var values = new string?[BinaryPrimitives.ReadInt16BigEndian(body)];
        for (int i = 0, at = 2; i < values.Length; i++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(at));
            values[i] = length < 0 ? null : Encoding.UTF8.GetString(body, at + 4, length);
            at += 4 + Math.Max(0, length);
        }

        return values;
    }

    private static byte[] CString(string value) => [.. Encoding.UTF8.GetBytes(value), 0];

    private async Task SendAsync(char? type, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length + 4);
        await stream.WriteAsync((byte[])[.. type is { } t ? new[] { (byte)t } : [], .. length, .. body]);
    }

    private async Task<(char Type, byte[] Body)> ReadAsync()
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header);
        var body = new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4];
        await stream.ReadExactlyAsync(body);
        return ((char)header[0], body);
    }
}
