namespace TerraScale.Auth.Tests;

using System.Net;

[NotInParallel]
public sealed class AuthMetricsTests
{
    [Test]
    public async Task MetricsRequiresThePrivateScrapeKeyAndReportsBoundedRequestTotals()
    {
        var service = await AuthServiceFixture.GetAsync();
        using var denied = await service.Http.GetAsync("/metrics");
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var operatorKey = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        operatorKey.Headers.Add("X-TerraScale-Metrics-Key", "operator-test-key");
        using var operatorResponse = await service.Http.SendAsync(operatorKey);
        await Assert.That(operatorResponse.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var ready = await service.Http.GetAsync("/readyz");
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Add("X-TerraScale-Metrics-Key", "metrics-test-key");
        using var response = await service.Http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("ts_auth_ready 1");
        await Assert.That(body).Contains("ts_auth_liveness 1");
        await Assert.That(body).Contains("ts_auth_dependency_ready{dependency=\"terrabase\"} 1");
        await Assert.That(body).Contains("ts_auth_http_requests_total");
        await Assert.That(body).DoesNotContain("tsauth-e2e-");
    }
}
