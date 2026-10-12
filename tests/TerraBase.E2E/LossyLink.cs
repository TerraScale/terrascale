using System.Net;
using System.Net.Sockets;

namespace TerraBase.E2E;

/// <summary>
/// A TCP proxy that makes one peer link lossy: every chunk waits a random delay up to <see cref="MaxDelay"/>, throughput
/// is capped at <see cref="BytesPerSecond"/>, and each connection is reset with probability <see cref="ResetChance"/>
/// per second it stays open. A reset drops whatever was in flight, so the sender must resend from what the receiver
/// acknowledged, and receiving something again must change nothing. Bytes are never altered: a corrupt group is refused
/// at the receiver's trust boundary, which fault injection inside the peer protocol tests, not this proxy.
/// </summary>
public sealed class LossyLink(IPEndPoint target, TimeSpan maxDelay, int bytesPerSecond, double resetChance, int seed) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Random _random = new(seed);
    private readonly Lock _gate = new();
    private Task _accepting = Task.CompletedTask;
    private int _resets;

    public TimeSpan MaxDelay { get; } = maxDelay;

    public int BytesPerSecond { get; } = bytesPerSecond;

    public double ResetChance { get; } = resetChance;

    /// <summary>Connections reset so far.</summary>
    public int Resets => Volatile.Read(ref _resets);

    /// <summary>Where peers connect instead of <c>target</c>.</summary>
    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

    public LossyLink Start()
    {
        _listener.Start();
        _accepting = AcceptAsync();
        return this;
    }

    public override string ToString() => $"a lossy link (delay up to {MaxDelay.TotalMilliseconds:F0} ms, {BytesPerSecond} B/s, {ResetChance:P0} resets per second)";

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _ = Task.Run(() => ConnectAsync(client));
        }
    }

    private async Task ConnectAsync(TcpClient client)
    {
        using var server = new TcpClient();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            await server.ConnectAsync(target, connection.Token);
            var reset = ResetAsync(connection);
            await Task.WhenAny(
                PumpAsync(client.GetStream(), server.GetStream(), connection.Token),
                PumpAsync(server.GetStream(), client.GetStream(), connection.Token),
                reset);
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await connection.CancelAsync();
            client.Client.LingerState = new LingerOption(true, 0); // RST, not FIN: the peer sees a broken link
            client.Dispose();
        }
    }

    // Once a second the connection is reset with ResetChance.
    private async Task ResetAsync(CancellationTokenSource connection)
    {
        while (!connection.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), connection.Token);
            if (Next() < ResetChance)
            {
                _ = Interlocked.Increment(ref _resets);
                return;
            }
        }
    }

    private async Task PumpAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
        {
            // The delay plus the time this chunk takes at the capped rate.
            var wait = (MaxDelay * Next()) + TimeSpan.FromSeconds((double)read / BytesPerSecond);
            await Task.Delay(wait, cancellationToken);
            await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private double Next()
    {
        lock (_gate)
        {
            return _random.NextDouble();
        }
    }
}
