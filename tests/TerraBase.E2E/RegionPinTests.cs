using System.Net;

namespace TerraBase.E2E;

/// <summary>
/// The Fly path's region check, against a stand-in for the proxy at the handler level. It runs without Fly, so the
/// refusal of a wrong-region answer runs on every local run and in the required local stage (the local-only category).
/// </summary>
[Category("local-only")]
public sealed class RegionPinTests
{
    [Test]
    [Scenario(Nodes.Harness)]
    public async Task Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it()
    {
        using var client = Client(pinned: Cluster.Gru, served: Cluster.Gru);

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        // The stand-in echoes the pin it received, so the body proves the header went out.
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(Cluster.Gru);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task Answer_served_from_another_region_is_refused()
    {
        using var client = Client(pinned: Cluster.Gru, served: Cluster.Sjc);

        await Assert.That(async () => await client.GetAsync(new Uri("/", UriKind.Relative))).Throws<RegionMismatchException>();
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task Answer_without_a_region_header_is_refused()
    {
        using var client = Client(pinned: Cluster.Sjc, served: null);

        await Assert.That(async () => await client.GetAsync(new Uri("/", UriKind.Relative))).Throws<RegionMismatchException>();
    }

    // A changes-feed resume enters at sjc and TerraBase replays it to gru, its cursor's issuer: only gru's answer counts,
    // and sjc answering it itself is refused like any other region.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments("gru", true)]
    [Arguments("sjc", false)]
    [Arguments("fra", false)]
    public async Task An_issuer_replayed_request_is_accepted_only_from_the_issuer(string served, bool accepted)
    {
        using var client = new HttpClient(new RegionPin(Cluster.Sjc, Cluster.Gru) { InnerHandler = new Stand(request => Answer(request, served)) })
        {
            BaseAddress = new Uri("http://fly.test"),
        };

        if (accepted)
        {
            using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(Cluster.Sjc);
        }
        else
        {
            await Assert.That(async () => await client.GetAsync(new Uri("/", UriKind.Relative))).Throws<RegionMismatchException>();
        }
    }

    // The local proxy follows fly-replay: instance=<machine> to that machine's node with Fly-Replay-Src, and, when that node
    // is down and the replay says fallback=force_self or prefer_self, back to the first node with fly-replay-failed (Fly's
    // fallback). Either way it reports the replay.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments(true, "force_self", "gru", "Fly-Replay-Src")]
    [Arguments(false, "force_self", "sjc", "fly-replay-failed")]
    [Arguments(false, "prefer_self", "sjc", "fly-replay-failed")]
    public async Task The_local_proxy_replays_to_an_instance_or_falls_back_to_itself(bool issuerUp, string fallback, string answeredBy, string marker)
    {
        var seen = new List<(string Node, bool Marked)>();
        var replays = new List<LocalFlyProxy.Replay>();
        using var gru = NodeClient(Cluster.Gru, seen, marker, fallback);
        using var sjc = NodeClient(Cluster.Sjc, seen, marker, fallback);
        HttpClient Node(string region)
        {
            return region == Cluster.Sjc ? sjc
                : issuerUp ? gru : throw new HttpRequestException("gru is down", null, HttpStatusCode.ServiceUnavailable);
        }
        using var client = new HttpClient(new LocalFlyProxy(Cluster.Sjc, Node, machine => machine == "m-gru" ? Cluster.Gru : null, replays.Add))
        {
            BaseAddress = new Uri("http://fly.test"),
        };

        using var response = await client.GetAsync(new Uri("/changes", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(answeredBy);
        await Assert.That(seen[^1]).IsEqualTo((answeredBy, true));
        await Assert.That(replays).IsEquivalentTo([new LocalFlyProxy.Replay(Cluster.Sjc, Cluster.Gru, "/changes", FellBack: !issuerUp)]);
    }

    // A node standing in for sjc (which answers a resume of gru's cursor with an issuer replay) or gru, recording each request.
    private static HttpClient NodeClient(string region, List<(string Node, bool Marked)> seen, string marker, string fallback) => new(new Stand(request =>
    {
        seen.Add((region, request.Headers.Contains(marker)));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(region) };
        if (region == Cluster.Sjc && !request.Headers.Contains("Fly-Replay-Src") && !request.Headers.Contains("fly-replay-failed"))
        {
            response.StatusCode = HttpStatusCode.MisdirectedRequest;
            response.Headers.Add("fly-replay", $"instance=m-gru;timeout=2s;fallback={fallback}");
        }

        return response;
    }))
    {
        BaseAddress = new Uri("http://node.test"),
    };

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
