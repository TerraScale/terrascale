using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleRegionsClientTests
{
    [Test]
    public async Task ListAsync_UsesEdgeCatalogEnvelope()
    {
        using var handler = new RecordingHandler("{\"regions\":[{\"code\":\"GRU\",\"name\":\"Sao Paulo\",\"country\":\"BR\",\"latitude\":-23.43,\"longitude\":-46.47,\"available\":true}],\"hosts\":{\"GRU\":\"gru.api.terrascale.tech\"}}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var catalog = await client.Regions.ListAsync();

        await Assert.That(handler.Request).IsEqualTo("https://api.example.invalid/api/v1/regions");
        await Assert.That(catalog.Regions[0].Code).IsEqualTo("GRU");
        await Assert.That(catalog.Regions[0].Name).IsEqualTo("Sao Paulo");
        await Assert.That(catalog.Hosts["GRU"]).IsEqualTo("gru.api.terrascale.tech");
    }

    [Test]
    public async Task ListAsync_RejectsMissingCatalogFields()
    {
        using var handler = new RecordingHandler("{\"regions\":[{\"code\":\"GRU\",\"name\":\"Sao Paulo\",\"country\":\"BR\",\"latitude\":-23.43,\"longitude\":-46.47,\"available\":true}]}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => client.Regions.ListAsync());

        await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task HealthAsync_UsesSeparateMemberHealthRoute()
    {
        using var handler = new RecordingHandler("{\"region\":\"GRU\",\"healthy\":true}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var status = await client.Regions.HealthAsync();

        await Assert.That(handler.Request).IsEqualTo("https://api.example.invalid/api/v1/health");
        await Assert.That(status.Region).IsEqualTo("GRU");
        await Assert.That(status.Healthy).IsTrue();
    }

    [Test]
    public async Task CatalogAcceptsAdmittedRegionBeyondLaunchSet()
    {
        using var handler = new RecordingHandler("{\"regions\":[{\"code\":\"IAD\",\"name\":\"Ashburn\",\"country\":\"US\",\"latitude\":38.9,\"longitude\":-77,\"available\":true}],\"hosts\":{\"IAD\":\"iad.api.terrascale.tech\"}}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var catalog = await client.Regions.ListAsync();

        await Assert.That(catalog.Regions[0].Code).IsEqualTo("IAD");
    }

    [Test]
    public async Task GetAsync_RemainsExplicitContractGap()
    {
        using var handler = new RecordingHandler("{}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.Regions.GetAsync("gru"));

        await Assert.That(exception).IsNotNull();
        await Assert.That(handler.Request).IsNull();
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") });
        }
    }
}
