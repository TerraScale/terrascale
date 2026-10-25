using System.Globalization;
using System.Net;

namespace TerraScale.Endpoints.Tests;

public sealed class RequestIdentityTests
{
    private static readonly string[] Destinations = ["https://auth.example/keys", "https://other.example/keys", "http://auth.example/keys", "https://auth.example:444/keys"];

    [Test]
    public async Task ConcurrentRequestsSignOnlyTheConfiguredHttpsOrigin()
    {
        const string secret = "synthetic-correlation-key";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseTerraScaleRequestIdentity(() => "00000000000000000000000000", secret);
        app.Run(async context =>
        {
            var propagation = RequestIdentity.CreatePropagationHandler(secret, new Uri("https://auth.example/tenant"));
            propagation.InnerHandler = new EchoHeaders();
            using var outbound = new HttpClient(propagation);
            foreach (var address in Destinations)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address);
                request.Headers.Add(RequestIdentity.Header, "untrusted");
                request.Headers.Add(RequestIdentity.ProofHeader, "untrusted");
                using var response = await outbound.SendAsync(request, context.RequestAborted);
                var trusted = address == "https://auth.example/keys";
                await Assert.That(response.Headers.Contains(RequestIdentity.Header)).IsEqualTo(trusted);
                await Assert.That(response.Headers.Contains(RequestIdentity.ProofHeader)).IsEqualTo(trusted);
                if (trusted)
                {
                    await Assert.That(response.Headers.GetValues(RequestIdentity.Header).Single()).IsEqualTo(context.TraceIdentifier);
                    await Assert.That(response.Headers.GetValues(RequestIdentity.ProofHeader).Single()).IsEqualTo(RequestIdentity.Sign(context.TraceIdentifier, secret));
                }
            }
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        await Task.WhenAll(Enumerable.Range(1, 16).Select(async number =>
        {
            var id = number.ToString("D26", CultureInfo.InvariantCulture);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add(RequestIdentity.Header, id);
            request.Headers.Add(RequestIdentity.ProofHeader, RequestIdentity.Sign(id, secret));
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Headers.GetValues(RequestIdentity.Header).Single()).IsEqualTo(id);
            await Assert.That(response.Headers.Contains(RequestIdentity.ProofHeader)).IsFalse();
        }));
    }

    private sealed class EchoHeaders : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            foreach (var header in request.Headers)
            { response.Headers.TryAddWithoutValidation(header.Key, header.Value); }
            return Task.FromResult(response);
        }
    }
}
