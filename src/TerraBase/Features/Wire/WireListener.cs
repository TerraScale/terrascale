using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using TerraBase.Configuration;
using TerraBase.Features.Pressure;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Wire;

/// <summary>
/// The wire protocol's TCP listener (docs/terrabase/wire-protocol.md) on its own sockets rather than Kestrel's transport: a
/// frame is read where its receive completes and an answer is sent by the request that completes it, with no transport queue
/// hop in between. It binds like the public listeners (every interface; loopback only in local development), counts its
/// connections as client connections, and drains like them at shutdown.
/// </summary>
internal sealed class WireListener(
    WireConnectionHandler handler, TerraBaseOptions options, IHostEnvironment environment, IPressureSignals pressure, ILogger<WireListener> logger)
    : IHostedService, IDisposable
{
    private const int Backlog = 512;

    // Cancelled when the drain budget ends: connections still open stop reading frames and close.
    private readonly CancellationTokenSource _drain = new();
    private readonly HashSet<Task> _connections = [];
    private Socket? _socket;
    private Task _accepting = Task.CompletedTask;

    /// <summary>The bound port: the configured one, or the one the system picked for <c>0</c>.</summary>
    public int Port { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var loopbackOnly = Listeners.LoopbackOnly(options, environment.EnvironmentName);
        var socket = loopbackOnly
            ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        try
        {
            socket.Bind(new IPEndPoint(loopbackOnly ? IPAddress.Loopback : IPAddress.IPv6Any, options.WirePort));
            socket.Listen(Backlog);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _accepting = AcceptAsync(socket);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // No new connections; open ones finish the requests they run within the drain budget, then close.
        _socket?.Dispose();
        await _accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Task[] open;
        lock (_connections)
        {
            open = [.. _connections];
        }

        _drain.CancelAfter(Shutdown.DrainBudget);
        await Task.WhenAll(open).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose()
    {
        _socket?.Dispose();
        _drain.Dispose();
    }

    private async Task AcceptAsync(Socket listener)
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                return;
            }

            socket.NoDelay = true;
            var connection = ServeAsync(socket);
            lock (_connections)
            {
                _connections.Add(connection);
            }

            _ = connection.ContinueWith(
                done =>
                {
                    lock (_connections)
                    {
                        _connections.Remove(done);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        // Off the accept loop: a connection's first frame never holds up the next accept.
        await Task.Yield();
        pressure.ConnectionOpened();
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(_drain.Token);
        var stream = new NetworkStream(socket, ownsSocket: true);
        await using (stream.ConfigureAwait(false))
        {
            // The client closing its side ends reading; a send to a gone client fails its flush.
            var input = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 64 * 1024, leaveOpen: true));
            var output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
            try
            {
                await handler.RunAsync(input, output, closed.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.ZLogWarning(error, $"A wire connection failed");
            }
            finally
            {
                await closed.CancelAsync().ConfigureAwait(false);
                await input.CompleteAsync().ConfigureAwait(false);
                await output.CompleteAsync().ConfigureAwait(false);
                pressure.ConnectionClosed();
            }
        }
    }
}
