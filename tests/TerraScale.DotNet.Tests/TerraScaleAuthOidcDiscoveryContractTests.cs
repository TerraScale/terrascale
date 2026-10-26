using System.Net;
using System.Text;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthOidcDiscoveryContractTests
{
    [Test]
    public async Task DiscoveryAllowsOmittedIntrospectionAndExportsNoIntrospectionApi()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contracts/auth/fixtures/oidc-discovery.json"));
        using var handler = new Handler(json);
        using var http = new HttpClient(handler);
        var client = new TerraScaleAuthClient(http, new() { BaseAddress = new("https://auth.example.test"), InstanceSlug = "terrascale" });
        var metadata = await client.Oidc.GetDiscoveryDocumentAsync();
        await Assert.That(metadata.IntrospectionEndpoint).IsNull();
        await Assert.That(metadata.GrantTypesSupported.Contains("authorization_code")).IsTrue();
        await Assert.That(metadata.GrantTypesSupported.Contains("refresh_token")).IsTrue();
        await Assert.That(metadata.RevocationEndpoint).IsEqualTo("https://auth.terrascale.tech/terrascale/oauth/revoke");
        await Assert.That(typeof(TerraScaleAuthOidcClient).GetMethod("IntrospectTokenAsync")).IsNull();
        await Assert.That(typeof(TerraScaleAuthClient).Assembly.GetType("TerraScale.DotNet.TerraScaleAuthIntrospection")).IsNull();
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
