using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleRegionsClientTests
{
    [Test]
    public async Task ListAsync_UsesHealthEnvelope()
    {
        using var handler = new RecordingHandler("{\"region\":\"GRU\",\"healthy\":true}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var status = await client.Regions.ListAsync();

        await Assert.That(handler.Request).IsEqualTo("https://api.example.invalid/api/v1/regions");
        await Assert.That(status.Region).IsEqualTo("GRU");
        await Assert.That(status.Healthy).IsTrue();
    }

    [Test]
    public async Task ListAsync_RejectsMissingRequiredFields()
    {
        using var handler = new RecordingHandler("{\"region\":\"GRU\"}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => client.Regions.ListAsync());

        await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task GetAsync_IsContractGapWithoutSendingARequest()
    {
        using var handler = new RecordingHandler("{}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<TerraScaleContractGapException>(() => client.Regions.GetAsync(TerraScaleRegions.Gru));

        await Assert.That(exception).IsNotNull();
        await Assert.That(handler.Request).IsNull();
    }

    [Test]
    public async Task GetAsync_RejectsInvalidCodeBeforeContractGap()
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
