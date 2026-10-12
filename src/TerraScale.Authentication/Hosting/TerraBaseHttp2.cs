using System.Net;

namespace TerraScale.Authentication.Hosting;

/// <summary>
/// Sends every request as HTTP/2 with prior knowledge: TerraBase's internal listener is cleartext HTTP/2 only (it serves
/// gRPC on the same port), so an HTTP/1.1 request there fails.
/// </summary>
internal sealed class TerraBaseHttp2(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return base.SendAsync(request, cancellationToken);
    }
}
