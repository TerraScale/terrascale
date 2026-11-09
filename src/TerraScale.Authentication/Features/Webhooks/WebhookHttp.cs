using System.Net;
using TerraScale.Authentication.Features.Http;

namespace TerraScale.Authentication.Features.Webhooks;

public static class WebhookHttp
{
    public const string HttpClientName = "webhook-delivery";

    public static HttpMessageHandler GuardedHandler() => new HttpsOnlyHandler(PublicAddresses.Handler());

    private sealed class HttpsOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri is { Scheme: "https" }
                ? base.SendAsync(request, cancellationToken)
                : throw new HttpRequestException("Webhook endpoints must use https.");
    }
}
