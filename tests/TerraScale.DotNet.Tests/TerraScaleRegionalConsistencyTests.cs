using System.Net;
using System.Text;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleRegionalConsistencyTests
{
    [Test]
    public async Task InvalidPreferredRegionsFailBeforeAnyRequest()
    {
        using var handler = new RecordingHandler(DocumentJson());
        using var httpClient = new HttpClient(handler);

        var exception = Assert.Throws<ArgumentException>(() => new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token",
                PreferredRegion = "-invalid"
            }));

        await Assert.That(exception!.ParamName).IsEqualTo("PreferredRegion");
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task UnknownRegionalOptionsFailBeforeAnyRequest()
    {
        using var handler = new RecordingHandler(DocumentJson());
        using var httpClient = new HttpClient(handler);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token",
                RegionAffinity = (TerraScaleRegionAffinity)99
            }));

        await Assert.That(exception!.ParamName).IsEqualTo("RegionAffinity");
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task DefaultAndStrictClientsUseTheContractedPublicHosts()
    {
        using var globalHandler = new RecordingHandler(DocumentJson());
        using var globalHttpClient = new HttpClient(globalHandler);
        var global = new TerraScaleClient(
            globalHttpClient,
            new TerraScaleClientOptions { BearerToken = "caller-token" });

        await global.Documents.GetAsync("repository-a", "document-a");

        await Assert.That(globalHandler.Requests.Single().Uri)
            .IsEqualTo(new Uri("https://api.terrascale.tech/api/v1/repositories/repository-a/documents/document-a"));

        using var strictHandler = new RecordingHandler(DocumentJson());
        using var strictHttpClient = new HttpClient(strictHandler);
        var strict = new TerraScaleClient(
            strictHttpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token",
                PreferredRegion = "SFO",
                RegionAffinity = TerraScaleRegionAffinity.Strict
            });

        await strict.Documents.GetAsync("repository-a", "document-a");

        var request = strictHandler.Requests.Single();
        await Assert.That(request.Uri)
            .IsEqualTo(new Uri("https://sfo.api.terrascale.tech/api/v1/repositories/repository-a/documents/document-a"));
        await Assert.That(request.Headers.ContainsKey("X-TerraScale-Preferred-Region")).IsFalse();
    }

    [Test]
    public async Task FailoverConditionalMutationPropagatesSessionTokenToLaterRead()
    {
        using var handler = new RecordingHandler(
            DocumentJson(sessionConsistencyToken: "session-2"),
            DocumentJson());
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token",
                PreferredRegion = "FRA",
                RegionAffinity = TerraScaleRegionAffinity.Failover,
                SessionConsistency = TerraScaleSessionConsistency.Automatic
            });

        var written = await client.Documents.UpsertAsync(
            "repository-a",
            "document-a",
            new { Name = "Grace" },
            new TerraScaleDocumentMutationOptions("change-1"));
        var read = await client.Documents.GetAsync(
            "repository-a",
            "document-a",
            new TerraScaleDocumentReadOptions(AllowStale: true));

        await Assert.That(written.SessionConsistencyToken).IsEqualTo("session-2");
        await Assert.That(read.Id).IsEqualTo("document-a");
        await Assert.That(handler.Requests[0].Headers["If-Match"]).IsEqualTo("change-1");
        await Assert.That(handler.Requests[0].Headers["X-TerraScale-Preferred-Region"]).IsEqualTo("fra");
        await Assert.That(handler.Requests[1].Headers["X-TerraScale-Allow-Stale"]).IsEqualTo("true");
        await Assert.That(handler.Requests[1].Headers["X-TerraScale-Session-Consistency-Token"])
            .IsEqualTo("session-2");
    }

    [Test]
    public async Task AutomaticSessionTokensRemainScopedToTheirDocument()
    {
        using var handler = new RecordingHandler(
            DocumentJson(sessionConsistencyToken: "session-2"),
            DocumentJson(),
            DocumentPageJson(),
            DocumentJson());
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token",
                SessionConsistency = TerraScaleSessionConsistency.Automatic
            });

        await client.Documents.UpsertAsync("repository-a", "document-a", new { Name = "Grace" });
        await client.Documents.GetAsync("repository-a", "document-b");
        await client.Documents.ListAsync("repository-a");
        await client.Documents.GetAsync("repository-a", "document-a");

        await Assert.That(handler.Requests[1].Headers.ContainsKey("X-TerraScale-Session-Consistency-Token"))
            .IsFalse();
        await Assert.That(handler.Requests[2].Headers.ContainsKey("X-TerraScale-Session-Consistency-Token"))
            .IsFalse();
        await Assert.That(handler.Requests[3].Headers["X-TerraScale-Session-Consistency-Token"])
            .IsEqualTo("session-2");
    }

    [Test]
    public async Task BatchWriteSessionTokensApplyToEachAffectedDocument()
    {
        using var handler = new RecordingHandler(
            DocumentBatchJson(),
            DocumentJson(),
            DocumentJson());
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token",
                SessionConsistency = TerraScaleSessionConsistency.Automatic
            });

        await client.Documents.BatchAsync(
            "repository-alias",
            [
                TerraScaleDocumentBatchOperation.Upsert("document-a", new { Name = "Grace" }),
                TerraScaleDocumentBatchOperation.Upsert("document-b", new { Name = "Ada" })
            ]);
        await client.Documents.GetAsync("repository-alias", "document-a");
        await client.Documents.GetAsync("repository-id", "document-b");

        await Assert.That(handler.Requests[1].Headers["X-TerraScale-Session-Consistency-Token"])
            .IsEqualTo("session-a");
        await Assert.That(handler.Requests[2].Headers["X-TerraScale-Session-Consistency-Token"])
            .IsEqualTo("session-b");
    }

    [Test]
    public async Task RetryRetainsRegionalAndSessionHeaders()
    {
        using var handler = new RecordingHandler(DocumentJson(sessionConsistencyToken: "session-2"));
        handler.EnqueueResponse(
            """{"error":{"code":"temporarily_unavailable","message":"Retry the read."}}""",
            HttpStatusCode.ServiceUnavailable);
        handler.EnqueueResponse(DocumentJson(), HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token",
                PreferredRegion = "FRA",
                SessionConsistency = TerraScaleSessionConsistency.Automatic,
                MaxRetryAttempts = 1,
                RetryDelay = TimeSpan.Zero
            });

        await client.Documents.UpsertAsync("repository-a", "document-a", new { Name = "Grace" });
        await client.Documents.GetAsync("repository-a", "document-a");

        foreach (var request in handler.Requests.Skip(1))
        {
            await Assert.That(request.Headers["X-TerraScale-Preferred-Region"]).IsEqualTo("fra");
            await Assert.That(request.Headers["X-TerraScale-Session-Consistency-Token"])
                .IsEqualTo("session-2");
        }
    }

    [Test]
    public async Task AutomaticSessionTokensDoNotCrossClientInstances()
    {
        using var writerHandler = new RecordingHandler(DocumentJson(sessionConsistencyToken: "session-2"));
        using var writerHttpClient = new HttpClient(writerHandler);
        var writer = new TerraScaleClient(
            writerHttpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token",
                SessionConsistency = TerraScaleSessionConsistency.Automatic
            });
        await writer.Documents.UpsertAsync("repository-a", "document-a", new { Name = "Grace" });

        using var readerHandler = new RecordingHandler(DocumentJson());
        using var readerHttpClient = new HttpClient(readerHandler);
        var reader = new TerraScaleClient(
            readerHttpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token",
                SessionConsistency = TerraScaleSessionConsistency.Automatic
            });
        await reader.Documents.GetAsync("repository-a", "document-a");

        await Assert.That(readerHandler.Requests.Single().Headers)
            .DoesNotContainKey("X-TerraScale-Session-Consistency-Token");
    }

    [Test]
    public async Task RegionalFailureAndServingObservationRemainTyped()
    {
        using var unavailableHandler = new RecordingHandler(
            """{"error":{"code":"project_region_unavailable","message":"No selected region is available."}}""",
            HttpStatusCode.ServiceUnavailable);
        using var unavailableHttpClient = new HttpClient(unavailableHandler);
        var unavailableClient = new TerraScaleClient(
            unavailableHttpClient,
            new TerraScaleClientOptions { BearerToken = "caller-token" });

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            unavailableClient.Documents.GetAsync("repository-a", "document-a"));

        await Assert.That(exception!.IsRegionalUnavailable).IsTrue();

        using var routedHandler = new RecordingHandler(DocumentJson())
        {
            ResponseHeaders =
            {
                ["X-TerraScale-Edge-Region"] = "gru",
                ["X-TerraScale-Serving-Region"] = "sfo",
                ["X-TerraScale-Route-Mode"] = "preferred-failover",
                ["X-TerraScale-Failover-From-Region"] = "fra"
            }
        };
        using var routedHttpClient = new HttpClient(routedHandler);
        var routedClient = new TerraScaleClient(
            routedHttpClient,
            new TerraScaleClientOptions { BearerToken = "caller-token" });

        var routed = await routedClient.Documents.GetAsync("repository-a", "document-a");

        await Assert.That(routed.Routing)
            .IsEqualTo(new TerraScaleRoutingObservation(
                "gru",
                "sfo",
                IsDegraded: true,
                TerraScaleRouteMode.PreferredFailover,
                "fra"));
    }

    private static string DocumentJson(string? sessionConsistencyToken = null) => $$"""
        {
          "repositoryId":"repository-a",
          "repository":"users",
          "id":"document-a",
          "data":{"name":"Ada"},
          "lastModified":"2026-08-19T00:00:00Z",
          "changeToken":"change-2"{{(sessionConsistencyToken is null ? string.Empty : $",\"sessionConsistencyToken\":\"{sessionConsistencyToken}\"")}}
        }
        """;

    private static string DocumentPageJson() => """
        {
          "repositoryId":"repository-a",
          "repository":"users",
          "skip":0,
          "take":100,
          "total":0,
          "items":[]
        }
        """;

    private static string DocumentBatchJson() => """
        {
          "repositoryId":"repository-id",
          "repository":"users",
          "results":[
            {
              "index":0,
              "operation":"upsert",
              "id":"document-a",
              "document":{
                "repositoryId":"repository-id",
                "repository":"users",
                "id":"document-a",
                "data":{"name":"Grace"},
                "lastModified":"2026-08-19T00:00:00Z",
                "changeToken":"change-a",
                "sessionConsistencyToken":"session-a"
              },
              "deleted":null
            },
            {
              "index":1,
              "operation":"upsert",
              "id":"document-b",
              "document":{
                "repositoryId":"repository-id",
                "repository":"users",
                "id":"document-b",
                "data":{"name":"Ada"},
                "lastModified":"2026-08-19T00:00:00Z",
                "changeToken":"change-b",
                "sessionConsistencyToken":"session-b"
              },
              "deleted":null
            }
          ]
        }
        """;

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<(string Body, HttpStatusCode Status)> responses =
            new(responses.Select(response => (response, HttpStatusCode.OK)));

        public RecordingHandler(string response, HttpStatusCode statusCode) : this()
        {
            responses.Enqueue((response, statusCode));
        }

        public List<RecordedRequest> Requests { get; } = [];

        public Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void EnqueueResponse(string body, HttpStatusCode statusCode) =>
            responses.Enqueue((body, statusCode));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.Concat(
                        request.Content is null
                            ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()
                            : request.Content.Headers)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Single(), StringComparer.OrdinalIgnoreCase)));
            var (body, status) = responses.Dequeue();
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            foreach (var header in ResponseHeaders)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return Task.FromResult(response);
        }
    }

    private sealed record RecordedRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers);
}
