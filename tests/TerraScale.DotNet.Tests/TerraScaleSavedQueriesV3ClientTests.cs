using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleSavedQueriesV3ClientTests
{
    [Test]
    public async Task SavedQueryV3List_SendsOpaqueCursorAndDeserializesSnapshotPage()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"items":[{"savedQueryId":"saved-query-a","projectId":"project-a","name":"Orders","query":"from Orders","repositoryId":null,"createdAt":"2026-08-05T12:00:00+00:00","updatedAt":"2026-08-05T13:00:00+00:00","revision":"opaque-revision"}],"nextCursor":"opaque+cursor/="}
            """);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var page = await client.SavedQueriesV3.ListAsync("project-a", "opaque+cursor/=", take: 3);

        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v3/projects/project-a/saved-queries?cursor=opaque%2Bcursor%2F%3D&take=3"));
        await Assert.That(page.Items[0].Revision).IsEqualTo("opaque-revision");
        await Assert.That(page.NextCursor).IsEqualTo("opaque+cursor/=");
    }

    [Test]
    public async Task SavedQueryV3List_UsesDefaultTakeAllowsNullCursorAndRejectsOutOfRangeTake()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, """{"items":[],"nextCursor":null}""");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var page = await client.SavedQueriesV3.ListAsync("project-a");
        var tooSmall = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SavedQueriesV3.ListAsync("project-a", take: 0));
        var tooLarge = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SavedQueriesV3.ListAsync("project-a", take: 501));

        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v3/projects/project-a/saved-queries?take=100"));
        await Assert.That(page.NextCursor).IsNull();
        await Assert.That(tooSmall).IsNotNull();
        await Assert.That(tooLarge).IsNotNull();
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest)]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.Conflict)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    public async Task SavedQueryV3List_MapsPublicErrors(HttpStatusCode statusCode)
    {
        using var handler = new RecordingHandler(statusCode, """{"code":"TERRASCALE-1400","message":"Saved-query snapshot unavailable."}""");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.SavedQueriesV3.ListAsync("project-a"));

        if (exception is null)
        {
            throw new InvalidOperationException("The saved query v3 error fixture did not produce an API exception.");
        }

        await Assert.That(exception.StatusCode).IsEqualTo(statusCode);
        await Assert.That(exception.ErrorCode).IsEqualTo("TERRASCALE-1400");
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key",
        ProjectId = "project-a"
    });

    private sealed class RecordingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri
                ?? throw new InvalidOperationException("The request URI fixture is missing.");
            Request = new RecordedRequest(request.Method, requestUri.ToString());
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri);
}
