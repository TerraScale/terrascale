using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace TerraBase.Testing;

/// <summary>An answer of the TerraBase wire protocol: the HTTP API's status and the rest of the frame, read in place.</summary>
public sealed record WireAnswer(int Status, ArraySegment<byte> Payload, byte Operation)
{
    /// <summary>The error code of an error answer.</summary>
    public string Code => Status >= 400 ? Encoding.ASCII.GetString(Payload.Slice(1, Payload[0])) : "";

    /// <summary>The error message, UTF-8 to the end of the frame.</summary>
    public string Message => Status >= 400 ? Encoding.UTF8.GetString(Payload[(1 + Payload[0])..]) : "";

    /// <summary>The ETag of a get or put answer.</summary>
    public string ETag => Operation == 2 ? Encoding.ASCII.GetString(Payload.Slice(2, Payload[1])) : Encoding.ASCII.GetString(Payload.Slice(1, Payload[0]));

    /// <summary>The JSON document of a get answer.</summary>
    public string Json => Encoding.UTF8.GetString(Payload[(1 + Payload[0])..]);

    /// <summary>Whether a put answered remote_durable rather than local_durable.</summary>
    public bool RemoteDurable => Payload[0] == 1;
}

/// <summary>
/// A TerraBase wire protocol v1 client (docs/terrabase/wire-protocol.md): one connection, any number of requests in flight,
/// answers matched by request id; frames sent together go out in one socket write.
/// </summary>
public sealed class WireClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly ConcurrentDictionary<uint, (TaskCompletionSource<WireAnswer> Answer, byte Operation)> _pending = new();
    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writing;
    private int _next;

    private WireClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        Closed = Task.Run(ReadAsync);
        _writing = Task.Run(WriteAsync);
    }

    /// <summary>Completes when the server closes the connection.</summary>
    public Task Closed { get; }

    /// <summary>Connects without a hello.</summary>
    public static async Task<WireClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        return new WireClient(tcp);
    }

    /// <summary>Connects and sends the hello; throws when it is refused.</summary>
    public static async Task<WireClient> OpenAsync(int port, string key, string project, string database)
    {
        var client = await ConnectAsync(port);
        var hello = await client.HelloAsync(key, project, database);
        if (hello.Status != 200)
        {
            await client.DisposeAsync();
            throw new InvalidOperationException($"the wire hello was refused: {hello.Status} {hello.Code}");
        }

        return client;
    }

    public Task<WireAnswer> HelloAsync(string key, string project, string database) =>
        SendAsync(0, [1, .. Text(key), .. Text(project), .. Text(database)]);

    public Task<WireAnswer> HelloPinnedAsync(string key, string project, string database, string region) =>
        SendAsync(4, [1, .. Text(key), .. Text(project), .. Text(database), checked((byte)Encoding.UTF8.GetByteCount(region)), .. Encoding.UTF8.GetBytes(region)]);

    public Task<WireAnswer> GetAsync(string collection, string id) => SendAsync(1, Address(collection, id));

    /// <summary>A put, answered locally durable or, with <paramref name="remoteDurable"/>, after another region's acknowledgement.</summary>
    public Task<WireAnswer> PutAsync(string collection, string id, string json, bool remoteDurable = false) =>
        SendAsync(2, [.. Address(collection, id), remoteDurable ? (byte)1 : (byte)0, .. Encoding.UTF8.GetBytes(json)]);

    /// <summary>Sends any frame; an answer that never comes fails when the connection closes.</summary>
    public Task<WireAnswer> SendAsync(byte operation, byte[] body)
    {
        var id = (uint)Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<WireAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (answer, operation);
        var frame = new byte[9 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(frame.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), id);
        frame[8] = operation;
        body.CopyTo(frame.AsSpan(9));
        if (!_frames.Writer.TryWrite(frame) || Closed.IsCompleted)
        {
            _pending.TryRemove(id, out _);
            answer.TrySetException(new IOException("the wire connection is closed"));
        }

        return answer.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();
        _tcp.Dispose();
        await Task.WhenAll(Closed, _writing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static byte[] Address(string collection, string id) => [(byte)Encoding.UTF8.GetByteCount(collection), .. Encoding.UTF8.GetBytes(collection), .. Encoding.ASCII.GetBytes(id)];

    private static byte[] Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [(byte)bytes.Length, (byte)(bytes.Length >> 8), .. bytes];
    }

    private async Task WriteAsync()
    {
        var batch = new MemoryStream();
        try
        {
            while (await _frames.Reader.WaitToReadAsync())
            {
                batch.SetLength(0);
                while (batch.Length < 256 * 1024 && _frames.Reader.TryRead(out var frame))
                {
                    batch.Write(frame);
                }

                await _stream.WriteAsync(batch.GetBuffer().AsMemory(0, (int)batch.Length));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The reader fails what is pending.
        }
    }

    private async Task ReadAsync()
    {
        var input = new BufferedStream(_stream, 64 * 1024);
        var header = new byte[4];
        try
        {
            while (await input.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false) == 4)
            {
                var frame = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
                await input.ReadExactlyAsync(frame);
                var id = BinaryPrimitives.ReadUInt32LittleEndian(frame);
                if (_pending.TryRemove(id, out var pending))
                {
                    pending.Answer.TrySetResult(new WireAnswer(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)), new ArraySegment<byte>(frame, 6, frame.Length - 6), pending.Operation));
                }
                else if (id == 0)
                {
                    // A connection-level error (a bad frame): every pending request fails with it below.
                    return;
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or EndOfStreamException)
        {
        }
        finally
        {
            foreach (var id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out var pending))
                {
                    pending.Answer.TrySetException(new IOException("the wire connection closed before the answer"));
                }
            }
        }
    }
}
