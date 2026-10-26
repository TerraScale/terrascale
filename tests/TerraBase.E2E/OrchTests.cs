using System.Net;

namespace TerraBase.E2E;

/// <summary>
/// Negative controls of a ts-orch run's target (<see cref="Orch.Nodes"/>) and identity check
/// (<see cref="Cluster.IdentifyAsync"/>): a missing or malformed node list is refused before any request, a node that does
/// not answer as its region (or, pinned, on the revision under test) stops the run, and a pinned run refuses scenarios
/// still listed as expected failures.
/// </summary>
[Category("local-only")]
public sealed class OrchTests
{
    private static readonly string[] Regions = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments(null, "names no node for gru, sjc, fra")]
    [Arguments("gru=http://10.0.0.1:18080,sjc=http://10.0.0.2:18080", "names no node for fra")]
    [Arguments("gru=http://10.0.0.1:18080,gru=http://10.0.0.2:18080,sjc=http://10.0.0.2:18080,fra=http://10.0.0.3:18080", "each region once")]
    [Arguments("gru=https://10.0.0.1:18080,sjc=http://10.0.0.2:18080,fra=http://10.0.0.3:18080", "each region once")]
    [Arguments("gru=http://10.0.0.1:18080/v1,sjc=http://10.0.0.2:18080,fra=http://10.0.0.3:18080", "each region once")]
    [Arguments("gru,sjc=http://10.0.0.2:18080,fra=http://10.0.0.3:18080", "each region once")]
    public async Task A_missing_or_malformed_node_list_is_refused_before_any_request(string? nodes, string reason)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(Orch.Nodes(_ => nodes, Regions)));
        await Assert.That(error!.Message).Contains(reason);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_node_list_names_each_regions_client_listener()
    {
        var nodes = Orch.Nodes(_ => "gru=http://10.0.0.1:18080, sjc=http://10.0.0.2:18080/,fra=http://10.0.0.3:18080", Regions);
        await Assert.That(nodes[Cluster.Sjc]).IsEqualTo(new Uri("http://10.0.0.2:18080/"));
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_pinned_run_refuses_a_scenario_still_listed_as_an_expected_failure()
    {
        await Assert.That(Gaps.PinnedRefusal("E13")).Contains("its Gaps row must be removed first");
        await Assert.That(Gaps.PinnedRefusal("E11")).IsNull();
        await Assert.That(Gaps.PinnedRefusal("not-a-gap")).IsNull();
    }

    // /healthz of gru's node: the run starts (and ends) only when it answers as a named node in gru (X-TerraBase-Machine) and,
    // on its internal listener, reports the revision under test.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments(HttpStatusCode.OK, """{"region":"gru"}""", "ts-gru-1", """{"revision":"rev1"}""", null)]
    [Arguments(HttpStatusCode.OK, """{"region":"sjc"}""", "ts-gru-1", """{"revision":"rev1"}""", "region sjc")]
    [Arguments(HttpStatusCode.OK, """{"region":"gru"}""", "ts-gru-1", """{"revision":"rev2"}""", "revision rev2")]
    [Arguments(HttpStatusCode.OK, """{"region":"gru"}""", "ts-gru-1", """{"status":"ok"}""", "revision (none)")]
    [Arguments(HttpStatusCode.OK, """{"status":"ok","version":"0.1.0","region":"gru"}""", null, """{"revision":"rev1"}""", "node (none)")]
    [Arguments(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"unavailable"}}""", "ts-gru-1", """{"revision":"rev1"}""", "503")]
    public async Task Each_node_must_answer_as_itself_running_the_revision_under_test(HttpStatusCode status, string body, string? machine, string build, string? refusal)
    {
        using var client = new HttpClient(new Stand(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (machine is not null)
            {
                response.Headers.Add("X-TerraBase-Machine", machine);
            }

            return response;
        }))
        { BaseAddress = new Uri("http://node.test") };
        using var internalClient = new HttpClient(new Stand(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(build) })) { BaseAddress = new Uri("http://node.test:8081") };
        var nodes = new Dictionary<string, HttpClient>(StringComparer.Ordinal) { [Cluster.Gru] = client };
        var builds = new Dictionary<string, HttpClient>(StringComparer.Ordinal) { [Cluster.Gru] = internalClient };
        if (refusal is null)
        {
            await Assert.That((await Cluster.IdentifyAsync(nodes, "rev1", builds))[Cluster.Gru]).IsEqualTo("ts-gru-1");
            return;
        }

        var error = await Assert.ThrowsAsync<Exception>(() => Cluster.IdentifyAsync(nodes, "rev1", builds));
        await Assert.That(error!.Message).Contains(refusal);
    }

    private sealed class Stand(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
