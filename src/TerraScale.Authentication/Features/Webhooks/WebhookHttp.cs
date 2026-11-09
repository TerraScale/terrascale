using System.Net;
using System.Net.Sockets;
using TerraScale.Authentication.Features.Sso;

namespace TerraScale.Authentication.Features.Webhooks;

public static class WebhookHttp
{
    public const string HttpClientName = "webhook-delivery";

    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(1);

    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = ConnectionLifetime,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            var address = addresses.Length > 0 && addresses.All(PublicAddresses.IsPublic)
                ? addresses[0]
                : throw new HttpRequestException("The webhook endpoint has no public address.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static HttpMessageHandler GuardedHandler() => new HttpsOnlyHandler(Handler());

    private sealed class HttpsOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri is { Scheme: "https" }
                ? base.SendAsync(request, cancellationToken)
                : throw new HttpRequestException("Webhook endpoints must use https.");
    }
}
