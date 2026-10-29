using System.Net;
using System.Net.Sockets;
using System.Text;
using ApiBench;
using TUnit.Core;

namespace BenchClient.Tests;

public sealed class ParityLaneTests
{
    [Test]
    public async Task CountedConnectKeepsSdkHandlerOptionsAndCountsOnlySuccessfulConnects()
    {
        var connections = new ParityConnections();
        using var handler = connections.CreateHandler();
        await Assert.That(connections.Counted).IsTrue();
        await Assert.That(handler.AllowAutoRedirect).IsFalse();
        await Assert.That(handler.UseCookies).IsFalse();
        await Assert.That(handler.ConnectTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(handler.PooledConnectionLifetime).IsEqualTo(TimeSpan.FromMinutes(2));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var serve = ServeAsync(listener, 2);
        using var client = new HttpClient(handler, disposeHandler: false);
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.GetAsync(address);
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("ok");
        }
        await serve.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(connections.Count).IsEqualTo(1);
        listener.Stop();
        await Assert.That(async () => await client.GetAsync(address)).Throws<HttpRequestException>();
        await Assert.That(connections.Count).IsEqualTo(1);
    }

    // Minimal HTTP/1.1 keep-alive server: one connection, one fixed answer per request.
    private static async Task ServeAsync(TcpListener listener, int requests)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[4096];
        var seen = new StringBuilder();
        for (var served = 0; served < requests;)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) return;
            seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
            while (served < requests && seen.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) is var end and >= 0)
            {
                seen.Remove(0, end + 4);
                served++;
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray());
            }
        }
    }
}
