using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TerraScale.Authentication.Tests;

/// <summary>
/// One region's TerraBase machine for storage-routing tests: a loopback TCP relay to a real TerraBase listener that counts
/// the connections it accepts. <see cref="Stop"/> takes the region down (new connections are refused, open ones dropped),
/// <see cref="Restart"/> brings it back on the same port. <see cref="Hold"/> keeps what ts-auth sends from reaching
/// TerraBase until <see cref="Release"/>, as a machine that stalls without closing anything.
/// </summary>
public sealed class TcpRelay : IAsyncDisposable
{
    private readonly IPEndPoint target;
    private readonly ConcurrentDictionary<TcpClient, byte> open = new();
    private TcpListener listener;
    private Task accepting;
    private int accepted;
    private int held;
    private volatile bool stopped;
    private volatile TaskCompletionSource gate = Open();

    private TcpRelay(Uri target, IPEndPoint at)
    {
        this.target = new IPEndPoint(IPAddress.Parse(target.Host), target.Port);
        this.listener = Listen(at);
        this.EndPoint = (IPEndPoint)this.listener.LocalEndpoint;
        this.accepting = this.AcceptAsync(this.listener);
    }

    /// <summary>The relay's loopback address and port.</summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>The relay's port.</summary>
    public int Port => this.EndPoint.Port;

    /// <summary>The relay as a TerraBase origin.</summary>
    public Uri Address => new($"http://{this.EndPoint}/");

    /// <summary>Connections accepted so far.</summary>
    public int Accepted => Volatile.Read(ref this.accepted);

    /// <summary>Chunks of data held back by <see cref="Hold"/> so far.</summary>
    public int Held => Volatile.Read(ref this.held);

    /// <summary>Holds what ts-auth sends from now on, until <see cref="Release"/>.</summary>
    public void Hold() => this.gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets held and later data through.</summary>
    public void Release() => this.gate.TrySetResult();

    /// <summary>Starts a relay to <paramref name="target"/>, a loopback origin, on <paramref name="at"/> (a free 127.0.0.1 port by default).</summary>
    public static TcpRelay Start(Uri target, IPEndPoint? at = null) => new(target, at ?? new IPEndPoint(IPAddress.Loopback, 0));

    /// <summary>A random loopback address other than 127.0.0.1, so a test can own a fixed port (the internal listener's 8081) on it.</summary>
    public static IPAddress RandomLoopback() =>
        new([127, (byte)Random.Shared.Next(1, 255), (byte)Random.Shared.Next(0, 256), (byte)Random.Shared.Next(1, 255)]);

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
        this.listener = Listen(this.EndPoint);
        this.accepting = this.AcceptAsync(this.listener);
    }

    public async ValueTask DisposeAsync()
    {
        this.Release();
        this.Stop();
        await this.accepting;
    }

    private static TaskCompletionSource Open()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.SetResult();
        return open;
    }

    // ts-auth to TerraBase, through the hold gate.
    private async Task ForwardAsync(Stream from, Stream to)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await from.ReadAsync(buffer)) > 0)
        {
            var gate = this.gate;
            if (!gate.Task.IsCompleted)
            {
                Interlocked.Increment(ref this.held);
                await gate.Task;
            }

            await to.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private static TcpListener Listen(IPEndPoint at)
    {
        var listener = new TcpListener(at);
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
            await Task.WhenAny(this.ForwardAsync(downstream, toTarget), toTarget.CopyToAsync(downstream));
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
