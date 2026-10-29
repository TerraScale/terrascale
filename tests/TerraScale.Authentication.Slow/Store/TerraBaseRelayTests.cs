using System.Net;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Store;

public sealed class TerraBaseRelayTests
{
    [Test]
    public async Task The_fixture_keeps_more_than_64_simultaneous_public_connections_alive(CancellationToken cancellationToken)
    {
        await using var fixture = new TerraBaseFixture();
        await fixture.InitializeAsync();
        var clients = Enumerable.Range(0, 128).Select(_ => TerraBaseFixture.NewHttpClient()).ToArray();
        try
        {
            await ProbeAsync();
            await ProbeAsync();
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }

        async Task ProbeAsync() => await Task.WhenAll(clients.Select(async client =>
        {
            using var response = await client.GetAsync(new Uri(fixture.Server.ClientAddress, "healthz"), cancellationToken);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }));
    }
}
