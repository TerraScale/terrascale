using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>
/// The SDK's side of TerraBase's regional contract (monorepo docs/terrabase/region-identity.md), against
/// a fake OCI HTTP endpoint: TerraBase answers <c>X-TerraBase-Region</c>; a request for a database another
/// region serves answers <c>421 wrong_region</c> without SDK redirects or retries; writes are
/// leaderless; TerraBase refuses <c>Idempotency-Key</c>, so a write must never be sent twice and only conditional writes
/// guard against duplicates. A client with <see cref="TerraBaseDocumentsClientOptions.Region"/> targets that region's
/// host and pins every request to it; one without sends no routing header at all.
/// </summary>
public sealed class TerraBaseRegionalContractTests
{
    private const string WriteResult = """{"id":"a","version":"tbv1-v1","offset":"1","durability":"local_durable"}""";

    private static readonly TerraBaseDocumentsClientOptions Options = new()
    {
        BaseAddress = new Uri("https://api.terrascale.test/"),
        ApiKey = "tb_test_key",
    };

    [Test]
    public async Task A_wrong_region_ts_replay_answer_preserves_placement_without_redirect_or_retry()
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
        var database = new TerraBaseDocumentsClient(http, Options).Database("project", "app");

        var error = await Assert.ThrowsAsync<TerraBaseWrongRegionException>(() => database.PutDocumentAsync("items", "01JA00000000000000000000D1", Body()));

        await Assert.That(sent.Count).IsEqualTo(1);
        await Assert.That(sent[0].Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(sent[0].RequestUri).IsEqualTo(new Uri("https://api.terrascale.test/v1/projects/project/databases/app/collections/items/documents/by-key?id=01JA00000000000000000000D1"));
        await AssertOnlyExpectedHeadersAsync(sent[0]);
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(error.ErrorCode).IsEqualTo("wrong_region");
        await Assert.That(error.Region).IsEqualTo("sjc");
        await Assert.That(error.HomeRegion).IsEqualTo("gru");
        await Assert.That(error.Regions).IsEquivalentTo(["gru"]);
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    [Arguments(503, "storage_unavailable", typeof(TerraBaseUnavailableException))]
    // An HTTP gateway with no healthy upstream.
    [Arguments(502, null, typeof(TerraBaseException))]
    [Arguments(0, null, typeof(HttpRequestException))]
    public async Task An_unavailable_or_unreachable_region_fails_the_write_without_a_retry(int status, string? code, Type type)
    {
        var sent = 0;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            Interlocked.Increment(ref sent);
            return status == 0
                ? throw new HttpRequestException("connection refused", null, null)
                : Task.FromResult(code is null ? new HttpResponseMessage((HttpStatusCode)status) : Error((HttpStatusCode)status, code, "unavailable"));
        });
        using var http = new HttpClient(handler);
        var database = new TerraBaseDocumentsClient(http, Options).Database("project", "app");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => database.PutDocumentAsync("items", "01JA00000000000000000000D1", Body()));

        await Assert.That(sent).IsEqualTo(1);
        await Assert.That(error!.GetType()).IsEqualTo(type);
        if (error is TerraBaseException terraBase)
        {
            await Assert.That((int)terraBase.StatusCode!).IsEqualTo(status);
            await Assert.That(terraBase.ErrorCode).IsEqualTo(code);
        }
    }

    [Test]
    public async Task Conditional_writes_are_the_duplicate_guard_when_a_caller_repeats_a_write_in_another_region()
    {
        // Two regions of one leaderless database: the first write landed in gru, but its answer was lost.
        var stored = new HashSet<string>(StringComparer.Ordinal);
        var sent = new List<(string Host, string? IfNoneMatch, string? IfMatch)>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent.Add((request.RequestUri!.Host, request.Headers.IfNoneMatch.ToString() is { Length: > 0 } none ? none : null,
                request.Headers.IfMatch.ToString() is { Length: > 0 } match ? match : null));
            var key = request.RequestUri.Query;
            if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == "*") && !stored.Add(key))
            {
                return Task.FromResult(Error(HttpStatusCode.PreconditionFailed, "precondition_failed", "the document exists"));
            }

            stored.Add(key);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(WriteResult, Encoding.UTF8, "application/json") });
        });
        using var http = new HttpClient(handler);
        var regions = new Uri[] { new("https://gru.example.test/"), new("https://sjc.example.test/") };
        var next = 0;
        var resolver = new Resolver(() => regions[next++ % regions.Length]);
        var database = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { EndpointResolver = resolver, ApiKey = "tb_test_key" })
            .Database("project", "app");

        var created = await database.PutDocumentAsync("items", "01JA00000000000000000000D1", Body(), TerraBasePrecondition.Absent);
        var repeated = await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(
            () => database.PutDocumentAsync("items", "01JA00000000000000000000D1", Body(), TerraBasePrecondition.Absent));
        // A version read in one region satisfies If-Match in another (cross-region.md, version tokens).
        await database.PutDocumentAsync("items", "01JA00000000000000000000D1", Body(), TerraBasePrecondition.Matches(created.Version));

        await Assert.That(created.Created).IsTrue();
        await Assert.That(repeated!.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That(sent).IsEquivalentTo(new[]
        {
            ("gru.example.test", (string?)"*", (string?)null),
            ("sjc.example.test", "*", null),
            ("gru.example.test", null, "\"tbv1-v1\""),
        });
    }

    [Test]
    public async Task The_endpoint_resolver_alone_picks_the_region_and_answering_regions_are_not_pinned()
    {
        var sent = new List<HttpRequestMessage>();
        var answering = new Queue<string>(["gru", "sjc", "fra"]);
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent.Add(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(WriteResult, Encoding.UTF8, "application/json") };
            response.Headers.Add("X-TerraBase-Region", answering.Dequeue());
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var resolver = new TerraBaseClientOptionsTests.CountingResolver(new Uri("https://api.terrascale.test/"));
        var database = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { EndpointResolver = resolver, ApiKey = "tb_test_key" })
            .Database("project", "app");

        for (var i = 0; i < 3; i++)
        {
            var written = await database.PutDocumentAsync("items", "01JA00000000000000000000D1", Body());
            await Assert.That(written.Version).IsEqualTo("tbv1-v1");
        }

        // Each request resolves its origin again; whichever region answered, nothing pins the next one.
        await Assert.That(resolver.Calls).IsEqualTo(3);
        await Assert.That(sent.Count).IsEqualTo(3);
        foreach (var request in sent)
        {
            await Assert.That(request.RequestUri!.Host).IsEqualTo("api.terrascale.test");
            await AssertOnlyExpectedHeadersAsync(request);
        }
    }

    [Test]
    public async Task A_region_option_targets_the_public_origin_and_pins_every_request_to_it()
    {
        var sent = new List<HttpRequestMessage>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(WriteResult, Encoding.UTF8, "application/json") });
        });
        using var http = new HttpClient(handler);
        var regional = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { Region = "fra", ApiKey = "tb_test_key" }).Database("project", "app");
        // An explicit origin keeps its host; the pin still holds the request to the region.
        var explicitOrigin = new TerraBaseDocumentsClient(http, Options with { Region = "sjc" }).Database("project", "app");

        await regional.PutDocumentAsync("items", "01JA00000000000000000000D1", Body());
        await explicitOrigin.PutDocumentAsync("items", "01JA00000000000000000000D1", Body());

        await Assert.That(sent.Select(request => request.RequestUri!.GetLeftPart(UriPartial.Authority))).IsEquivalentTo(["https://api.terrascale.tech", "https://api.terrascale.test"]);
        await Assert.That(sent.Select(request => string.Join(',', request.Headers.GetValues(TerraBaseRegion.PinHeader)))).IsEquivalentTo(["fra", "sjc"]);
        foreach (var request in sent)
        {
            await AssertOnlyExpectedHeadersAsync(request, pinned: true);
        }
    }

    [Test]
    public async Task A_pin_another_region_rejects_surfaces_as_wrong_region_without_placement()
    {
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            // TerraBase's answer to a mismatched pin: 421 naming only itself.
            var response = Error(HttpStatusCode.MisdirectedRequest, "misdirected_request", "the region pin names another region");
            response.Headers.Add("X-TerraBase-Region", "gru");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var database = new TerraBaseDocumentsClient(http, Options with { Region = "sjc" }).Database("project", "app");

        var error = await Assert.ThrowsAsync<TerraBaseWrongRegionException>(() => database.GetDocumentAsync("items", "01JA00000000000000000000D1"));

        await Assert.That(error!.Region).IsEqualTo("gru");
        await Assert.That(error.HomeRegion).IsNull();
        await Assert.That(error.Regions).IsEmpty();
    }

    [Test]
    public async Task Malformed_region_headers_are_ignored_and_other_errors_name_the_answering_region()
    {
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            var response = request.Method == HttpMethod.Get
                ? Error(HttpStatusCode.MisdirectedRequest, "wrong_region", "the database is served in gru")
                : Error(HttpStatusCode.ServiceUnavailable, "storage_unavailable", "unavailable");
            response.Headers.Add("X-TerraBase-Region", request.Method == HttpMethod.Get ? "SJC" : "fra");
            response.Headers.Add("X-TerraBase-Database-Home", "GRU");
            response.Headers.Add("X-TerraBase-Database-Regions", "gru, SJC, fra-");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var database = new TerraBaseDocumentsClient(http, Options).Database("project", "app");

        var wrong = await Assert.ThrowsAsync<TerraBaseWrongRegionException>(() => database.GetDocumentAsync("items", "01JA00000000000000000000D1"));
        var unavailable = await Assert.ThrowsAsync<TerraBaseUnavailableException>(() => database.PutDocumentAsync("items", "01JA00000000000000000000D1", Body()));

        await Assert.That(wrong!.Region).IsNull();
        await Assert.That(wrong.HomeRegion).IsNull();
        await Assert.That(wrong.Regions).IsEquivalentTo(["gru"]);
        await Assert.That(unavailable!.Region).IsEqualTo("fra");
    }

    [Test]
    [Arguments("GRU")]
    [Arguments("Gru")]
    [Arguments("")]
    [Arguments("gru-")]
    [Arguments("1gru")]
    [Arguments("g ru")]
    [Arguments("gru.evil.example")]
    public async Task A_region_that_is_not_a_lowercase_region_id_is_refused_at_construction(string region)
    {
        using var http = new HttpClient(new TerraBaseTestHandler((_, _) => throw new InvalidOperationException("nothing is sent")));

        await Assert.That(() => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { Region = region, ApiKey = "tb_test_key" }))
            .Throws<ArgumentException>();
    }

    private static JsonElement Body() => JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["from"] = "sdk" });

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
