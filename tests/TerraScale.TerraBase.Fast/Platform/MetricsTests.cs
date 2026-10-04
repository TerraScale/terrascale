using System.Net;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Platform;

public sealed class MetricsTests
{
    [Test]
    public async Task Metrics_are_exposed_in_prometheus_text_format()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        // Generate an HTTP server measurement before scraping.
        using var warmup = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));

        using var response = await server.Client.GetAsync(new Uri("/metrics", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
        await Assert.That(text).Contains("http_server_request_duration_seconds");
    }
}
