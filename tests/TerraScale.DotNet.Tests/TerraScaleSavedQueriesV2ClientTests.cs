using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleSavedQueriesV2ClientTests
{
    [Test]
    public async Task SavedQueryV2Operations_SendVersionedRoutesAndOpaqueRevision()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var listed = await client.SavedQueriesV2.ListAsync("project-a", skip: 2, take: 3);
        var loaded = await client.SavedQueriesV2.GetAsync("project-a", "saved-query-a");
        var updated = await client.SavedQueriesV2.UpdateAsync(
            "project-a",
            "saved-query-a",
            new TerraScaleUpdateSavedQueryV2Request("Recent paid orders", "from Orders where Paid = true", "opaque-revision"));

        await Assert.That(handler.Requests).Count().IsEqualTo(3);
        await Assert.That(handler.Requests[0]).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v2/projects/project-a/saved-queries?skip=2&take=3",
            null));
        await Assert.That(handler.Requests[1]).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v2/projects/project-a/saved-queries/saved-query-a",
            null));
        await Assert.That(handler.Requests[2]).IsEqualTo(new RecordedRequest(
            HttpMethod.Put,
            "https://api.example.invalid/api/v2/projects/project-a/saved-queries/saved-query-a",
            "{\"name\":\"Recent paid orders\",\"query\":\"from Orders where Paid = true\",\"revision\":\"opaque-revision\",\"repositoryId\":null}"));
        await Assert.That(listed[0].Revision).IsEqualTo("opaque-revision");
        await Assert.That(loaded.Revision).IsEqualTo("opaque-revision");
        await Assert.That(updated.Revision).IsEqualTo("new-opaque-revision");
    }

    [Test]
    public async Task SavedQueryV2Update_RequiresRevisionAndMapsStaleConflict()
    {
        using var handler = new RecordingHandler(returnConflictForUpdate: true);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var missingRevision = await Assert.ThrowsAsync<ArgumentException>(() => client.SavedQueriesV2.UpdateAsync(
            "project-a",
            "saved-query-a",
            new TerraScaleUpdateSavedQueryV2Request("Orders", "from Orders", " ")));
        var conflict = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.SavedQueriesV2.UpdateAsync(
            "project-a",
            "saved-query-a",
            new TerraScaleUpdateSavedQueryV2Request("Orders", "from Orders", "stale-opaque-revision")));

        await Assert.That(missingRevision).IsNotNull();
        if (conflict is null)
        {
            throw new InvalidOperationException("The stale revision fixture did not produce an API exception.");
        }

        await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(conflict.ErrorCode).IsEqualTo("TERRASCALE-1401");
        var responseBody = conflict.ResponseBody
            ?? throw new InvalidOperationException("The stale revision API exception did not include a response body.");
        await Assert.That(responseBody.Contains("stale-opaque-revision", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task SavedQueryV2Models_DeserializePaginationResponseAndKeepV1Separate()
    {
        var v2 = JsonSerializer.Deserialize<IReadOnlyList<TerraScaleSavedQueryV2>>("""
            [{"savedQueryId":"saved-query-a","projectId":"project-a","name":"Orders","query":"from Orders","repositoryId":null,"createdAt":"2026-08-05T12:00:00+00:00","updatedAt":"2026-08-05T13:00:00+00:00","revision":"opaque-revision"}]
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var v1 = JsonSerializer.Deserialize<TerraScaleSavedQuery>("""
            {"savedQueryId":"saved-query-a","projectId":"project-a","name":"Orders","query":"from Orders","repositoryId":null,"createdAt":"2026-08-05T12:00:00+00:00","updatedAt":"2026-08-05T13:00:00+00:00"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        if (v2 is null || v2.Count == 0)
        {
            throw new InvalidOperationException("The saved query v2 fixture did not deserialize any items.");
        }

        if (v1 is null)
        {
            throw new InvalidOperationException("The saved query v1 fixture did not deserialize.");
        }

        await Assert.That(v2[0].Revision).IsEqualTo("opaque-revision");
        await Assert.That(JsonSerializer.Serialize(v1, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Contains("revision", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key",
        ProjectId = "project-a"
    });

    private sealed class RecordingHandler(bool returnConflictForUpdate = false) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var requestUri = request.RequestUri
                ?? throw new InvalidOperationException("The request URI fixture is missing.");
            Requests.Add(new RecordedRequest(request.Method, requestUri.ToString(), body));

            if (returnConflictForUpdate && request.Method == HttpMethod.Put)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("{\"code\":\"TERRASCALE-1401\",\"message\":\"The saved query was modified.\"}", Encoding.UTF8, "application/json")
                };
            }

            var response = request.Method == HttpMethod.Get && requestUri.AbsolutePath.EndsWith("saved-queries", StringComparison.Ordinal)
                ? "[{\"savedQueryId\":\"saved-query-a\",\"projectId\":\"project-a\",\"name\":\"Orders\",\"query\":\"from Orders\",\"repositoryId\":null,\"createdAt\":\"2026-08-05T12:00:00+00:00\",\"updatedAt\":\"2026-08-05T13:00:00+00:00\",\"revision\":\"opaque-revision\"}]"
                : request.Method == HttpMethod.Put
                    ? "{\"savedQueryId\":\"saved-query-a\",\"projectId\":\"project-a\",\"name\":\"Recent paid orders\",\"query\":\"from Orders where Paid = true\",\"repositoryId\":null,\"createdAt\":\"2026-08-05T12:00:00+00:00\",\"updatedAt\":\"2026-08-05T13:00:00+00:00\",\"revision\":\"new-opaque-revision\"}"
                    : "{\"savedQueryId\":\"saved-query-a\",\"projectId\":\"project-a\",\"name\":\"Orders\",\"query\":\"from Orders\",\"repositoryId\":null,\"createdAt\":\"2026-08-05T12:00:00+00:00\",\"updatedAt\":\"2026-08-05T13:00:00+00:00\",\"revision\":\"opaque-revision\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Body);
}
