using System.Net;
using System.Net.Sockets;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Store;

/// <summary>
/// The TerraBase process the suites share must start while other processes keep taking loopback ports. Main run
/// 38276954117 failed every test of the shared fixture in 0 ms: another listener took a port the harness had probed and
/// released before TerraBase bound it, and TerraBase exited with "address already in use".
/// </summary>
public sealed class TerraBaseTestServerTests
{
    [Test]
    public async Task TerraBase_starts_while_other_listeners_keep_taking_free_loopback_ports()
    {
        using var stop = new CancellationTokenSource();
        var held = new Queue<Socket>();
        // Another test's listeners: free loopback ports, taken and held a moment, over and over.
        var taker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                for (var i = 0; i < 64; i++)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    socket.Listen();
                    held.Enqueue(socket);
                }

                while (held.Count > 4000)
                {
                    held.Dequeue().Dispose();
                }

                await Task.Yield();
            }
        });

        var started = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try
            {
                var (server, unavailable) = await TerraBaseTestServer.StartAsync();
                return (server, error: unavailable);
            }
            catch (InvalidOperationException error)
            {
                return (server: (TerraBaseTestServer?)null, error: (string?)error.Message[..Math.Min(error.Message.Length, 300)]);
            }
        }));
        await stop.CancelAsync();
        await taker;
        while (held.TryDequeue(out var socket))
        {
            socket.Dispose();
        }

        foreach (var (server, _) in started)
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }
        }

        await Assert.That(started.Select(result => result.error)).All().Satisfy(error => error.IsNull());
    }
}
