using System.Net;
using System.Net.Sockets;

namespace TerraBase.E2E;

/// <summary>The partition switch itself: cutting a link resets its connections, however their own closing races with it.</summary>
public sealed class PeerLinkTests
{
    // A connection's pump aborts its client when it ends, and Reset (a partition) aborts every open client: both can reach
    // the same client. The second abort finds its socket already gone.
    [Test]
    [Scenario(Nodes.Harness)]
    public async Task Aborting_a_connection_twice_does_not_throw()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var accepted = await listener.AcceptTcpClientAsync();

        PeerLink.Abort(client);
        PeerLink.Abort(client);
        await Assert.That(client.Connected).IsFalse();
    }

    // Many connections close from their own side while partitions reset the link from several threads at once: no reset
    // throws, and the link still carries a new connection afterwards.
    [Test]
    [Scenario(Nodes.Harness)]
    public async Task Resetting_a_link_while_its_connections_close_never_throws()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var connection = await target.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(async () =>
                {
                    using (connection)
                    {
                        try
                        {
                            await connection.GetStream().CopyToAsync(connection.GetStream(), stop.Token);
                        }
                        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
                        {
                        }
                    }
                });
            }
        });

        await using var link = new PeerLink((IPEndPoint)target.LocalEndpoint);
        var port = new Uri(link.Address).Port;
        for (var round = 0; round < 20; round++)
        {
            var clients = new List<TcpClient>();
            for (var i = 0; i < 32; i++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                await client.GetStream().WriteAsync(new byte[] { 1 });
                clients.Add(client);
            }

            // Wait until every connection is pumping (each one echoes its byte).
            foreach (var client in clients)
            {
                var echo = new byte[1];
                await client.GetStream().ReadExactlyAsync(echo);
            }

            var resets = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    link.Reset();
                }
            }));
            var closes = Task.Run(() => clients.ForEach(client => client.Dispose()));
            await Task.WhenAll([.. resets, closes]);
        }

        using (var after = new TcpClient())
        {
            await after.ConnectAsync(IPAddress.Loopback, port);
            await after.GetStream().WriteAsync(new byte[] { 7 });
            var echo = new byte[1];
            await after.GetStream().ReadExactlyAsync(echo);
            await Assert.That(echo[0]).IsEqualTo((byte)7);
        }

        await stop.CancelAsync();
        target.Stop();
        await serving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}
