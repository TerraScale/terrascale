using System.Net;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Creates the transport configuration required by the TerraBase clients.</summary>
public static class TerraBaseHttp
{
    /// <summary>
    /// Creates a handler with redirects and cookies disabled. Redirects can disclose request bodies and acting-user
    /// headers to another origin. A connection attempt fails after 10 seconds with its own error instead of using the
    /// whole request deadline, and pooled connections are replaced every 2 minutes so a long-lived client follows DNS
    /// changes to TerraBase's regional endpoints. A plain-http or ws request to a loopback host connects directly,
    /// never through <see cref="HttpClient.DefaultProxy"/> (HTTP_PROXY, ALL_PROXY), so its credential stays on the
    /// machine; every other request uses that proxy. A system proxy configuration that lists several proxies uses only
    /// its first. Configure TLS or connection settings before using it in a caller-owned HttpClient.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        Proxy = new LoopbackDirect(),
    };

    internal sealed class LoopbackDirect : IWebProxy
    {
        public ICredentials? Credentials
        {
            get => HttpClient.DefaultProxy.Credentials;
            set => HttpClient.DefaultProxy.Credentials = value;
        }

        public Uri? GetProxy(Uri destination) => HttpClient.DefaultProxy.GetProxy(destination);

        public bool IsBypassed(Uri host) =>
            (host.Scheme == Uri.UriSchemeHttp || host.Scheme == Uri.UriSchemeWs) && host.IsLoopback || HttpClient.DefaultProxy.IsBypassed(host);
    }
}
