using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleClientTests
{
    [Test]
    public async Task CreateDocumentAsync_SendsApiKeyProjectHeadersAndDocumentPayload()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.RequestUri?.PathAndQuery).IsEqualTo("/api/v1/repositories/orders/documents");
            await Assert.That(request.Headers.GetValues("X-TerraScale-Auth-Key").Single()).IsEqualTo("tsauth_secret");
            await Assert.That(request.Headers.GetValues("X-TerraScale-Project-Id").Single()).IsEqualTo("project_123");

            var json = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            await Assert.That(document.RootElement.GetProperty("data").GetProperty("status").GetString())
                .IsEqualTo("paid");

            return Json(HttpStatusCode.Created, """
                {
                  "repository": "orders",
                  "id": "orders/1",
                  "data": { "status": "paid" },
                  "lastModified": "2026-06-10T00:00:00+00:00",
                  "changeVector": "A:1"
                }
                """);
        }));
        var client = CreateClient(httpClient);

        var created = await client.Documents.CreateAsync(
            "orders",
            new { status = "paid" });

        await Assert.That(created.Id).IsEqualTo("orders/1");
        await Assert.That(created.Data.GetProperty("status").GetString()).IsEqualTo("paid");
    }

    [Test]
    public async Task ListDatabasesAsync_UsesPublicDatabaseEndpoint()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
            await Assert.That(request.RequestUri?.PathAndQuery).IsEqualTo("/api/v1/databases?start=5&take=10");
            return Json(HttpStatusCode.OK, """[{ "name": "customer-data" }]""");
        }));
        var client = CreateClient(httpClient);

        var databases = await client.Databases.ListAsync(skip: 5, take: 10);

        await Assert.That(databases.Single().Name).IsEqualTo("customer-data");
    }

    [Test]
    public async Task GetDocumentAsync_PreservesDocumentIdPathSegments()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
            await Assert.That(request.RequestUri?.PathAndQuery)
                .IsEqualTo("/api/v1/repositories/orders/documents/orders/1");

            return Json(HttpStatusCode.OK, """
                {
                  "repository": "orders",
                  "id": "orders/1",
                  "data": { "status": "paid" },
                  "lastModified": "2026-06-10T00:00:00+00:00",
                  "changeVector": "A:1"
                }
                """);
        }));
        var client = CreateClient(httpClient);

        var document = await client.Documents.GetAsync("orders", "orders/1");

        await Assert.That(document.Id).IsEqualTo("orders/1");
    }

    [Test]
    public async Task QueryAsync_SendsTqlPayloadAndReturnsDocumentPage()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.RequestUri?.PathAndQuery).IsEqualTo("/api/v1/repositories/orders/tql");

            var json = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            await Assert.That(document.RootElement.GetProperty("query").GetString()).IsEqualTo("from 'orders'");
            await Assert.That(document.RootElement.GetProperty("take").GetInt32()).IsEqualTo(10);

            return Json(HttpStatusCode.OK, """
                {
                  "repository": "orders",
                  "skip": 0,
                  "take": 10,
                  "total": 1,
                  "items": [
                    {
                      "repository": "orders",
                      "id": "orders/1",
                      "data": { "status": "paid" },
                      "lastModified": "2026-06-10T00:00:00+00:00",
                      "changeVector": "A:1"
                    }
                  ]
                }
                """);
        }));
        var client = CreateClient(httpClient);

        var page = await client.Tql.QueryAsync("orders", "from 'orders'", take: 10);

        await Assert.That(page.Total).IsEqualTo(1);
        await Assert.That(page.Items.Single().Id).IsEqualTo("orders/1");
    }

    [Test]
    public async Task QueryAsync_ThrowsTypedApiExceptionForTqlSyntaxErrors()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.RequestUri?.PathAndQuery).IsEqualTo("/api/v1/repositories/orders/tql");
            return Json(HttpStatusCode.BadRequest, """
                {
                  "code": "TERRASCALE-2100",
                  "message": "Query syntax error.",
                  "detail": "This TQL endpoint currently supports only 'from <repository>' queries.",
                  "traceId": "req_tql"
                }
                """);
        }));
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Tql.QueryAsync("orders", "from 'orders' where status = 'paid'"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(exception.ErrorCode).IsEqualTo("TERRASCALE-2100");
        await Assert.That(exception.ResponseBody).Contains("from <repository>");
    }

    [Test]
    public async Task Requests_UseCallerConfiguredHttpVersion()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.Version).IsEqualTo(HttpVersion.Version20);
            await Assert.That(request.VersionPolicy).IsEqualTo(HttpVersionPolicy.RequestVersionExact);
            return Json(HttpStatusCode.OK, """[{ "name": "customer-data" }]""");
        }))
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var client = CreateClient(httpClient);

        var databases = await client.Databases.ListAsync();

        await Assert.That(databases.Single().Name).IsEqualTo("customer-data");
    }

    [Test]
    public async Task NonSuccessResponse_ThrowsTypedApiExceptionWithErrorDetails()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """
                {
                  "error": {
                    "code": "TERRASCALE-4290",
                    "message": "Rate limit exceeded."
                  }
                }
                """);
            response.Headers.TryAddWithoutValidation("X-Request-Id", "req_123");
            response.Headers.TryAddWithoutValidation("Retry-After", "3");
            return Task.FromResult(response);
        }));
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync("orders", "orders/1"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(exception.ErrorCode).IsEqualTo("TERRASCALE-4290");
        await Assert.That(exception.RequestId).IsEqualTo("req_123");
        await Assert.That(exception.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Options_RequireProjectIdWhenApiKeyIsConfigured()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new TerraScaleClient(
                new HttpClient(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}")))),
                new TerraScaleClientOptions
                {
                    BaseAddress = new Uri("https://api.example.test"),
                    ApiKey = "tsauth_secret"
                }));

        await Assert.That(exception.Message).Contains("ProjectId is required");
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) =>
        new(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.test"),
                ApiKey = "tsauth_secret",
                ProjectId = "project_123"
            });

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handle(request);
    }
}
