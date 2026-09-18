using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleRegionsClientTests
{
    [Test]
    public async Task GetAsync_UsesThePublishedRegionDiscoveryRoute()
    {
        using var handler = new RecordingHandler("{\"region\":\"GRU\",\"healthy\":true,\"version\":\"test\"}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var status = await client.Regions.GetAsync();

        await Assert.That(handler.Request).IsEqualTo("https://api.example.invalid/api/v1/regions");
        await Assert.That(handler.Authorization).IsEqualTo("Bearer test-key");
        await Assert.That(status.Region).IsEqualTo("GRU");
        await Assert.That(status.Healthy).IsTrue();
        await Assert.That(status.Version).IsEqualTo("test");
    }

    [Test]
    public async Task HealthAsync_UsesSeparateMemberHealthRoute()
    {
        using var handler = new RecordingHandler("{\"region\":\"GRU\",\"healthy\":true,\"version\":\"test\"}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var status = await client.Regions.HealthAsync();

        await Assert.That(handler.Request).IsEqualTo("https://api.example.invalid/api/v1/health");
        await Assert.That(handler.Authorization).IsEqualTo("Bearer test-key");
        await Assert.That(status.Region).IsEqualTo("GRU");
        await Assert.That(status.Healthy).IsTrue();
        await Assert.That(status.Version).IsEqualTo("test");
    }

    [Test]
    public async Task HealthAsync_DecodesTypedUnhealthyResponseOn503()
    {
        using var handler = new RecordingHandler("{\"region\":\"GRU\",\"healthy\":false,\"version\":\"test\"}", HttpStatusCode.ServiceUnavailable);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var status = await client.Regions.HealthAsync();

        await Assert.That(status.Region).IsEqualTo("GRU");
        await Assert.That(status.Healthy).IsFalse();
    }

    [Test]
    public async Task GetAsync_DecodesTheTypedUnhealthyRegionResponse()
    {
        using var handler = new RecordingHandler("{\"region\":\"GRU\",\"healthy\":false,\"version\":\"test\"}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var status = await client.Regions.GetAsync();

        await Assert.That(status.Region).IsEqualTo("GRU");
        await Assert.That(status.Healthy).IsFalse();
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private sealed class RecordingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Request { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") });
        }
    }
}
