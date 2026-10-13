using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TerraScale.Authentication.Tests;

/// <summary>
/// One region's TerraBase machine for storage-routing tests: a loopback TCP relay to a real TerraBase listener that counts
/// the connections it accepts. <see cref="Stop"/> takes the region down (new connections are refused, open ones dropped),
/// <see cref="Restart"/> brings it back on the same port.
/// </summary>
public sealed class TcpRelay : IAsyncDisposable
{
    private readonly IPEndPoint target;
    private readonly ConcurrentDictionary<TcpClient, byte> open = new();
    private TcpListener listener;
    private Task accepting;
    private int accepted;
    private volatile bool stopped;

    private TcpRelay(Uri target)
    {
        this.target = new IPEndPoint(IPAddress.Parse(target.Host), target.Port);
        this.listener = Listen(0);
        this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
        this.accepting = this.AcceptAsync(this.listener);
    }

    /// <summary>The relay's loopback port.</summary>
    public int Port { get; }

    /// <summary>The relay as a TerraBase origin.</summary>
    public Uri Address => new($"http://127.0.0.1:{this.Port}/");

    /// <summary>Connections accepted so far.</summary>
    public int Accepted => Volatile.Read(ref this.accepted);

    /// <summary>Starts a relay to <paramref name="target"/>, a loopback origin.</summary>
    public static TcpRelay Start(Uri target) => new(target);

    /// <summary>Refuses new connections and drops the open ones, as a machine that went away.</summary>
    public void Stop()
    {
        this.stopped = true;
        this.listener.Stop();
        foreach (var client in this.open.Keys)
        {
            client.Dispose();
        }
    }

    /// <summary>Accepts connections on the same port again.</summary>
    public void Restart()
    {
        this.stopped = false;
        this.listener = Listen(this.Port);
        this.accepting = this.AcceptAsync(this.listener);
    }

    public async ValueTask DisposeAsync()
    {
        this.Stop();
        await this.accepting;
    }

    private static TcpListener Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        return listener;
    }

    private async Task AcceptAsync(TcpListener from)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await from.AcceptTcpClientAsync();
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref this.accepted);
            _ = this.RelayAsync(client);
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using var upstream = new TcpClient();
        this.open[client] = 0;
        this.open[upstream] = 0;
        try
        {
            if (this.stopped)
            {
                // Accepted just before Stop, which may not have seen it.
                return;
            }

            await upstream.ConnectAsync(this.target);
            var downstream = client.GetStream();
            var toTarget = upstream.GetStream();
            await Task.WhenAny(downstream.CopyToAsync(toTarget), toTarget.CopyToAsync(downstream));
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Either side went away: the relayed connection ends with it.
        }
        finally
        {
            this.open.TryRemove(client, out _);
            this.open.TryRemove(upstream, out _);
            client.Dispose();
        }
    }
}
