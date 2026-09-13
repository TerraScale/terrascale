// <copyright file="MetricsEndpointTests.cs" company="TerraScale">
// TerraBase observability endpoint coverage.
// </copyright>

namespace TerraBase.Api.Tests;

/// <summary>HTTP contract rows for the private metrics and probe routes.</summary>
public sealed class MetricsEndpointTests
{
    /// <summary>The private metrics route is anonymous and uses Prometheus text exposition.</summary>
    [Test]
    public async Task PrivateMetricsAndProbeRoutesAreMappedWithoutApiContext()
    {
        var socket = Path.Combine(Path.GetTempPath(), $"terrabase-metrics-{Guid.NewGuid():N}.sock");
        await using var engine = new ScriptedTbepEngine(socket);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = socket });

        using var metrics = await host.Http.GetAsync(PrometheusMetrics.Path);
        await Assert.That(metrics.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(metrics.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
        var body = await metrics.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("# TYPE terrabase_liveness gauge");
        await Assert.That(body).Contains("terrabase_in_flight_operations");

        using var live = await host.Http.GetAsync(PrometheusMetrics.LivePath);
        await Assert.That(live.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);

        foreach (var path in new[] { PrometheusMetrics.StartupPath, PrometheusMetrics.ReadyPath })
        {
            using var probe = await host.Http.GetAsync(path);
            await Assert.That(probe.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.ServiceUnavailable).IsTrue();
        }
    }
}
