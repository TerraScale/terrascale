using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthorizationClientTests
{
    [Test]
    public async Task GetLeaseAsync_UsesTheAnonymousLeaseRouteAndMapsTheLease()
    {
        var response = """
            {
              "version": 3,
              "tenantId": "tenant-1",
              "epoch": "epoch-9",
              "featureEpoch": "feature-2",
              "audience": "https://sync.api.terrascale.tech",
              "region": "GRU",
              "issuedAt": 1700000000000,
              "validUntil": 1700003600000,
              "keyId": "key-7",
              "signature": "c2ln"
            }
            """;
        using var handler = new RecordingHandler(response);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var lease = await client.Authorization.GetLeaseAsync("tenant/1");

        await Assert.That(handler.Request).IsEqualTo("https://api.example.invalid/api/v1/authorization/leases/tenant%2F1");
        await Assert.That(handler.Authorization).IsNull();
        await Assert.That(lease.Version).IsEqualTo(3);
        await Assert.That(lease.TenantId).IsEqualTo("tenant-1");
        await Assert.That(lease.Epoch).IsEqualTo("epoch-9");
        await Assert.That(lease.FeatureEpoch).IsEqualTo("feature-2");
        await Assert.That(lease.Audience).IsEqualTo("https://sync.api.terrascale.tech");
        await Assert.That(lease.Region).IsEqualTo("GRU");
        await Assert.That(lease.IssuedAt).IsEqualTo(1700000000000L);
        await Assert.That(lease.ValidUntil).IsEqualTo(1700003600000L);
        await Assert.That(lease.KeyId).IsEqualTo("key-7");
        await Assert.That(lease.Signature).IsEqualTo("c2ln");
    }

    [Test]
    public async Task GetLeaseAsync_RejectsBlankTenantBeforeSending()
    {
        using var handler = new RecordingHandler("{}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Authorization.GetLeaseAsync(" "));

        await Assert.That(handler.Request).IsNull();
    }

    [Test]
    public async Task GetLeaseAsync_SurfacesTypedErrors()
    {
        using var handler = new RecordingHandler("{\"error\":{\"code\":\"not_found\",\"message\":\"no lease\",\"fix\":\"authenticate first\"}}", HttpStatusCode.NotFound);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var error = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Authorization.GetLeaseAsync("tenant-1"));

        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(error.ErrorCode).IsEqualTo("not_found");
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
