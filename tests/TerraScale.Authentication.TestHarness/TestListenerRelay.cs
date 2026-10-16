using System.Net;
using System.Net.Sockets;

namespace TerraScale.Authentication.Tests;

// Owns one front port for the whole fixture; stopping a backend never releases that port.
internal sealed class TestListenerRelay : IAsyncDisposable
{
    // Parallel store races keep hundreds of HTTP/1.1 connections pooled between requests.
    private const int MaximumConnections = 1024;
    private readonly Lock gate = new();
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Task> connections = [];
    private readonly Task accepting;
    private IPEndPoint? backend;
    private CancellationTokenSource? forwarding;

    internal TestListenerRelay()
    {
        try
        {
            this.listener.ExclusiveAddressUse = true;
            this.listener.Start(MaximumConnections);
            this.Address = new Uri($"http://127.0.0.1:{((IPEndPoint)this.listener.LocalEndpoint).Port}/");
            this.accepting = this.AcceptAsync();
        }
        catch
        {
            this.listener.Stop();
            this.lifetime.Dispose();
            throw;
        }
    }

    internal Uri Address { get; }

    internal void SetBackend(int port)
    {
        lock (this.gate)
        {
            if (this.forwarding is not null)
            {
                throw new InvalidOperationException("Stop the previous backend before replacing it.");
            }

            this.forwarding = new CancellationTokenSource();
            this.backend = new IPEndPoint(IPAddress.Loopback, port);
        }
    }

    internal void RefuseNewConnections()
    {
        lock (this.gate)
        {
            this.backend = null;
        }
    }

    internal async Task StopBackendAsync()
    {
        CancellationTokenSource? stopping;
        Task[] pending;
        lock (this.gate)
        {
            this.backend = null;
            stopping = this.forwarding;
            this.forwarding = null;
            pending = this.connections.ToArray();
            this.connections.Clear();
        }

        using (stopping)
        {
            if (stopping is not null)
            {
                await stopping.CancelAsync();
            }

            await Task.WhenAll(pending);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.lifetime.CancelAsync();
        this.listener.Stop();
        try
        {
            await Task.WhenAll(this.StopBackendAsync(), this.accepting);
        }
        finally
        {
            this.lifetime.Dispose();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await this.listener.AcceptTcpClientAsync(this.lifetime.Token);
                lock (this.gate)
                {
                    this.connections.RemoveAll(task => task.IsCompletedSuccessfully);
                    if (this.backend is null || this.lifetime.IsCancellationRequested || this.connections.Count >= MaximumConnections)
                    {
                        client.Dispose();
                    }
                    else
                    {
                        this.connections.Add(RelayAsync(client, this.backend, this.forwarding!.Token));
                    }
                }
            }
        }
        catch (Exception error) when (this.lifetime.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The fixture disposed its listening socket.
        }
    }

    private static async Task RelayAsync(TcpClient client, IPEndPoint backend, CancellationToken cancellationToken)
    {
        using (client)
        using (var upstream = new TcpClient())
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                await upstream.ConnectAsync(backend, connection.Token);
                await Task.WhenAll(CopyAsync(client, upstream, connection), CopyAsync(upstream, client, connection));
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // A disconnected peer or stopped backend ends this connection.
            }
        }
    }

    private static async Task CopyAsync(TcpClient from, TcpClient to, CancellationTokenSource connection)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream(), 16 * 1024, connection.Token);
            to.Client.Shutdown(SocketShutdown.Send);
        }
        catch
        {
            await connection.CancelAsync();
            throw;
        }
    }
}
