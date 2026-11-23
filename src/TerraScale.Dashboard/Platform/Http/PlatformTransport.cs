using System.Net;

namespace TerraScale.Dashboard.Platform.Http
{
    public static class PlatformTransport
    {
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
