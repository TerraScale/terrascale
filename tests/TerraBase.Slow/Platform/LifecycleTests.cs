using System.Net;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

public sealed class LifecycleTests
{
    [Test]
    public async Task Repeated_start_and_stop_releases_the_listener()
    {
        for (var round = 0; round < 20; round++)
        {
            await using var server = await TerraBaseTestServer.StartAsync();
            using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task Concurrent_probes_all_succeed()
    {
        await using var server = await TerraBaseTestServer.StartAsync();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 256).Select(async _ =>
        {
            using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
            return response.StatusCode;
        }));

        await Assert.That(statuses.All(status => status == HttpStatusCode.OK)).IsTrue();
    }
}
