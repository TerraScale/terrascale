using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleSavedQueriesClientTests
{
    [Test]
    public async Task SavedQueryOperations_SendPublicRoutesBodiesAndIdempotencyKeys()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
            ProjectId = "project-a",
            IdempotencyKeyFactory = () => "factory-key"
        });

        var queries = await client.SavedQueries.ListAsync("project-a");
        var created = await client.SavedQueries.CreateAsync(
            "project-a",
            new TerraScaleSaveQueryRequest("Recent orders", "from Orders", "repository-a"));
        var updated = await client.SavedQueries.UpdateAsync(
            "project-a",
            "saved-query-a",
            new TerraScaleSaveQueryRequest("Recent paid orders", "from Orders where Paid = true"));
        var deleted = await client.SavedQueries.DeleteAsync("project-a", "saved-query-a");

        await Assert.That(handler.Requests).Count().IsEqualTo(4);
        await Assert.That(handler.Requests[0]).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v1/projects/project-a/saved-queries?skip=0&take=100",
            null,
            null));
        await Assert.That(handler.Requests[1]).IsEqualTo(new RecordedRequest(
            HttpMethod.Post,
            "https://api.example.invalid/api/v1/projects/project-a/saved-queries",
            "{\"name\":\"Recent orders\",\"query\":\"from Orders\",\"repositoryId\":\"repository-a\"}",
            "factory-key"));
        await Assert.That(handler.Requests[2]).IsEqualTo(new RecordedRequest(
            HttpMethod.Put,
            "https://api.example.invalid/api/v1/projects/project-a/saved-queries/saved-query-a",
            "{\"name\":\"Recent paid orders\",\"query\":\"from Orders where Paid = true\",\"repositoryId\":null}",
            "factory-key"));
        await Assert.That(handler.Requests[3]).IsEqualTo(new RecordedRequest(
            HttpMethod.Delete,
            "https://api.example.invalid/api/v1/projects/project-a/saved-queries/saved-query-a",
            null,
            "factory-key"));
        await Assert.That(queries[0].RepositoryId).IsNull();
        await Assert.That(created.CreatedAt).IsEqualTo(DateTimeOffset.Parse("2026-08-05T12:00:00+00:00"));
        await Assert.That(updated.Name).IsEqualTo("Recent paid orders");
        await Assert.That(deleted.Deleted).IsTrue();
    }

    [Test]
    public async Task ListAsync_AllowsPublicDefaultAndMaximumTakeButRejectsHigherValues()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
            ProjectId = "project-a"
        });

        await client.SavedQueries.ListAsync("project-a", take: 0);
        await client.SavedQueries.ListAsync("project-a", take: 500);
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.SavedQueries.ListAsync("project-a", take: 501));

        await Assert.That(handler.Requests[0].Uri).IsEqualTo(
            "https://api.example.invalid/api/v1/projects/project-a/saved-queries?skip=0&take=0");
        await Assert.That(handler.Requests[1].Uri).IsEqualTo(
            "https://api.example.invalid/api/v1/projects/project-a/saved-queries?skip=0&take=500");
        await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task SavedQueryModels_SerializeAndDeserializeContractFields()
    {
        var request = new TerraScaleSaveQueryRequest("Orders", "from Orders");
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var response = JsonSerializer.Deserialize<TerraScaleSavedQuery>("""
            {"savedQueryId":"saved-query-a","projectId":"project-a","name":"Orders","query":"from Orders","repositoryId":null,"createdAt":"2026-08-05T12:00:00+00:00","updatedAt":"2026-08-05T13:00:00+00:00"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await Assert.That(json).IsEqualTo("{\"name\":\"Orders\",\"query\":\"from Orders\",\"repositoryId\":null}");
        await Assert.That(response).IsNotNull();
        await Assert.That(response!.RepositoryId).IsNull();
        await Assert.That(response.UpdatedAt).IsEqualTo(DateTimeOffset.Parse("2026-08-05T13:00:00+00:00"));
    }

    [Test]
    public async Task SavedQueryOperations_RejectInvalidArguments()
    {
        using var httpClient = new HttpClient(new RecordingHandler());
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
            ProjectId = "project-a"
        });

        var listException = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SavedQueries.ListAsync("project-a", take: -1));
        var createException = await Assert.ThrowsAsync<ArgumentException>(() => client.SavedQueries.CreateAsync(
            "project-a",
            new TerraScaleSaveQueryRequest(" ", "from Orders")));
        var updateException = await Assert.ThrowsAsync<ArgumentException>(() => client.SavedQueries.UpdateAsync(
            "project-a",
            " ",
            new TerraScaleSaveQueryRequest("Orders", "from Orders")));

        await Assert.That(listException).IsNotNull();
        await Assert.That(createException).IsNotNull();
        await Assert.That(updateException).IsNotNull();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null));

            var body = request.Method == HttpMethod.Delete
                ? "{\"savedQueryId\":\"saved-query-a\",\"projectId\":\"project-a\",\"deleted\":true}"
                : request.Method == HttpMethod.Get
                    ? "[{\"savedQueryId\":\"saved-query-a\",\"projectId\":\"project-a\",\"name\":\"Orders\",\"query\":\"from Orders\",\"repositoryId\":null,\"createdAt\":\"2026-08-05T12:00:00+00:00\",\"updatedAt\":\"2026-08-05T13:00:00+00:00\"}]"
                    : "{\"savedQueryId\":\"saved-query-a\",\"projectId\":\"project-a\",\"name\":\"Recent paid orders\",\"query\":\"from Orders where Paid = true\",\"repositoryId\":null,\"createdAt\":\"2026-08-05T12:00:00+00:00\",\"updatedAt\":\"2026-08-05T13:00:00+00:00\"}";
            return new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Body, string? IdempotencyKey);
}
