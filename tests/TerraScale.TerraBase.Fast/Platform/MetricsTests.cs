using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class MetricsTests
{
    [Test]
    public async Task Each_server_exports_only_its_own_http_and_feature_measurements()
    {
        await using var first = await TerraBaseTestServer.StartAsync();
        await using var second = await TerraBaseTestServer.StartAsync();
        var firstMeter = first.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        var secondMeter = second.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        firstMeter.CreateCounter<long>("terrabase_test_isolation").Add(7);
        secondMeter.CreateCounter<long>("terrabase_test_isolation").Add(11);
        using var health = await first.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        health.EnsureSuccessStatusCode();

        var firstMetrics = await first.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        var secondMetrics = await second.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        await Assert.That(firstMetrics).Contains("http_route=\"/healthz\"");
        await Assert.That(secondMetrics).DoesNotContain("http_route=\"/healthz\"");
        await Assert.That(firstMetrics.Split('\n').Single(line => line.StartsWith("terrabase_test_isolation_total{", StringComparison.Ordinal)).Split(' ')[1])
            .IsEqualTo("7");
        await Assert.That(secondMetrics.Split('\n').Single(line => line.StartsWith("terrabase_test_isolation_total{", StringComparison.Ordinal)).Split(' ')[1])
            .IsEqualTo("11");
    }

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
