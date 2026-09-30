using System.Net;
using System.Text;

namespace TerraScale.DotNet.Tests;

/// <summary>
/// ADR 0050 routing semantics: discovery at Atlas first, direct regional
/// connections after, and the Atlas API proxy when discovery is unavailable.
/// </summary>
public sealed class TerraScaleDiscoveryRoutingTests
{
    private const string ProductOrigin = "https://api.terrascale.tech";

    [Test]
    public async Task DefaultOptionsDiscoverAndPinTheTopRankedRegion()
    {
        var transport = new ScriptedTransport();
        using var httpClient = new HttpClient(transport);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions { ApiKey = "test-key" });
        _ = await client.Documents.GetAsync("default-pin", "items", "item-1");
        _ = await client.Documents.GetAsync("default-pin", "items", "item-1");

        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://atlas.terrascale.tech/api/v1/discovery?database=default-pin&branch=main",
            "GET https://fra.api.terrascale.tech/api/v1/databases/default-pin/collections/items/documents/item-1",
            "GET https://fra.api.terrascale.tech/api/v1/databases/default-pin/collections/items/documents/item-1",
        ]);
    }

    [Test]
    public async Task ExpiredDocumentRefreshesAndPinsTheNewTopRegion()
    {
        var transport = new ScriptedTransport();
        using var httpClient = new HttpClient(transport);
        long now = 0;
        var cache = new TerraScaleRoutingCache { NowMs = () => now };
        var router = new TerraScaleDiscoveryRouter(httpClient, new Uri("https://atlas-ttl.test"),
            TimeSpan.FromSeconds(3), null, null, null, cache, null);
        var first = await router.ResolveBaseAsync("orders-ttl", false, CancellationToken.None);
        transport.DiscoveryBody = transport.DiscoveryBody.Replace("\"refreshSeconds\":300", "\"refreshSeconds\":1")
            .Replace("FRA", "SFO").Replace("fra.api.", "sfo.api.");
        now = 299_999;
        await Assert.That((await router.ResolveBaseAsync("orders-ttl", false, CancellationToken.None))!.Region).IsEqualTo(first!.Region);
        now = 300_000;
        await Assert.That((await router.ResolveBaseAsync("orders-ttl", false, CancellationToken.None))!.Region).IsEqualTo("SFO");
        now = 301_000;
        _ = await router.ResolveBaseAsync("orders-ttl", false, CancellationToken.None);
        await Assert.That(transport.Requests.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments(500)]
    [Arguments(503)]
    [Arguments(599)]
    public async Task ServerErrorsFailOverAndRepinWithoutARetryBudget(int status)
    {
        var transport = new ScriptedTransport { FailRegionalRequests = true, RegionalFailureStatus = (HttpStatusCode)status };
        var client = CreateClient(transport, new Uri($"https://atlas-server-{status}.test"));
        _ = await client.Documents.GetAsync("orders-server", "items", "item-1");
        _ = await client.Documents.GetAsync("orders-server", "items", "item-1");
        await Assert.That(transport.Requests.Count).IsEqualTo(4);
        await Assert.That(transport.Requests[^1]).IsEqualTo("GET https://gru.api.terrascale.tech/api/v1/databases/orders-server/collections/items/documents/item-1");
    }

    [Test]
    public async Task TransportErrorsFailOverAndRepinWithoutARetryBudget()
    {
        var transport = new ScriptedTransport { ThrowRegionalRequests = true };
        var client = CreateClient(transport, new Uri("https://atlas-transport.test"));
        _ = await client.Documents.GetAsync("orders-transport", "items", "item-1");
        _ = await client.Documents.GetAsync("orders-transport", "items", "item-1");
        await Assert.That(transport.Requests.Count).IsEqualTo(4);
        await Assert.That(transport.Requests[^1]).IsEqualTo("GET https://gru.api.terrascale.tech/api/v1/databases/orders-transport/collections/items/documents/item-1");
    }

    [Test]
    public async Task TransportTimeoutFailsOverWithoutCancellingTheCaller()
    {
        var transport = new ScriptedTransport { TimeoutRegionalRequests = true };
        var client = CreateClient(transport, new Uri("https://atlas-timeout.test"));
        _ = await client.Documents.GetAsync("orders-timeout", "items", "item-1");
        _ = await client.Documents.GetAsync("orders-timeout", "items", "item-1");
        await Assert.That(transport.Requests.Count).IsEqualTo(4);
        await Assert.That(transport.Requests[^1]).IsEqualTo("GET https://gru.api.terrascale.tech/api/v1/databases/orders-timeout/collections/items/documents/item-1");
    }

    [Test]
    public async Task MisdirectionWithoutPlacementFailsOverAndRepins()
    {
        var transport = new ScriptedTransport { MisdirectDataRequests = true, OmitPlacement = true };
        var client = CreateClient(transport, new Uri("https://atlas-no-placement.test"));
        _ = await client.Documents.GetAsync("orders-no-placement", "items", "item-1");
        _ = await client.Documents.GetAsync("orders-no-placement", "items", "item-1");
        await Assert.That(transport.Requests.Count).IsEqualTo(4);
        await Assert.That(transport.Requests[^1]).IsEqualTo("GET https://gru.api.terrascale.tech/api/v1/databases/orders-no-placement/collections/items/documents/item-1");
    }

    [Test]
    public async Task DiscoveryTransportFailureUsesTheProxyAndBacksOff()
    {
        var transport = new ScriptedTransport { ThrowDiscovery = true };
        var client = CreateClient(transport, new Uri("https://atlas-offline.test"));
        _ = await client.Documents.GetAsync("orders-offline", "items", "item-1");
        _ = await client.Documents.GetAsync("orders-offline", "items", "item-1");
        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://atlas-offline.test/api/v1/discovery?database=orders-offline&branch=main",
            "GET https://api.terrascale.tech/api/v1/databases/orders-offline/collections/items/documents/item-1",
            "GET https://api.terrascale.tech/api/v1/databases/orders-offline/collections/items/documents/item-1",
        ]);
    }

    [Test]
    public async Task FailoverStopsWhenEveryRankedRegionFails()
    {
        var transport = new ScriptedTransport { FailAllRegionalRequests = true };
        var client = CreateClient(transport, new Uri("https://atlas-exhausted.test"));
        var error = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync("orders-exhausted", "items", "item-1"));
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(transport.Requests.Count).IsEqualTo(3);
    }

    [Test]
    public async Task UnkeyedMutationIsSentOnceAndLaterReadsUseTheNextRegion()
    {
        var transport = new ScriptedTransport { FailRegionalRequests = true };
        var client = CreateClient(transport, new Uri("https://atlas-mutation.test"));
        _ = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.UpsertAsync("orders-mutation", "items", "item-1", new { status = "paid" }));
        _ = await client.Documents.GetAsync("orders-mutation", "items", "item-1");
        await Assert.That(transport.Requests.Count(request => request.StartsWith("PUT ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(transport.Requests[^1]).IsEqualTo("GET https://gru.api.terrascale.tech/api/v1/databases/orders-mutation/collections/items/documents/item-1");
    }

    [Test]
    public async Task DatabaseScopedRequestsDiscoverOnceThenConnectDirectly()
    {
        var transport = new ScriptedTransport();
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-once.test"));

        _ = await client.Documents.GetAsync("orders-once", "items", "item-1");
        _ = await client.Documents.GetAsync("orders-once", "items", "item-1");

        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://atlas-once.test/api/v1/discovery?database=orders-once&branch=main",
            "GET https://fra.api.terrascale.tech/api/v1/databases/orders-once/collections/items/documents/item-1",
            "GET https://fra.api.terrascale.tech/api/v1/databases/orders-once/collections/items/documents/item-1",
        ]);
    }

    [Test]
    public async Task DiscoveryFailureFallsBackToTheAtlasProxy()
    {
        var transport = new ScriptedTransport { DiscoveryStatus = HttpStatusCode.ServiceUnavailable };
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-fallback.test"));

        _ = await client.Documents.GetAsync("orders-fallback", "items", "item-1");

        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://atlas-fallback.test/api/v1/discovery?database=orders-fallback&branch=main",
            "GET https://api.terrascale.tech/api/v1/databases/orders-fallback/collections/items/documents/item-1",
        ]);
        await Assert.That(transport.Requests.Any(request => request.Contains(ProductOrigin, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task CatalogIsFetchedOnceAndReusedAcrossRequests()
    {
        var transport = new ScriptedTransport { DiscoveryStatus = HttpStatusCode.ServiceUnavailable };
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-reuse.test"));

        _ = await client.Databases.ListAsync();
        _ = await client.Databases.ListAsync();

        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://atlas-reuse.test/api/v1/regions",
            "GET https://gru.api.terrascale.tech/api/v1/databases",
            "GET https://gru.api.terrascale.tech/api/v1/databases",
        ]);
    }

    [Test]
    public async Task RegionCatalogRouteTargetsTheDiscoveryOrigin()
    {
        var transport = new ScriptedTransport();
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-catalog.test"));

        var catalog = await client.Regions.GetAsync();

        await Assert.That(transport.Requests).IsEquivalentTo(["GET https://atlas-catalog.test/api/v1/regions"]);
        await Assert.That(catalog.Regions.Select(region => region.Code)).IsEquivalentTo(["GRU", "FRA"]);
        await Assert.That(catalog.Hosts["GRU"].ToString()).IsEqualTo("https://gru.api.terrascale.tech/");
        await Assert.That(transport.Authorizations.Count).IsEqualTo(1);
        await Assert.That(transport.Authorizations[0]).IsNull();
    }

    [Test]
    public async Task MemberHealthRouteTargetsTheConfiguredOriginOnlyWhenRoutingIsInactive()
    {
        var transport = new ScriptedTransport();
        using var httpClient = new HttpClient(transport);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            DiscoveryBaseAddress = new Uri("https://unused-discovery.test"),
            ApiKey = "test-key",
        });

        _ = await client.Regions.HealthAsync();

        await Assert.That(transport.Requests).IsEquivalentTo(["GET https://api.example.invalid/api/v1/health"]);
    }

    [Test]
    public async Task MisdirectedResponseAdoptsPlacementAndRepairsOnce()
    {
        var transport = new ScriptedTransport { MisdirectDataRequests = true };
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-421.test"));

        var document = await client.Documents.GetAsync("orders-repair", "items", "item-1");

        await Assert.That(document.Id).IsEqualTo("item-1");
        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://atlas-421.test/api/v1/discovery?database=orders-repair&branch=main",
            "GET https://fra.api.terrascale.tech/api/v1/databases/orders-repair/collections/items/documents/item-1",
            "GET https://gru.api.terrascale.tech/api/v1/databases/orders-repair/collections/items/documents/item-1",
        ]);

        // The adopted placement is sticky for later requests in the scope.
        transport.Requests.Clear();
        transport.MisdirectDataRequests = false;
        _ = await client.Documents.GetAsync("orders-repair", "items", "item-1");
        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://gru.api.terrascale.tech/api/v1/databases/orders-repair/collections/items/documents/item-1",
        ]);
    }

    [Test]
    public async Task RepeatedMisdirectionStopsAfterOneRepair()
    {
        var transport = new ScriptedTransport { MisdirectDataRequests = true, AlwaysMisdirect = true };
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-421b.test"));

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync("orders-repeat", "items", "item-1"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(transport.Requests.Count).IsEqualTo(3);
    }

    [Test]
    public async Task WriteSelectsTheFirstWriteCapableEndpoint()
    {
        var transport = new ScriptedTransport
        {
            DiscoveryBody = """{"v":1,"database":"PLACEHOLDER","branch":"main","revision":7,"updated":1,"residency":null,"placement":"known","refreshSeconds":300,"endpoints":[{"region":"FRA","url":"https://fra.api.terrascale.tech","priority":1,"capabilities":["read","reactive"]},{"region":"GRU","url":"https://gru.api.terrascale.tech","priority":2,"capabilities":["read","write","sql","reactive"]}]}""",
        };
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-write.test"));

        _ = await client.Documents.GetAsync("orders-write", "items", "item-1");
        _ = await client.Documents.UpsertAsync("orders-write", "items", "item-1", new { status = "paid" });

        await Assert.That(transport.Requests).Contains("GET https://fra.api.terrascale.tech/api/v1/databases/orders-write/collections/items/documents/item-1");
        await Assert.That(transport.Requests).Contains("PUT https://gru.api.terrascale.tech/api/v1/databases/orders-write/collections/items/documents/item-1");
    }

    [Test]
    public async Task RetryableFailureDemotesTheRegionToTheNextEndpoint()
    {
        var transport = new ScriptedTransport { FailRegionalRequests = true };
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-demote.test"));

        _ = await client.Documents.GetAsync("orders-demote", "items", "item-1");
        _ = await client.Documents.GetAsync("orders-demote", "items", "item-1");

        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://atlas-demote.test/api/v1/discovery?database=orders-demote&branch=main",
            "GET https://fra.api.terrascale.tech/api/v1/databases/orders-demote/collections/items/documents/item-1",
            "GET https://gru.api.terrascale.tech/api/v1/databases/orders-demote/collections/items/documents/item-1",
            "GET https://gru.api.terrascale.tech/api/v1/databases/orders-demote/collections/items/documents/item-1",
        ]);
    }

    [Test]
    public async Task MalformedDocumentsFallBackToTheProxyAndNeverContactEvilHosts()
    {
        var transport = new ScriptedTransport
        {
            DiscoveryBody = """{"v":1,"database":"PLACEHOLDER","branch":"main","revision":7,"updated":1,"residency":null,"placement":"known","refreshSeconds":300,"endpoints":[{"region":"FRA","url":"https://evil.example.com","priority":1,"capabilities":["read","write"]}]}""",
        };
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-evil.test"));

        _ = await client.Documents.GetAsync("orders-evil", "items", "item-1");

        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://atlas-evil.test/api/v1/discovery?database=orders-evil&branch=main",
            "GET https://api.terrascale.tech/api/v1/databases/orders-evil/collections/items/documents/item-1",
        ]);
    }

    [Test]
    public async Task IdentityEchoMismatchRejectsTheDocument()
    {
        var transport = new ScriptedTransport
        {
            DiscoveryBody = """{"v":1,"database":"some-other-database","branch":"main","revision":7,"updated":1,"residency":null,"placement":"known","refreshSeconds":300,"endpoints":[{"region":"FRA","url":"https://fra.api.terrascale.tech","priority":1,"capabilities":["read","write"]}]}""",
        };
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-echo.test"));

        _ = await client.Documents.GetAsync("orders-echo", "items", "item-1");

        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://atlas-echo.test/api/v1/discovery?database=orders-echo&branch=main",
            "GET https://api.terrascale.tech/api/v1/databases/orders-echo/collections/items/documents/item-1",
        ]);
    }

    [Test]
    public async Task CustomOriginsKeepDirectRoutingWithoutDiscovery()
    {
        var transport = new ScriptedTransport();
        using var httpClient = new HttpClient(transport);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            DiscoveryBaseAddress = new Uri("https://unused-discovery.test"),
            ApiKey = "test-key",
        });

        _ = await client.Documents.GetAsync("orders-custom", "items", "item-1");

        await Assert.That(transport.Requests).IsEquivalentTo([
            "GET https://api.example.invalid/api/v1/databases/orders-custom/collections/items/documents/item-1",
        ]);
    }

    [Test]
    public async Task DiscoverySendsMeasuredRegionRttAfterSuccessfulRegionalRequests()
    {
        var transport = new ScriptedTransport { DelayRegionalRequests = true };
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-rtt.test"));

        _ = await client.Documents.GetAsync("orders-rtt", "items", "item-1");
        _ = await client.Documents.GetAsync("orders-rtt-b", "items", "item-1");

        var firstDiscovery = transport.RegionRttHeaderFor("GET https://atlas-rtt.test/api/v1/discovery?database=orders-rtt&branch=main");
        var secondDiscovery = transport.RegionRttHeaderFor("GET https://atlas-rtt.test/api/v1/discovery?database=orders-rtt-b&branch=main");
        await Assert.That(firstDiscovery).IsNull();
        await Assert.That(secondDiscovery).IsNotNull();
        await Assert.That(secondDiscovery!.StartsWith("FRA=", StringComparison.Ordinal)).IsTrue();
        await Assert.That(int.Parse(secondDiscovery["FRA=".Length..], System.Globalization.CultureInfo.InvariantCulture)).IsGreaterThanOrEqualTo(20);

        // Measured latency travels only to Atlas, never to a member.
        foreach (var (request, header) in transport.Requests.Zip(transport.RegionRttHeaders, (request, header) => (request, header)))
        {
            if (!request.Contains("atlas-rtt.test", StringComparison.Ordinal))
            {
                await Assert.That(header).IsNull();
            }
        }
    }

    [Test]
    public async Task CorrectiveRediscoverySendsTheCachedRevision()
    {
        var transport = new ScriptedTransport();
        var client = CreateClient(transport, atlasOrigin: new Uri("https://atlas-rev.test"));

        _ = await client.Documents.GetAsync("orders-rev", "items", "item-1");
        client.DiscoveryRouter!.ReportRejectedRoute("orders-rev");
        _ = await client.Documents.GetAsync("orders-rev", "items", "item-1");

        await Assert.That(transport.Requests).Contains(
            "GET https://atlas-rev.test/api/v1/discovery?database=orders-rev&branch=main");
        await Assert.That(transport.Requests).Contains(
            "GET https://atlas-rev.test/api/v1/discovery?database=orders-rev&branch=main&rev=7&stale=1");
    }

    private static TerraScaleClient CreateClient(
        ScriptedTransport transport,
        Uri atlasOrigin,
        Action<TerraScaleClientOptions>? configure = null)
    {
        var httpClient = new HttpClient(transport);
        var options = new TerraScaleClientOptions
        {
            // The default product origin: Atlas routing activates through the
            // discovery override exactly as it does implicitly in production.
            DiscoveryBaseAddress = atlasOrigin,
            ApiKey = "test-key",
        };
        configure?.Invoke(options);
        return new TerraScaleClient(httpClient, options);
    }

    private sealed class ScriptedTransport : HttpMessageHandler
    {
        private const string DefaultDiscoveryBody =
            """{"v":1,"database":"PLACEHOLDER","branch":"main","revision":7,"updated":1,"residency":null,"placement":"known","refreshSeconds":300,"endpoints":[{"region":"FRA","url":"https://fra.api.terrascale.tech","priority":1,"capabilities":["read","write","sql","reactive"]},{"region":"GRU","url":"https://gru.api.terrascale.tech","priority":2,"capabilities":["read","write","sql","reactive"]}]}""";

        private const string CatalogBody =
            """{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":-23.5475,"longitude":-46.6361,"available":true},{"code":"FRA","name":"Frankfurt","country":"DE","latitude":50.1109,"longitude":8.6821,"available":true}],"hosts":{"GRU":"gru.api.terrascale.tech","FRA":"fra.api.terrascale.tech"}}""";

        public HttpStatusCode DiscoveryStatus { get; set; } = HttpStatusCode.OK;
        public string DiscoveryBody { get; set; } = DefaultDiscoveryBody;
        public bool MisdirectDataRequests { get; set; }
        public bool AlwaysMisdirect { get; set; }
        public bool FailRegionalRequests { get; set; }
        public bool FailAllRegionalRequests { get; set; }
        public bool ThrowRegionalRequests { get; set; }
        public bool TimeoutRegionalRequests { get; set; }
        public bool ThrowDiscovery { get; set; }
        public bool OmitPlacement { get; set; }
        public HttpStatusCode RegionalFailureStatus { get; set; } = HttpStatusCode.ServiceUnavailable;
        public bool DelayRegionalRequests { get; set; }
        public List<string> Requests { get; } = [];
        public List<string?> RegionRttHeaders { get; } = [];
        public List<string?> Authorizations { get; } = [];

        public string? RegionRttHeaderFor(string request) => RegionRttHeaders[Requests.IndexOf(request)];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("The request URI was not resolved.");
            Requests.Add($"{request.Method.Method.ToUpperInvariant()} {DescribeUrl(uri)}");
            RegionRttHeaders.Add(request.Headers.TryGetValues("X-TerraScale-Region-Rtt", out var values)
                ? string.Join(",", values)
                : null);
            Authorizations.Add(request.Headers.Authorization?.ToString());

            if (uri.AbsolutePath == "/api/v1/discovery")
            {
                if (ThrowDiscovery) throw new HttpRequestException("Atlas offline");
                var database = Uri.UnescapeDataString(
                    uri.Query.TrimStart('?').Split('&').First(value => value.StartsWith("database=", StringComparison.Ordinal))["database=".Length..]);
                return Respond(DiscoveryStatus, DiscoveryBody.Replace("PLACEHOLDER", database));
            }

            if (uri.AbsolutePath == "/api/v1/regions")
            {
                return Respond(HttpStatusCode.OK, CatalogBody);
            }

            if (DelayRegionalRequests)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            var dataRequestCount = Requests.Count(value =>
                !value.Contains("/api/v1/discovery", StringComparison.Ordinal) &&
                !value.Contains("/api/v1/regions", StringComparison.Ordinal));
            if (MisdirectDataRequests && (AlwaysMisdirect || dataRequestCount == 1))
            {
                var misdirected = Respond(HttpStatusCode.MisdirectedRequest, "{\"error\":{\"code\":\"misdirected_request\",\"message\":\"Wrong region\"}}");
                if (!OmitPlacement) misdirected.Headers.TryAddWithoutValidation("X-TerraScale-Placement", "GRU,SFO");
                return misdirected;
            }

            if (ThrowRegionalRequests && uri.Host == "fra.api.terrascale.tech")
            {
                throw new HttpRequestException("Region offline");
            }

            if (TimeoutRegionalRequests && uri.Host == "fra.api.terrascale.tech")
            {
                throw new TaskCanceledException("Regional transport timed out");
            }

            if ((FailRegionalRequests && uri.Host == "fra.api.terrascale.tech") ||
                (FailAllRegionalRequests && uri.Host.EndsWith(".api.terrascale.tech", StringComparison.Ordinal)))
            {
                return Respond(RegionalFailureStatus, "{\"error\":{\"code\":\"unavailable\",\"message\":\"region down\"}}");
            }

            var body = uri.AbsolutePath == "/api/v1/health"
                ? "{\"region\":\"GRU\",\"healthy\":true,\"version\":\"test\"}"
                : request.Method != HttpMethod.Get
                    ? "{\"revision\":\"r1\"}"
                    : uri.AbsolutePath == "/api/v1/databases" ? "{\"databases\":[]}" : "{\"status\":\"paid\"}";
            var success = Respond(HttpStatusCode.OK, body);
            success.Headers.TryAddWithoutValidation("ETag", "\"r1\"");
            return success;
        }

        private static string DescribeUrl(Uri uri)
        {
            var authority = uri.Host + (uri.Port is 80 or 443 ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return $"{uri.Scheme}://{authority}{uri.AbsolutePath}{uri.Query}";
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string body)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("X-Request-Id", "test-request");
            return response;
        }
    }
}
