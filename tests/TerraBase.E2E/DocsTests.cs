using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace TerraBase.E2E;

/// <summary>
/// The live outage proof's own calls against a stand-in for a region at the handler level: which refusals a probe write
/// sends again, the durability it reports, and the tombstone answer. It runs without Fly, so the required local stage (the
/// local-only category) checks them.
/// </summary>
[Category("local-only")]
public sealed class DocsTests
{
    private static readonly Uri Document = new("/v1/doc", UriKind.Relative);

    // Refused before execution: sent again until the region acknowledges it, and the acknowledgement's durability returned.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments("remote_region_unavailable")]
    [Arguments("confirmation_busy")]
    public async Task A_write_refused_before_execution_is_sent_again_and_answers_its_durability(string code)
    {
        var sends = 0;
        using var client = Client(_ => ++sends <= 2 ? Refused(code) : Acknowledged("remote_durable"));

        var durability = await Docs.PutRefusedBeforeRunAsync(client, Document, new JsonObject { ["probe"] = "gru" });

        await Assert.That(durability).IsEqualTo("remote_durable");
        await Assert.That(sends).IsEqualTo(3);
    }

    // An ambiguous commit (also when its message names a refusal code), a 503 that only mentions a code outside a JSON
    // string, and the proxy's own 503 may each have run (or say nothing): sent once, and the 503 is the failure.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments("{\"error\":{\"code\":\"write_outcome_unknown\",\"message\":\"m\",\"region\":\"gru\",\"machine\":\"m-gru\"}}")]
    [Arguments("{\"error\":{\"code\":\"write_outcome_unknown\",\"message\":\"remote_region_unavailable\"}}")]
    [Arguments("{\"error\":\"remote_region_unavailable\"}")]
    [Arguments("[\"remote_region_unavailable\"]")]
    [Arguments("{\"error\":{\"code\":[\"remote_region_unavailable\"]}}")]
    [Arguments("{\"code\":\"remote_region_unavailable\"}")]
    [Arguments("remote_region_unavailable")]
    [Arguments("")]
    public async Task Any_other_503_is_sent_exactly_once(string body)
    {
        var sends = 0;
        using var client = Client(_ =>
        {
            sends++;
            return Unavailable(body);
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Docs.PutRefusedBeforeRunAsync(client, Document, new JsonObject { ["probe"] = "gru" }));

        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(sends).IsEqualTo(1);
    }

    // The probe asserts remote_durable from this value, so a local acknowledgement or none must come back as it is.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments("{\"durability\":\"local_durable\"}", "local_durable")]
    [Arguments("{\"id\":\"x\"}", null)]
    [Arguments("", null)]
    public async Task The_acknowledged_durability_is_returned_as_answered(string body, string? durability)
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        await Assert.That(await Docs.PutRefusedBeforeRunAsync(client, Document, new JsonObject { ["probe"] = "gru" })).IsEqualTo(durability);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_deleted_documents_404_answers_its_tombstone_etag()
    {
        using var client = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.NotFound);
            response.Headers.ETag = new EntityTagHeaderValue("\"t1\"");
            return response;
        });

        await Assert.That(await Docs.TombstoneAsync(client, Document)).IsEqualTo("\"t1\"");
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_never_written_documents_404_answers_no_tombstone_and_names_it_in_a_failure()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var tag = await Docs.TombstoneAsync(client, Document);

        await Assert.That(tag).IsNull();
        await Assert.That(new ReplicationMatrixTests.Tombstones([(Cluster.Gru, "\"t1\""), (Cluster.Sjc, tag)]).ToString())
            .IsEqualTo("gru=\"t1\" sjc=no-tombstone(never-written-or-collected)");
    }

    // A live document or a failure is not a tombstone.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments(HttpStatusCode.OK)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    [Arguments(HttpStatusCode.InternalServerError)]
    public async Task Any_answer_but_404_is_not_a_tombstone(HttpStatusCode status)
    {
        using var client = Client(_ => new HttpResponseMessage(status) { Content = new StringContent("{}") });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Docs.TombstoneAsync(client, Document));

        await Assert.That(error!.StatusCode).IsEqualTo(status);
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        new(new Stand(answer)) { BaseAddress = new Uri("http://region.test") };

    // Match the server's positive Retry-After for a refusal before execution.
    private static HttpResponseMessage Refused(string code)
    {
        var response = Unavailable($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"m\"}}}}");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
        return response;
    }

    private static HttpResponseMessage Unavailable(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(body) };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static HttpResponseMessage Acknowledged(string durability) =>
        new(HttpStatusCode.Created) { Content = new StringContent($"{{\"durability\":\"{durability}\"}}", Encoding.UTF8, "application/json") };

    // The region's stand-in: answers each request it receives, without a network.
    private sealed class Stand(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
