using System.Net;

namespace TerraBase.E2E;

/// <summary>
/// The Fly path's region check, against a stand-in for the proxy at the handler level. It runs without Fly, so the
/// refusal of a wrong-region answer runs on every local run.
/// </summary>
public sealed class RegionPinTests
{
    [Test]
    public async Task Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it()
    {
        using var client = Client(pinned: Cluster.Gru, served: Cluster.Gru);

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        // The stand-in echoes the pin it received, so the body proves the header went out.
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(Cluster.Gru);
    }

    [Test]
    public async Task Answer_served_from_another_region_is_refused()
    {
        using var client = Client(pinned: Cluster.Gru, served: Cluster.Sjc);

        await Assert.That(async () => await client.GetAsync(new Uri("/", UriKind.Relative))).Throws<RegionMismatchException>();
    }

    [Test]
    public async Task Answer_without_a_region_header_is_refused()
    {
        using var client = Client(pinned: Cluster.Sjc, served: null);

        await Assert.That(async () => await client.GetAsync(new Uri("/", UriKind.Relative))).Throws<RegionMismatchException>();
    }

    private static HttpClient Client(string pinned, string? served) =>
        new(new RegionPin(pinned) { InnerHandler = new Stand(request => Answer(request, served)) }) { BaseAddress = new Uri("http://fly.test") };

    private static HttpResponseMessage Answer(HttpRequestMessage request, string? served)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.Headers.GetValues("fly-prefer-region").Single()) };
        if (served is not null)
        {
            response.Headers.Add("X-TerraBase-Region", served);
        }

        return response;
    }

    // The proxy's stand-in: answers each request it receives, without a network.
    private sealed class Stand(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
