using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Http;

namespace TerraScale.Authentication.Features.Webhooks;

public static class WebhookHttp
{
    public const string HttpClientName = "webhook-delivery";

    public static HttpMessageHandler GuardedHandler() => new HttpsOnlyHandler(PublicAddresses.Handler());

    public static IServiceCollection AddWebhookDelivery(this IServiceCollection services) => services
        .AddHttpClient(HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.MaxResponseContentBufferSize = 64 * 1024;
        })
        .RemoveAllLoggers()
        .ConfigurePrimaryHttpMessageHandler(GuardedHandler)
        .Services;

    private sealed class HttpsOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri is { Scheme: "https" }
                ? base.SendAsync(request, cancellationToken)
                : throw new HttpRequestException("Webhook endpoints must use https.");
    }
}
