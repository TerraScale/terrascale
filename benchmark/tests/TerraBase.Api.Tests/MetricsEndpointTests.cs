using System.Net;
using System.Text.Json;

namespace TerraBase.Api.Tests;

/// <summary>HTTP coverage for the metrics host and its component probes.</summary>
public sealed class MetricsEndpointTests
{
    [Test]
    public async Task MetricsAndProbesUseTheLocalRegistry()
    {
        await using var host = await HostHarness.StartAsync();

        using var metrics = await host.Http.GetAsync(PrometheusMetrics.Path);
        await Assert.That(metrics.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(metrics.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
        var body = await metrics.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("# TYPE terrabase_liveness gauge");
        await Assert.That(body).Contains("terrabase_in_flight_operations");

        using var live = await host.Http.GetAsync(PrometheusMetrics.LivePath);
        await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);

        foreach (var path in new[] { "/health", PrometheusMetrics.StartupPath, PrometheusMetrics.ReadyPath })
        {
            using var probe = await host.Http.GetAsync(path);
            await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }

        foreach (var component in Enum.GetValues<Component>())
        {
            host.Metrics.SetComponentHealth(component, HealthStatus.Healthy);
        }

        host.Metrics.SetServiceState(ServiceState.Serving);
        foreach (var path in new[] { "/health", PrometheusMetrics.StartupPath, PrometheusMetrics.ReadyPath })
        {
            using var probe = await host.Http.GetAsync(path);
            await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        host.Metrics.SetComponentHealth(Component.Storage, HealthStatus.Unhealthy);
        using var unhealthy = await host.Http.GetAsync("/health");
        await Assert.That(unhealthy.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var unready = await host.Http.GetAsync(PrometheusMetrics.ReadyPath);
        await Assert.That(unready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task OpenApiDescribesTheObservabilitySurface()
    {
        await using var host = await HostHarness.StartAsync();
        using var response = await host.Http.GetAsync("/openapi/v1.json");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths").EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        var expected = new[]
        {
            "/health", PrometheusMetrics.Path, PrometheusMetrics.LivePath,
            PrometheusMetrics.StartupPath, PrometheusMetrics.ReadyPath,
        }.Order(StringComparer.Ordinal).ToArray();
        await Assert.That(paths.SequenceEqual(expected, StringComparer.Ordinal)).IsTrue();
    }
}
