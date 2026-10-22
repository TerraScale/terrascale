using System.Net;

namespace TerraBase.E2E;

/// <summary>
/// The edge's answering-region check against an in-process handler: a wrong or missing region is refused, and a cursor
/// replay accepts only the expected issuer region.
/// </summary>
[Category("local-only")]
public sealed class RegionPinTests
{
    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_request_is_preserved_and_accepted_when_its_region_serves_it()
    {
        var headers = new List<string>();
        using var client = new HttpClient(new RegionPin(Cluster.Gru)
        {
            InnerHandler = new Stand(request =>
            {
                headers.AddRange(request.Headers.Select(header => header.Key));
                return Answer(request, Cluster.Gru);
            }),
        })
        { BaseAddress = new Uri("http://edge.test") };

        using var response = await client.GetAsync(new Uri("/probe?value=region", UriKind.Relative));

        await Assert.That(headers).IsEmpty();
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("/probe?value=region");
        await Assert.That(response.Headers.GetValues("X-TerraBase-Region").Single()).IsEqualTo(Cluster.Gru);
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

    // A changes-feed resume enters at sjc and the edge replays it to gru, its cursor's issuer region: only gru's answer counts,
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
            BaseAddress = new Uri("http://edge.test"),
        };

        if (accepted)
        {
            using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("/");
        }
        else
        {
            await Assert.That(async () => await client.GetAsync(new Uri("/", UriKind.Relative))).Throws<RegionMismatchException>();
        }
    }

    // The local edge follows ts-replay: region=<r> to that region's node with ts-replay-src, and, when that node is down
    // and the replay says fallback=self, back to the first node with ts-replay-failed; without it the 421 stands. It
    // reports each replay it followed.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments(true, ";fallback=self", "gru", "ts-replay-src")]
    [Arguments(false, ";fallback=self", "sjc", "ts-replay-failed")]
    [Arguments(false, "", "sjc", "")]
    public async Task The_local_edge_replays_to_a_region_or_falls_back_to_itself(bool issuerUp, string fallback, string answeredBy, string marker)
    {
        var seen = new List<(string Node, bool Marked)>();
        var replays = new List<LocalEdge.Replay>();
        using var gru = NodeClient(Cluster.Gru, seen, marker, fallback);
        using var sjc = NodeClient(Cluster.Sjc, seen, marker, fallback);
        HttpClient Node(string region)
        {
            return region == Cluster.Sjc ? sjc
                : issuerUp ? gru : throw new HttpRequestException("gru is down", null, HttpStatusCode.ServiceUnavailable);
        }
        using var client = new HttpClient(new LocalEdge(Cluster.Sjc, Node, replays.Add))
        {
            BaseAddress = new Uri("http://edge.test"),
        };

        using var response = await client.GetAsync(new Uri("/changes", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(marker.Length > 0 ? HttpStatusCode.OK : HttpStatusCode.MisdirectedRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(answeredBy);
        await Assert.That(seen[^1]).IsEqualTo((answeredBy, marker.Length > 0));
        LocalEdge.Replay[] expected = marker.Length > 0 ? [new(Cluster.Sjc, Cluster.Gru, "/changes", FellBack: !issuerUp)] : [];
        await Assert.That(replays).IsEquivalentTo(expected);
    }

    // A node standing in for sjc (which answers a resume of gru's cursor with an issuer replay) or gru, recording each request.
    private static HttpClient NodeClient(string region, List<(string Node, bool Marked)> seen, string marker, string fallback) => new(new Stand(request =>
    {
        seen.Add((region, marker.Length > 0 && request.Headers.Contains(marker)));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(region) };
        if (region == Cluster.Sjc && !request.Headers.Contains("ts-replay-src") && !request.Headers.Contains("ts-replay-failed"))
        {
            response.StatusCode = HttpStatusCode.MisdirectedRequest;
            response.Headers.Add("ts-replay", $"region={Cluster.Gru};instance=m-gru{fallback}");
        }

        return response;
    }))
    {
        BaseAddress = new Uri("http://node.test"),
    };

    private static HttpClient Client(string pinned, string? served) =>
        new(new RegionPin(pinned) { InnerHandler = new Stand(request => Answer(request, served)) }) { BaseAddress = new Uri("http://edge.test") };

    private static HttpResponseMessage Answer(HttpRequestMessage request, string? served)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.PathAndQuery) };
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
