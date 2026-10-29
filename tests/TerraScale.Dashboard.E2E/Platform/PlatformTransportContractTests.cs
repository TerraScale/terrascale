using System.Net;
using TerraScale.Dashboard.Platform.Http;

namespace TerraScale.Dashboard.E2E.Platform
{
    public sealed class PlatformTransportContractTests
    {
        [Test]
        [Arguments("https://auth.terrascale.tech/terrascale/oauth/userinfo", "1.1", HttpVersionPolicy.RequestVersionOrLower, "2.0")]
        [Arguments("http://127.0.0.1:8081/v1/regions", "1.1", HttpVersionPolicy.RequestVersionOrLower, "1.1")]
        [Arguments("https://gru.api.terrascale.tech/v1/regions", "2.0", HttpVersionPolicy.RequestVersionExact, "2.0")]
        [Arguments("https://api.terrascale.tech/v1/regions", "1.1", HttpVersionPolicy.RequestVersionExact, "1.1")]
        public async Task HttpsSdkRequestsOfferHttp2AndExplicitVersionsStay(string url, string version, HttpVersionPolicy policy, string sent)
        {
            var seen = new Capture();
            using var invoker = new HttpMessageInvoker(new PlatformTransport.PreferHttp2 { InnerHandler = seen });
            using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = Version.Parse(version), VersionPolicy = policy };
            using var _ = await invoker.SendAsync(request, default);
            await Assert.That(seen.Version).IsEqualTo(Version.Parse(sent));
            await Assert.That(seen.Policy).IsEqualTo(policy)
                .Because("Concurrent ts-auth calls share one HTTP/2 connection; servers without HTTP/2 still get HTTP/1.1.");
        }

        [Test]
        public async Task PooledConnectionsOutliveIdleGapsButStillRecycleForDns()
        {
            using var handler = PlatformTransport.CreateHandler();
            await Assert.That(handler.PooledConnectionLifetime).IsEqualTo(TimeSpan.FromMinutes(2));
            await Assert.That(handler.PooledConnectionIdleTimeout).IsEqualTo(handler.PooledConnectionLifetime);
            await Assert.That(handler.AllowAutoRedirect).IsFalse();
        }

        private sealed class Capture : HttpMessageHandler
        {
            public Version? Version { get; private set; }
            public HttpVersionPolicy Policy { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Version = request.Version; Policy = request.VersionPolicy;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
