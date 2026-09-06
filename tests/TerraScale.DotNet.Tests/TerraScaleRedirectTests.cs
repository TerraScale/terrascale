using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleRedirectTests
{
    [Test]
    [Arguments(307)]
    [Arguments(308)]
    public async Task DependencyInjectionDoesNotReplayMutationsAcrossRedirects(int status)
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        target.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = ServeRedirectAsync(origin, targetPort, status, deadline.Token);
        var services = new ServiceCollection();
        services.AddTerraScaleClient(options =>
        {
            options.BaseAddress = new Uri($"http://127.0.0.1:{originPort}");
            options.ApiKey = "loopback-conformance-key";
        });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<TerraScaleClient>();

        var error = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Sql.ExecuteAsync("db", TerraScaleSqlQuery.Raw("SELECT * FROM docs"), cancellationToken: deadline.Token));
        await serve;

        await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(target.Pending()).IsFalse();
    }

    private static async Task ServeRedirectAsync(TcpListener origin, int targetPort, int status, CancellationToken cancellationToken)
    {
        using var connection = await origin.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 }) { }
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Redirect\r\nLocation: http://127.0.0.1:{targetPort}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
    }
}
