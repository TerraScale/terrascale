using System.Net;
using System.Net.Sockets;

namespace TerraBase.E2E;

/// <summary>
/// One local machine's way to one peer: a TCP proxy on the real peer path (gRPC over h2c), which the machine's own view of
/// the member directory names instead of the peer's listener. <see cref="Cut"/> resets the open connections and refuses
/// new ones until it is cleared, as a network partition does; <see cref="Via"/> sends new connections through another
/// hop, such as a <see cref="LossyLink"/>.
/// </summary>
internal sealed class PeerLink : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly HashSet<TcpClient> _open = [];
    private readonly Task _accepting;
    private bool _cut;

    public PeerLink(IPEndPoint target)
    {
        Target = target;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    /// <summary>The peer's listener.</summary>
    public IPEndPoint Target { get; }

    /// <summary>Where the machine connects instead of <see cref="Target"/>.</summary>
    public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>The next hop of new connections, or null for <see cref="Target"/>.</summary>
    public IPEndPoint? Via { get; set; }

    /// <summary>Whether the link is cut: setting it resets every open connection.</summary>
    public bool Cut
    {
        get
        {
            lock (_gate)
            {
                return _cut;
            }
        }

        set
        {
            lock (_gate)
            {
                _cut = value;
            }

            if (value)
            {
                Reset();
            }
        }
    }

    /// <summary>Resets every open connection, so the machine reconnects (through <see cref="Via"/>, if set).</summary>
    public void Reset()
    {
        List<TcpClient> open;
        lock (_gate)
        {
            open = [.. _open];
            _open.Clear();
        }

        open.ForEach(Abort);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Reset();
        await _accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            if (Cut)
            {
                Abort(client);
                continue;
            }

            _ = PumpAsync(client);
        }
    }

    private async Task PumpAsync(TcpClient client)
    {
        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(Via ?? Target, _stop.Token);
            lock (_gate)
            {
                if (_cut)
                {
                    Abort(client);
                    return;
                }

                _open.Add(client);
                _open.Add(upstream);
            }

            var (down, up) = (client.GetStream(), upstream.GetStream());
            await Task.WhenAny(down.CopyToAsync(up, _stop.Token), up.CopyToAsync(down, _stop.Token));
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The link was cut or one side went away: the machine sees a reset and reconnects.
        }
        finally
        {
            lock (_gate)
            {
                _open.Remove(client);
                _open.Remove(upstream);
            }

            Abort(client);
        }
    }

    // A reset (RST), not an orderly close: what a dropped route looks like to gRPC. Reset and a connection's own pump can both
    // abort one client; the second finds its socket already gone (TcpClient.Client is null after Close).
    internal static void Abort(TcpClient client)
    {
        try
        {
            if (client.Client is { } socket)
            {
                socket.LingerState = new LingerOption(true, 0);
            }

            client.Close();
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
        }
    }
}
