using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleDocumentsClientTests
{
    [Test]
    public async Task UpsertAsync_SendsRawJsonAndProjectsEtagAndSession()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"revision\":\"r1\"}", "\"r1\"", "7");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var document = await client.Documents.UpsertAsync("orders", "items", "item/1", new { total = 3 });

        await Assert.That(handler.Request?.RequestUri).IsEqualTo("https://api.example.invalid/api/v1/databases/orders/collections/items/documents/item%2F1");
        await Assert.That(handler.Request?.Body).IsEqualTo("{\"total\":3}");
        await Assert.That(document.ChangeToken).IsEqualTo("r1");
        await Assert.That(document.SessionConsistencyToken).IsEqualTo("7");
    }

    [Test]
    public async Task GetAsync_SendsIfNoneMatchAndSessionAndRequiresStrongEtag()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"total\":3}", "\"r1\"");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var document = await client.Documents.GetAsync("orders", "items", "item-1", new TerraScaleDocumentReadOptions(IfNoneMatch: "\"old\"", Session: "7"));

        await Assert.That(handler.Request?.IfNoneMatch).IsEqualTo("\"old\"");
        await Assert.That(handler.Request?.Session).IsEqualTo("7");
        await Assert.That(document.Data.GetProperty("total").GetInt32()).IsEqualTo(3);
        await Assert.That(document.ChangeToken).IsEqualTo("r1");
    }

    [Test]
    public async Task BatchAsync_UsesPublishedOperationShapeAndRejectsMoreThan1000()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"committed\":1,\"count\":1}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.Documents.BatchAsync("orders", "items", [TerraScaleDocumentBatchOperation.Upsert("item-1", new { ok = true })]);

        await Assert.That(handler.Request?.RequestUri).IsEqualTo("https://api.example.invalid/api/v1/databases/orders/collections/items/documents/batch");
        await Assert.That(handler.Request?.Body).IsEqualTo("{\"operations\":[{\"op\":\"put\",\"id\":\"item-1\",\"value\":{\"ok\":true}}]}");
        await Assert.That(result.Count).IsEqualTo(1);
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Documents.BatchAsync("orders", "items", Enumerable.Repeat(TerraScaleDocumentBatchOperation.Delete("x"), 1001).ToArray()));
        await Assert.That(exception).IsNotNull();
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
                request.Headers.TryGetValues("If-None-Match", out var etagValues) ? etagValues.Single() : null,
                request.Headers.TryGetValues("X-TerraScale-Session", out var sessionValues) ? sessionValues.Single() : null);
            var response = new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
            if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag);
            if (session is not null) response.Headers.TryAddWithoutValidation("X-TerraScale-Session", session);
            return response;
        }
    }

    private sealed record RecordedRequest(string RequestUri, string? Body, string? IfNoneMatch, string? Session);
}
