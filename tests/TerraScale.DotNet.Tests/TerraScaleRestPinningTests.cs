using System.Net;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleRestPinningTests
{
    [Test]
    public async Task GlobalFirstContactPinsFollowingReadsAndWritesToServingRegion()
    {
        using var handler = new RestPinHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token"
            });

        _ = await client.Documents.GetAsync("orders", "items", "item-1");
        _ = await client.Documents.UpsertAsync(
            "orders",
            "items",
            "item-1",
            new { status = "paid" });

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests[0].Host).IsEqualTo("api.terrascale.tech");
        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(handler.Requests[1].Host).IsEqualTo("sfo.api.terrascale.tech");
        await Assert.That(handler.Requests[1].Method).IsEqualTo(HttpMethod.Put);
    }

    [Test]
    public async Task MisdirectedResponseRepairsPinOnceFromPlacementHeader()
    {
        using var handler = new RestPinHandler(misdirectFirstRequest: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token"
            });

        var document = await client.Documents.GetAsync("orders", "items", "item-1");

        await Assert.That(document.Id).IsEqualTo("item-1");
        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests[0].Host).IsEqualTo("api.terrascale.tech");
        await Assert.That(handler.Requests[1].Host).IsEqualTo("fra.api.terrascale.tech");
    }

    [Test]
    public async Task RepeatedMisdirectionStopsAfterOneRepair()
    {
        using var handler = new RestPinHandler(
            misdirectFirstRequest: true,
            alwaysMisdirect: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token"
            });

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync("orders", "items", "item-1"));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception?.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests[1].Host).IsEqualTo("fra.api.terrascale.tech");
    }

    private sealed class RestPinHandler(
        bool misdirectFirstRequest = false,
        bool alwaysMisdirect = false) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Host)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri
                ?? throw new InvalidOperationException("The request URI was not resolved.");
            Requests.Add((request.Method, requestUri.Host));

            if (misdirectFirstRequest && (Requests.Count == 1 || alwaysMisdirect))
            {
                var misdirected = new HttpResponseMessage(HttpStatusCode.MisdirectedRequest)
                {
                    Content = new StringContent(
                        "{\"error\":{\"code\":\"misdirected_request\",\"message\":\"Wrong region\"}}")
                };
                misdirected.Headers.TryAddWithoutValidation("X-TerraScale-Placement", "FRA,SFO");
                return Task.FromResult(misdirected);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Put
                    ? "{\"revision\":\"r1\"}"
                    : "{\"status\":\"paid\"}")
            };
            response.Headers.TryAddWithoutValidation("ETag", "\"r1\"");
            response.Headers.TryAddWithoutValidation("X-TerraScale-Region", "SFO");
            return Task.FromResult(response);
        }
    }
}
