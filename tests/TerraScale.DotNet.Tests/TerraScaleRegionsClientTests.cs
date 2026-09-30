using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleRegionsClientTests
{
    private const string CatalogBody =
        """{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":-23.5475,"longitude":-46.6361,"available":true},{"code":"FRA","name":"Frankfurt","country":"DE","latitude":50.1109,"longitude":8.6821,"available":false}],"hosts":{"GRU":"https://gru.api.terrascale.tech","FRA":"https://fra.api.terrascale.tech"}}""";

    [Test]
    [Arguments("https://gru.api.terrascale.tech")]
    [Arguments("gru.api.terrascale.tech")]
    public async Task GetAsync_DecodesTheRegionCatalogFromTheConfiguredOrigin(string gruHost)
    {
        using var handler = new RecordingHandler(CatalogBody.Replace("https://gru.api.terrascale.tech", gruHost, StringComparison.Ordinal));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var catalog = await client.Regions.GetAsync();

        await Assert.That(handler.Request).IsEqualTo("https://api.example.invalid/api/v1/regions");
        await Assert.That(handler.Authorization).IsNull();
        await Assert.That(catalog.Regions.Select(region => region.Code)).IsEquivalentTo(["GRU", "FRA"]);
        await Assert.That(catalog.Regions[1].Available).IsFalse();
        await Assert.That(catalog.Hosts["GRU"].ToString()).IsEqualTo("https://gru.api.terrascale.tech/");
    }

    [Test]
    [Arguments("https://evil.example.com")]
    [Arguments("fra.api.terrascale.tech")]
    [Arguments("evil.example.com")]
    [Arguments("gru.auth.terrascale.tech")]
    [Arguments("gru.api.terrascale.tech:443")]
    public async Task GetAsync_RejectsHostsOutsideTheRegionalFamily(string gruHost)
    {
        using var handler = new RecordingHandler(
            CatalogBody.Replace("https://gru.api.terrascale.tech", gruHost, StringComparison.Ordinal));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Regions.GetAsync());

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(handler.Request).IsEqualTo("https://api.example.invalid/api/v1/regions");
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
