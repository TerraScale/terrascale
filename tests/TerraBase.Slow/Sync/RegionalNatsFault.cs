using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TerraBase.Slow.Sync;

/// <summary>Real NATS transport with fixture-owned publication loss and partitions.</summary>
internal sealed class RegionalNatsFault : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Uri _upstream;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accept;
    private Func<string, ReadOnlyMemory<byte>, bool>? _drop;
    private TaskCompletionSource _dropped = NewSignal();
    private int _partitioned;

    internal RegionalNatsFault(Uri upstream)
    {
        _upstream = upstream;
        _listener.Start();
        Url = new Uri($"nats://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _accept = AcceptAsync();
    }

    internal Uri Url { get; }

    internal Task Drop(Func<string, ReadOnlyMemory<byte>, bool> predicate)
    {
        _dropped = NewSignal();
        Volatile.Write(ref _drop, predicate);
        return _dropped.Task;
    }

    internal void Allow() => Volatile.Write(ref _drop, null);

    internal void Partition()
    {
        Volatile.Write(ref _partitioned, 1);
        foreach (var client in _clients.Keys)
        {
            client.Dispose();
        }
    }

    internal void Heal() => Volatile.Write(ref _partitioned, 0);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (Volatile.Read(ref _partitioned) != 0)
                {
                    client.Dispose();
                    continue;
                }
                _clients[client] = 0;
                _connections.Add(RelayAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            try
            {
                await upstream.ConnectAsync(_upstream.Host, _upstream.Port, connection.Token);
                var input = client.GetStream();
                var output = upstream.GetStream();
                var sending = SendAsync(input, output, connection.Token);
                var receiving = output.CopyToAsync(input, connection.Token);
                _ = await Task.WhenAny(sending, receiving);
                await connection.CancelAsync();
                await Task.WhenAll(sending, receiving);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                _clients.TryRemove(client, out _);
            }
        }
    }

    private async Task SendAsync(Stream input, Stream output, CancellationToken token)
    {
        var single = new byte[1];
        while (true)
        {
            using var line = new MemoryStream();
            do
            {
                if (await input.ReadAsync(single, token) == 0)
                {
                    return;
                }
                line.WriteByte(single[0]);
                if (line.Length > 65_536)
                {
                    throw new IOException("fixture NATS line exceeds bound");
                }
            }
            while (single[0] != '\n');

            var header = line.ToArray();
            var text = Encoding.ASCII.GetString(header).TrimEnd('\r', '\n');
            var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var publication = fields[0] is "PUB" or "HPUB";
            var body = Array.Empty<byte>();
            if (publication)
            {
                var length = int.Parse(fields[^1], CultureInfo.InvariantCulture);
                if (length is < 0 or > 16 * 1024 * 1024)
                {
                    throw new IOException("fixture NATS publication exceeds bound");
                }
                body = new byte[length + 2];
                await input.ReadExactlyAsync(body, token);
                var offset = fields[0] == "HPUB" ? int.Parse(fields[^2], CultureInfo.InvariantCulture) : 0;
                if (Volatile.Read(ref _drop)?.Invoke(fields[1], body.AsMemory(offset, length - offset)) == true)
                {
                    _dropped.TrySetResult();
                    continue;
                }
            }
            await output.WriteAsync(header, token);
            if (body.Length > 0)
            {
                await output.WriteAsync(body, token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        foreach (var client in _clients.Keys)
        {
            client.Dispose();
        }
        await _accept;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
