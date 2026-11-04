using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TerraBase.Testing;

internal sealed class TcpGate : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IPEndPoint _target;
    private readonly ConcurrentDictionary<Socket, bool> _open = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private volatile bool _cut;

    public TcpGate(int targetPort)
    {
        _target = new IPEndPoint(IPAddress.Loopback, targetPort);
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Cut()
    {
        _cut = true;
        foreach (var socket in _open.Keys)
        {
            Close(socket);
        }
    }

    public void Heal() => _cut = false;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Cut();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                return;
            }

            if (_cut)
            {
                client.Dispose();
                continue;
            }

            _ = PipeAsync(client);
        }
    }

    private async Task PipeAsync(Socket client)
    {
        var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _open[client] = true;
        _open[server] = true;
        try
        {
            await server.ConnectAsync(_target, _stop.Token);
            await Task.WhenAny(CopyAsync(client, server), CopyAsync(server, client));
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            Close(client);
            Close(server);
        }
    }

    private async Task CopyAsync(Socket from, Socket to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!_cut)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                if (read == 0 || _cut)
                {
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _stop.Token);
            }
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void Close(Socket socket)
    {
        if (_open.TryRemove(socket, out _))
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
            }

            socket.Dispose();
        }
    }
}
