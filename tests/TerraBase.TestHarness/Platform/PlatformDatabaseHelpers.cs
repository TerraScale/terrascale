using System.Text;
using TerraBase.Configuration;
using TerraBase.Testing;

namespace TerraBase.Testing;

internal static class PlatformDatabaseFixture
{
    // Test-only transport: serve the production issuer's discovery/JWKS with this test's private signing keys.
    internal sealed class GlobalIssuerRoute : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(request.RequestUri!.AbsoluteUri.Replace(TerraBaseOptions.PlatformAuthIssuer, TestIssuer.Issuer, StringComparison.Ordinal));
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri.AbsoluteUri == TestIssuer.Discovery && response.IsSuccessStatusCode)
            {
                var discovery = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Content.Dispose();
                response.Content = new StringContent(discovery.Replace(TestIssuer.Issuer, TerraBaseOptions.PlatformAuthIssuer, StringComparison.Ordinal),
                    Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}
