using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class MetricsTests
{
    [Test]
    public async Task Metrics_are_exposed_in_prometheus_text_format()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        // Generate an HTTP server measurement before scraping.
        using var warmup = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));

        // Kestrel records the request duration after the response is sent: a scrape can arrive before it.
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await server.Client.GetAsync(new Uri("/metrics", UriKind.Relative), token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
            return (await response.Content.ReadAsStringAsync(token)).Contains("http_server_request_duration_seconds", StringComparison.Ordinal);
        });
    }
}
