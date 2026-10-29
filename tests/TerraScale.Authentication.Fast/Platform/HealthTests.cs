using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Observability;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

public sealed class HealthTests
{
    [Test]
    public async Task Liveness_reports_ok_and_the_service_version()
    {
        await using var server = await AuthTestServer.StartAsync();

        using var response = await server.Client.GetAsync("/healthz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("ok");
        await Assert.That(body.GetProperty("version").GetString()).StartsWith("0.1.0-alpha");
        // Anonymous health never names the build revision (ServiceVersion.Value carries it as +<commit> in CI builds).
        await Assert.That(body.GetProperty("version").GetString()).DoesNotContain("+");
    }


    [Test]
    public async Task Prometheus_metrics_are_served_only_on_the_metrics_listener()
    {
        await using var server = await AuthTestServer.StartAsync();
        using var metrics = new HttpClient { BaseAddress = server.MetricsAddress };
        using var health = await server.Client.GetAsync("/healthz");

        using var scrape = await metrics.GetAsync("/metrics");
        var text = await scrape.Content.ReadAsStringAsync();
        using var publicScrape = await server.Client.GetAsync("/metrics");
        using var otherPath = await metrics.GetAsync("/healthz");

        await Assert.That(scrape.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(text).Contains("# TYPE http_server_request_duration_seconds");
        await Assert.That(publicScrape.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(otherPath.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }


    [Test]
    public async Task Readiness_fails_closed_when_storage_is_not_reachable()
    {
        await using var server = await AuthTestServer.StartAsync();

        using var response = await server.Client.GetAsync("/readyz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("not_ready");
        await Assert.That(body.TryGetProperty("region", out _)).IsFalse();
    }


    [Test]
    public async Task Readiness_reports_the_storage_region_once_the_authority_answers()
    {
        var health = new FakeStorageHealth(healthy: true, region: "gru");
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));
        await Assert.That(await ReadinessWait.ReadyWithinAsync(server.Client, TimeSpan.FromSeconds(10))).IsTrue();

        using var response = await server.Client.GetAsync("/readyz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("ready");
        await Assert.That(body.GetProperty("region").GetString()).IsEqualTo("gru");
    }

}
