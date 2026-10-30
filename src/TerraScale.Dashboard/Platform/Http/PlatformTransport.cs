using System.Net;

namespace TerraScale.Dashboard.Platform.Http
{
    // One warm connection per HTTP upstream. A new TLS connection to the edge costs
    // about 40 ms and a reused one under 5 ms (measured from gru), and one page makes several concurrent ts-auth calls.
    public static class PlatformTransport
    {
        // Follow DNS changes every two minutes and retain idle connections for the same interval.
        public static SocketsHttpHandler CreateHandler()
        {
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
            };
        }

        // HttpRequestMessage defaults to HTTP/1.1 and so needs a connection per concurrent
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
