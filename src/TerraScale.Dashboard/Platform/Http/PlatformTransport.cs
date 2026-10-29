using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Http
{
    // One warm connection per upstream for ts-auth and TerraBase's public API. A new TLS connection to the edge costs
    // about 40 ms and a reused one under 5 ms (measured from gru), and one page makes several concurrent ts-auth calls.
    public static class PlatformTransport
    {
        // TerraBaseHttp already replaces pooled connections every 2 minutes so DNS changes are followed; idle connections
        // live as long, instead of closing after the default minute between page views.
        public static SocketsHttpHandler CreateHandler()
        {
            var handler = TerraBaseHttp.CreateHandler();
            handler.PooledConnectionIdleTimeout = handler.PooledConnectionLifetime;
            return handler;
        }

        // The SDKs send their own HttpRequestMessage, which defaults to HTTP/1.1 and so needs a connection per concurrent
        // call. HTTPS requests offer HTTP/2 to multiplex them on one connection, and fall back where it is not served.
        public sealed class PreferHttp2 : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri?.Scheme == Uri.UriSchemeHttps && request.VersionPolicy == HttpVersionPolicy.RequestVersionOrLower && request.Version < HttpVersion.Version20)
                {
                    request.Version = HttpVersion.Version20;
                    request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                }
                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
