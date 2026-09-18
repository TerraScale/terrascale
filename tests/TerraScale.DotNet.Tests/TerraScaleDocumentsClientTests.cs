using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleDocumentsClientTests
{
    [Test]
    public async Task UnknownUnkeyedPutOutcomeIsNotRetried()
    {
        using var handler = new DroppedResponseHandler();
        using var http = new HttpClient(handler);
        var client = new TerraScaleClient(http, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
            MaxRetryAttempts = 2,
            RetryDelay = TimeSpan.Zero
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.Documents.PutAsync("db", "items", "one", new { value = 1 }));
        await Assert.That(handler.Keys.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CallerDocumentKeyControlsRetryAndOverridesFactory()
    {
        foreach (var method in new[] { "put", "patch", "delete" })
        {
            using var handler = new DroppedResponseHandler();
            using var http = new HttpClient(handler);
            var factoryCalls = 0;
            var client = new TerraScaleClient(http, new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                ApiKey = "test-key",
                MaxRetryAttempts = 1,
                RetryDelay = TimeSpan.Zero,
                IdempotencyKeyFactory = () => { factoryCalls++; return "factory-key"; }
            });
            var options = new TerraScaleDocumentMutationOptions(IdempotencyKey: " caller-key ");
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                switch (method)
                {
                    case "put": await client.Documents.PutAsync("db", "items", "one", new { value = 1 }, options); break;
                    case "patch": await client.Documents.PatchAsync("db", "items", "one", new { value = 1 }, options); break;
                    case "delete": await client.Documents.DeleteAsync("db", "items", "one", options); break;
                }
            });
            await Assert.That(handler.Keys.SequenceEqual(["caller-key", "caller-key"])).IsTrue();
            await Assert.That(factoryCalls).IsEqualTo(0);
        }
    }


    [Test]
    public async Task QueryAsync_SendsPredicateProjectionAndMapsPlanAndWork()
    {
        var response = """
            {
              "database": "orders",
              "collection": "items",
              "documents": [{ "id": "item-1", "version": 4, "value": { "name": "Ada" } }],
              "returnedCount": 1,
              "hasMore": true,
              "nextAfter": "item-1",
              "sourceCommitSequence": 12,
              "plan": {
                "indexName": "idx_name",
                "access": "EqualitySeek",
                "isCovered": true,
                "isFresh": true,
                "indexCommitSequence": 12,
                "indexObservedAt": "2026-09-15T00:00:00Z",
                "usedSnapshotFallback": false,
                "estimatedMatches": 1,
                "actualMatches": 1,
                "reason": "covering"
              },
              "work": { "pagesRead": 1, "rowsExamined": 3, "bytesRead": 512, "workUnits": 5 }
            }
            """;
        using var handler = new RecordingHandler(HttpStatusCode.OK, response);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.Documents.QueryAsync(
            "orders",
            "items",
            new TerraScaleDocumentQuery(
                Predicates: [new TerraScaleDocumentPredicate("name", TerraScaleQueryOperator.Equal, JsonSerializer.SerializeToElement("Ada"))],
                Projection: ["name"],
                Limit: 10));

        await Assert.That(handler.Request?.RequestUri).IsEqualTo("https://api.example.invalid/api/v1/databases/orders/collections/items/documents/query");

        await Assert.That(handler.Request?.Body).IsEqualTo("{\"predicates\":[{\"path\":\"name\",\"operator\":\"equal\",\"value\":\"Ada\"}],\"projection\":[\"name\"],\"limit\":10}");
        await Assert.That(result.Documents.Count).IsEqualTo(1);
        await Assert.That(result.Documents[0].Id).IsEqualTo("item-1");
        await Assert.That(result.Documents[0].Version).IsEqualTo(4UL);
        await Assert.That(result.ReturnedCount).IsEqualTo(1);
        await Assert.That(result.HasMore).IsTrue();
        await Assert.That(result.NextAfter).IsEqualTo("item-1");
        await Assert.That(result.SourceCommitSequence).IsEqualTo(12UL);
        await Assert.That(result.Plan.Access).IsEqualTo("EqualitySeek");
        await Assert.That(result.Plan.IsCovered).IsTrue();
        await Assert.That(result.Work.RowsExamined).IsEqualTo(3UL);
    }

    [Test]
    public async Task ExplainAsync_SendsTheExplainFlagAndMapsCandidates()
    {
        var response = """
            {
              "database": "orders",
              "collection": "items",
              "explain": {
                "indexName": "idx_status",
                "access": "EqualitySeek",
                "isCovered": false,
                "isFresh": true,
                "indexCommitSequence": 12,
                "indexObservedAt": "2026-09-15T00:00:00Z",
                "usedSnapshotFallback": false,
                "estimatedMatches": 1,
                "reason": "selected",
                "candidates": [
                  {
                    "indexName": "idx_status",
                    "accepted": true,
                    "access": "EqualitySeek",
                    "isCovered": false,
                    "isFresh": true,
                    "indexCommitSequence": 12,
                    "indexObservedAt": "2026-09-15T00:00:00Z",
                    "estimatedMatches": 1,
                    "indexCost": 2,
                    "sourceScanCost": 0,
                    "totalCost": 2,
                    "usesSnapshotFallback": false,
                    "reason": "selected"
                  }
                ],
                "rejectedCandidates": []
              }
            }
            """;
        using var handler = new RecordingHandler(HttpStatusCode.OK, response);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.Documents.ExplainAsync(
            "orders",
            "items",
            new TerraScaleDocumentQuery(
                Predicates: [new TerraScaleDocumentPredicate("status", TerraScaleQueryOperator.Equal, JsonSerializer.SerializeToElement("active"))],
                Limit: 10));

        await Assert.That(handler.Request?.RequestUri).IsEqualTo("https://api.example.invalid/api/v1/databases/orders/collections/items/documents/query");
        await Assert.That(handler.Request?.Body).IsEqualTo("{\"predicates\":[{\"path\":\"status\",\"operator\":\"equal\",\"value\":\"active\"}],\"limit\":10,\"explain\":true}");
        await Assert.That(result.DatabaseId).IsEqualTo("orders");
        await Assert.That(result.Collection).IsEqualTo("items");
        await Assert.That(result.Explain.IndexName).IsEqualTo("idx_status");
        await Assert.That(result.Explain.Access).IsEqualTo("EqualitySeek");
        await Assert.That(result.Explain.IsFresh).IsTrue();
        await Assert.That(result.Explain.Candidates).Count().IsEqualTo(1);
        await Assert.That(result.Explain.Candidates![0].TotalCost).IsEqualTo(2D);
        await Assert.That(result.Explain.Candidates[0].Accepted).IsTrue();
        await Assert.That(result.Explain.RejectedCandidates).Count().IsEqualTo(0);
    }

    [Test]
    public async Task QueryAsync_RejectsExistsPredicateWithValueAndOverLimitPages()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Documents.QueryAsync(
            "orders",
            "items",
            new TerraScaleDocumentQuery(Predicates: [new TerraScaleDocumentPredicate("name", TerraScaleQueryOperator.Exists, JsonSerializer.SerializeToElement(1))])));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Documents.QueryAsync(
            "orders",
            "items",
            new TerraScaleDocumentQuery(Limit: 1001)));
        await Assert.That(handler.Request).IsNull();
    }

    private sealed class DroppedResponseHandler : HttpMessageHandler
    {
        public List<string?> Keys { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Keys.Add(request.Headers.TryGetValues("Idempotency-Key", out var keys) ? string.Join(",", keys) : null);
            throw new HttpRequestException("Response lost after transmission.");
        }
    }

    [Test]
    public async Task UpsertAsync_SendsRawJsonAndProjectsEtag()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"revision\":\"r1\"}", "\"r1\"");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var document = await client.Documents.UpsertAsync("orders", "items", "item/1", new { total = 3 });

        await Assert.That(handler.Request?.RequestUri).IsEqualTo("https://api.example.invalid/api/v1/databases/orders/collections/items/documents/item%2F1");
        await Assert.That(handler.Request?.Body).IsEqualTo("{\"total\":3}");
        await Assert.That(document.ChangeToken).IsEqualTo("r1");
        await Assert.That(document.SessionConsistencyToken).IsNull();
    }

    [Test]
    public async Task MutationExpectedChangeTokenUsesStrongEtagSyntax()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"revision\":\"r2\"}", "\"r2\"");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        await client.Documents.PutAsync("orders", "items", "item-1", new { total = 3 }, new TerraScaleDocumentMutationOptions(ExpectedChangeToken: "r1"));

        await Assert.That(handler.Request?.IfMatch).IsEqualTo("\"r1\"");
    }

    [Test]
    public async Task GetAsync_SendsThePointInTimeAtParameterExactly()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"total\":3}", "\"r1\"");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await client.Documents.GetAsync("orders", "items", "one", new TerraScaleDocumentReadOptions(At: 1700000000123));

        await Assert.That(handler.Request!.RequestUri).IsEqualTo("https://api.example.invalid/api/v1/databases/orders/collections/items/documents/one?at=1700000000123");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Documents.GetAsync("orders", "items", "one", new TerraScaleDocumentReadOptions(At: -1)));
    }

    [Test]
    public async Task GetAsync_SendsTheExplicitSessionHeaderVerbatim()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"total\":3}", "\"r1\"", "tracked-token");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var document = await client.Documents.GetAsync("orders", "items", "one", new TerraScaleDocumentReadOptions(Session: "session-1"));

        await Assert.That(handler.Request!.Session).IsEqualTo("session-1");
        await Assert.That(document.SessionConsistencyToken).IsEqualTo("tracked-token");
    }

    [Test]
    public async Task GetAsync_OmitsTheSessionHeaderWhenNoTokenApplies()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"total\":3}", "\"r1\"", "tracked-token");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await client.Documents.GetAsync("orders", "items", "one");

        await Assert.That(handler.Request!.Session).IsNull();
    }

    [Test]
    public async Task AutomaticSessionConsistencyReplaysTheTrackedToken()
    {
        using var handler = new SequencedHandler("tracked-token");
        using var http = new HttpClient(handler);
        var client = new TerraScaleClient(http, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
            SessionConsistency = TerraScaleSessionConsistency.Automatic
        });

        await client.Documents.GetAsync("orders", "items", "one");
        await client.Documents.GetAsync("orders", "items", "one");

        await Assert.That(handler.Sessions.SequenceEqual([null, "tracked-token"])).IsTrue();
    }

    [Test]
    public async Task AutomaticSessionConsistencyClearsTheBoundedScopeCache()
    {
        const int scopes = 4096;
        using var handler = new SequencedHandler("tracked-token");
        using var http = new HttpClient(handler);
        var client = new TerraScaleClient(http, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
            SessionConsistency = TerraScaleSessionConsistency.Automatic
        });

        for (var index = 0; index <= scopes; index++)
        {
            await client.Documents.GetAsync("orders", "items", "doc-" + index.ToString(CultureInfo.InvariantCulture));
        }

        // The epoch clear dropped the first document's token but kept the last one.
        await client.Documents.GetAsync("orders", "items", "doc-0");
        await client.Documents.GetAsync("orders", "items", $"doc-{scopes}");
        await Assert.That(handler.Sessions[^2]).IsNull();
        await Assert.That(handler.Sessions[^1]).IsEqualTo("tracked-token");
    }

    [Test]
    public async Task MutationSessionRidesThePublishedHeader()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"revision\":\"r1\"}", "\"r1\"", "reply-token");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await client.Documents.PutAsync("orders", "items", "one", new { total = 3 }, new TerraScaleDocumentMutationOptions(Session: " session-1 "));

        await Assert.That(handler.Request!.Session).IsEqualTo("session-1");
    }

    [Test]
    public async Task ConditionalGetSendsIfNoneMatchAndSurfacesNotModified()
    {
        using var handler = new RecordingHandler(HttpStatusCode.NotModified, string.Empty, "\"r1\"");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Documents.GetAsync(
            "orders",
            "items",
            "one",
            new TerraScaleDocumentReadOptions(IfNoneMatch: " \"r1\" ")));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(handler.Request!.IfNoneMatch).IsEqualTo("\"r1\"");
    }

    [Test]
    public async Task BatchAsync_SendsAtomicOperationsWithMutationHeaders()
    {
        using var handler = new BatchHandler("{\"committed\":42,\"count\":2}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var batch = await client.Documents.BatchAsync(
            "orders",
            "items",
            [
                TerraScaleDocumentBatchOperation.Upsert("item-1", new { ok = true }),
                TerraScaleDocumentBatchOperation.Delete("item-2"),
            ],
            new TerraScaleDocumentMutationOptions(IdempotencyKey: "batch-key", Durability: TerraScaleDurability.Global));

        await Assert.That(handler.RequestUri).IsEqualTo("https://api.example.invalid/api/v1/databases/orders/collections/items/documents/batch");
        await Assert.That(handler.Body).IsEqualTo("{\"operations\":[{\"op\":\"put\",\"id\":\"item-1\",\"value\":{\"ok\":true}},{\"op\":\"delete\",\"id\":\"item-2\",\"value\":null}]}");
        await Assert.That(handler.IdempotencyKey).IsEqualTo("batch-key");
        await Assert.That(handler.Durability).IsEqualTo("global");
        await Assert.That(batch.DatabaseId).IsEqualTo("orders");
        await Assert.That(batch.Collection).IsEqualTo("items");
        await Assert.That(batch.Committed).IsEqualTo(42L);
        await Assert.That(batch.Count).IsEqualTo(2);
    }

    [Test]
    public async Task BatchAsync_RejectsUnsupportedOptionsAndBounds()
    {
        using var handler = new BatchHandler("{\"committed\":0,\"count\":0}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Documents.BatchAsync("orders", "items", [TerraScaleDocumentBatchOperation.Upsert("item-1", new { ok = true })], new TerraScaleDocumentMutationOptions(CreateOnly: true)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Documents.BatchAsync("orders", "items", [TerraScaleDocumentBatchOperation.Upsert("item-1", new { ok = true })], new TerraScaleDocumentMutationOptions(ExpectedChangeToken: "r1")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Documents.BatchAsync("orders", "items", []));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Documents.BatchAsync("orders", "items", Enumerable.Repeat(TerraScaleDocumentBatchOperation.Delete("item-1"), 1001).ToArray()));

        await Assert.That(handler.RequestUri).IsNull();
    }

    [Test]
    public async Task HistoryAsync_SendsBoundedPaginationAndMapsRetainedRevisions()
    {
        var response = """
            {
              "history": [
                {
                  "revision": "r2",
                  "deleted": true,
                  "commitUlid": "01J8Z",
                  "hlc": 7,
                  "origin": "GRU",
                  "policy": "remove-wins",
                  "explanation": "tombstone"
                },
                {
                  "revision": "r1",
                  "deleted": false,
                  "commitUlid": "01J8Y",
                  "hlc": 3,
                  "origin": "SFO",
                  "policy": "remove-wins",
                  "explanation": "initial"
                }
              ]
            }
            """;
        using var handler = new RecordingHandler(HttpStatusCode.OK, response);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var history = await client.Documents.HistoryAsync("orders", "items", "item-1", limit: 2, after: "r2");

        await Assert.That(handler.Request?.RequestUri).IsEqualTo("https://api.example.invalid/api/v1/databases/orders/collections/items/documents/item-1/history?limit=2&after=r2");
        await Assert.That(history).Count().IsEqualTo(2);
        await Assert.That(history[0].Revision).IsEqualTo("r2");
        await Assert.That(history[0].Deleted).IsTrue();
        await Assert.That(history[0].CommitUlid).IsEqualTo("01J8Z");
        await Assert.That(history[0].Hlc).IsEqualTo(7L);
        await Assert.That(history[0].Origin).IsEqualTo("GRU");
        await Assert.That(history[0].Policy).IsEqualTo("remove-wins");
        await Assert.That(history[0].Explanation).IsEqualTo("tombstone");
        await Assert.That(history[1].Deleted).IsFalse();
    }

    [Test]
    public async Task HistoryAsync_RejectsUnboundedPagination()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"history\":[]}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Documents.HistoryAsync("orders", "items", "item-1", limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Documents.HistoryAsync("orders", "items", "item-1", limit: 1001));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Documents.HistoryAsync("orders", "items", "item-1", after: " "));

        await Assert.That(handler.Request).IsNull();
    }

    [Test]
    public async Task CreateOnlyUsesTheDurablePutPrecondition()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"revision\":\"1\"}", "\"1\"");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        await client.Documents.CreateAsync("db", "items", "stable-id", new { ok = true });
        await Assert.That(handler.Request!.IfNoneMatch).IsEqualTo("*");

        var createOnly = new TerraScaleDocumentMutationOptions(CreateOnly: true);
        await Assert.ThrowsAsync<ArgumentException>(() => client.Documents.PatchAsync("db", "items", "stable-id", new { ok = true }, createOnly));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Documents.DeleteAsync("db", "items", "stable-id", createOnly));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Documents.BatchAsync("db", "items", [TerraScaleDocumentBatchOperation.Delete("stable-id")], createOnly));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Documents.PutAsync("db", "items", "stable-id", new { ok = true }, createOnly with { ExpectedChangeToken = "\"1\"" }));
    }

    [Test]
    public async Task ExistingDocumentCreateOnlyFailureRemainsTyped()
    {
        using var handler = new RecordingHandler(HttpStatusCode.PreconditionFailed, "{\"error\":{\"code\":\"precondition_failed\",\"message\":\"exists\",\"fix\":\"read existing document\"}}");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        var error = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Documents.PutAsync("db", "items", "stable-id", new { ok = true }, new TerraScaleDocumentMutationOptions(CreateOnly: true)));
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That(handler.Request!.IfNoneMatch).IsEqualTo("*");
    }

    [Test]
    public async Task ConditionalDeleteAcceptsThePublishedBodylessResponse()
    {
        using var handler = new RecordingHandler(HttpStatusCode.NoContent, "");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await client.Documents.DeleteAsync("db", "items", "stable-id", new TerraScaleDocumentMutationOptions(IdempotencyKey: "delete-1"));
        await Assert.That(handler.Request).IsNotNull();
    }

    [Test]
    public async Task NativeTimestampAndBytesUseCanonicalSchemaValues()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"revision\":\"1\"}", "\"1\"");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000123);
        await client.Documents.PutAsync("db", "items", "one", new { timestamp, utc = timestamp.UtcDateTime, bytes = new byte[] { 0, 255 } });
        using var body = JsonDocument.Parse(handler.Request!.Body!);
        await Assert.That(body.RootElement.GetProperty("timestamp").GetInt64()).IsEqualTo(1700000000123L);
        await Assert.That(body.RootElement.GetProperty("utc").GetInt64()).IsEqualTo(1700000000123L);
        await Assert.That(body.RootElement.GetProperty("bytes").GetString()).IsEqualTo("AP8=");
        var parameters = TerraScaleSqlParameters.FromOrdinal(timestamp, timestamp.UtcDateTime);
        await Assert.That(parameters["1"].GetInt64()).IsEqualTo(1700000000123L);
        await Assert.That(parameters["2"].GetInt64()).IsEqualTo(1700000000123L);
        await Assert.ThrowsAsync<JsonException>(() => client.Documents.PutAsync("db", "items", "one", new { timestamp = new DateTime(2026, 1, 1) }));
    }

    [Test]
    public async Task ListAsyncPreservesUnsignedDocumentVersions()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"documents\":[{\"id\":\"large\",\"version\":18446744073709551615,\"value\":{\"ok\":true}}]}");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var page = await client.Documents.ListAsync("orders", "items");

        await Assert.That(page.Items).Count().IsEqualTo(1);
        await Assert.That(page.Items[0].ChangeToken).IsEqualTo(ulong.MaxValue.ToString(CultureInfo.InvariantCulture));
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody, string? etag = null, string? session = null) : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = new RecordedRequest(
                request.RequestUri?.ToString() ?? throw new InvalidOperationException(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("If-Match", out var ifMatchValues) ? ifMatchValues.Single() : null,
                request.Headers.TryGetValues("If-None-Match", out var etagValues) ? etagValues.Single() : null,
                request.Headers.TryGetValues("X-TerraScale-Session", out var sessionValues) ? sessionValues.Single() : null);
            var response = new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
            if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag);
            if (session is not null) response.Headers.TryAddWithoutValidation("X-TerraScale-Session", session);
            return response;
        }
    }

    private sealed record RecordedRequest(string RequestUri, string? Body, string? IfMatch, string? IfNoneMatch, string? Session);

    /// <summary>Records the session header of every request and answers each read with one session token.</summary>
    private sealed class SequencedHandler(string? session) : HttpMessageHandler
    {
        public List<string?> Sessions { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sessions.Add(request.Headers.TryGetValues("X-TerraScale-Session", out var values) ? values.Single() : null);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"total\":3}", Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("ETag", "\"r1\"");
            if (session is not null) response.Headers.TryAddWithoutValidation("X-TerraScale-Session", session);
            return Task.FromResult(response);
        }
    }

    private sealed class BatchHandler(string responseBody) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? Durability { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
            Durability = request.Headers.TryGetValues("X-TerraScale-Durability", out var durability) ? durability.Single() : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
