using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>
/// The SDK's side of TerraBase's regional contract (monorepo docs/terrabase/region-identity.md), against
/// a fake OCI HTTP endpoint: TerraBase answers <c>X-TerraBase-Region</c>; a request for a database another
/// region serves answers <c>421 wrong_region</c> without SDK redirects or retries; TerraBase refuses
/// <c>Idempotency-Key</c>, so a write must never be sent twice. A client with <see cref="TerraBaseClientOptions.Region"/> targets that region's
/// host and pins every request to it; one without sends no routing header at all.
/// </summary>
public sealed class TerraBaseRegionalContractTests
{
    private const string Branch = """{"id":"01JA0000000000000000000005","projectId":"01JA0000000000000000000001","name":"copy","branchedFrom":null,"createdAt":"2026-10-12T00:00:00+00:00","region":"gru","homeRegion":"gru"}""";

    private static readonly TerraBaseClientOptions Options = new()
    {
        BaseAddress = new Uri("https://api.terrascale.test/"),
        ApiKey = "tb_test_key",
    };

    [Test]
    public async Task A_wrong_region_ts_replay_answer_preserves_the_home_region_without_redirect_or_retry()
    {
        var sent = new List<HttpRequestMessage>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent.Add(request);
            // A request to sjc for a database served in gru only.
            var response = Error(HttpStatusCode.MisdirectedRequest, "wrong_region", "the database is served in gru");
            response.Headers.Add("X-TerraBase-Region", "sjc");
            response.Headers.Add("X-TerraBase-Database-Home", "gru");
            response.Headers.Add("X-TerraBase-Database-Regions", "gru");
            response.Headers.Add("ts-replay", "region=gru"); // Producer #353's wrong-region response.
            // Misleading hints cannot make a write repeat or disclose its body and credential to another origin.
            response.Headers.Location = new Uri("https://gru.api.terrascale.test/");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var database = new TerraBaseClient(http, Options).Database("project", "app");

        var error = await Assert.ThrowsAsync<TerraBaseWrongRegionException>(() => database.BranchAsync("copy"));

        await Assert.That(sent.Count).IsEqualTo(1);
        await Assert.That(sent[0].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(sent[0].RequestUri).IsEqualTo(new Uri("https://api.terrascale.test/v1/projects/project/databases/app/branches"));
        await AssertOnlyExpectedHeadersAsync(sent[0]);
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(error.ErrorCode).IsEqualTo("wrong_region");
        await Assert.That(error.Region).IsEqualTo("sjc");
        await Assert.That(error.HomeRegion).IsEqualTo("gru");
        await Assert.That(error.Regions).IsEquivalentTo(["gru"]);
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task A_region_option_targets_the_public_origin_and_pins_every_request_to_it()
    {
        var sent = new List<HttpRequestMessage>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Branch, Encoding.UTF8, "application/json") });
        });
        using var http = new HttpClient(handler);
        var regional = new TerraBaseClient(http, new TerraBaseClientOptions { Region = "fra", ApiKey = "tb_test_key" }).Database("project", "app");
        // An explicit origin keeps its host; the pin still holds the request to the region.
        var explicitOrigin = new TerraBaseClient(http, Options with { Region = "sjc" }).Database("project", "app");

        await regional.BranchAsync("copy");
        await explicitOrigin.BranchAsync("copy");

        await Assert.That(sent.Select(request => request.RequestUri!.GetLeftPart(UriPartial.Authority))).IsEquivalentTo(["https://api.terrascale.tech", "https://api.terrascale.test"]);
        await Assert.That(sent.Select(request => string.Join(',', request.Headers.GetValues(TerraBaseRegion.PinHeader)))).IsEquivalentTo(["fra", "sjc"]);
        foreach (var request in sent)
        {
            await AssertOnlyExpectedHeadersAsync(request, pinned: true);
        }
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) => new(status)
    {
        Content = new StringContent($$$"""{"error":{"code":"{{{code}}}","message":"{{{message}}}"}}""", Encoding.UTF8, "application/json"),
    };

    private static async Task AssertOnlyExpectedHeadersAsync(HttpRequestMessage request, bool pinned = false)
    {
        // A complete allowlist also rejects unexpected routing, acting-user and idempotency headers.
        string[] expected = pinned ? ["Authorization", "Accept", "X-TerraScale-Region-Pin"] : ["Authorization", "Accept"];
        await Assert.That(request.Headers.Select(header => header.Key)).IsEquivalentTo(expected);
        await Assert.That(request.Headers.Authorization!.Scheme).IsEqualTo("Bearer");
        await Assert.That(request.Headers.Authorization.Parameter).IsEqualTo("tb_test_key");
    }

    private sealed class Resolver(Func<Uri> next) : ITerraBaseEndpointResolver
    {
        public ValueTask<Uri> ResolveAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(next());
    }
}
