using System.Net;

namespace TerraScale.DotNet.TerraBase;

public static class TerraBaseHttp
{
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
