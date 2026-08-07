using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleProjectAccessClientTests
{
    [Test]
    public async Task TeamMemberOperations_UseEscapedRoutesTypedJsonAndNoRetries()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, "project / a");

        var page = await client.Access.ListTeamMembersAsync("project / a", skip: 2, take: 3);
        var added = await client.Access.AddTeamMemberAsync("project / a", new("user / a", "member"));
        var updated = await client.Access.UpdateTeamMemberAsync("project / a", "user / a", new("owner"));
        await client.Access.RemoveTeamMemberAsync("project / a", "user / a");

        await Assert.That(handler.Requests).Count().IsEqualTo(4);
        await Assert.That(handler.IdempotencyKeys.All(static key => key is null)).IsTrue();
        await Assert.That(handler.Requests[0]).IsEqualTo(new RecordedRequest(HttpMethod.Get, "https://api.example.invalid/api/v1/projects/project%20%2F%20a/access/team-members?skip=2&take=3", null));
        await Assert.That(handler.Requests[1]).IsEqualTo(new RecordedRequest(HttpMethod.Post, "https://api.example.invalid/api/v1/projects/project%20%2F%20a/access/team-members", "{\"userId\":\"user / a\",\"role\":\"member\"}"));
        await Assert.That(handler.Requests[2]).IsEqualTo(new RecordedRequest(HttpMethod.Patch, "https://api.example.invalid/api/v1/projects/project%20%2F%20a/access/team-members/user%20%2F%20a", "{\"role\":\"owner\"}"));
        await Assert.That(handler.Requests[3]).IsEqualTo(new RecordedRequest(HttpMethod.Delete, "https://api.example.invalid/api/v1/projects/project%20%2F%20a/access/team-members/user%20%2F%20a", null));
        await Assert.That(page.TotalCount).IsEqualTo(1);
        await Assert.That(page.Items[0].JoinedAt).IsEqualTo(DateTimeOffset.Parse("2026-08-07T12:00:00+00:00"));
        await Assert.That(added.Role).IsEqualTo("member");
        await Assert.That(updated.Role).IsEqualTo("owner");
    }

    [Test]
    public async Task TeamMemberOperations_RejectInvalidArgumentsAndConfiguredProjectConflict()
    {
        using var httpClient = new HttpClient(new RecordingHandler());
        var client = CreateClient(httpClient, "project-a");

        var conflictingProject = await Assert.ThrowsAsync<ArgumentException>(() => client.Access.ListTeamMembersAsync("project-b"));
        var invalidPage = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Access.ListTeamMembersAsync("project-a", skip: -1));
        var invalidAdd = await Assert.ThrowsAsync<ArgumentException>(() => client.Access.AddTeamMemberAsync("project-a", new(" ", "member")));
        var invalidUpdate = await Assert.ThrowsAsync<ArgumentException>(() => client.Access.UpdateTeamMemberAsync("project-a", "user-a", new(" ")));

        await Assert.That(conflictingProject).IsNotNull();
        await Assert.That(invalidPage).IsNotNull();
        await Assert.That(invalidAdd).IsNotNull();
        await Assert.That(invalidUpdate).IsNotNull();
    }

    [Test]
    public async Task TeamMemberMutations_DoNotRetryAndErrorsRemainTyped()
    {
        using var handler = new RecordingHandler(failFirstMutation: true);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, "project-a", maxRetryAttempts: 1);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.Access.AddTeamMemberAsync("project-a", new("user-a", "member")));

        await Assert.That(exception).IsNotNull();
        await Assert.That(handler.Requests).Count().IsEqualTo(1);
        await Assert.That(handler.IdempotencyKeys.All(static key => key is null)).IsTrue();
    }

    [Test]
    public async Task RemoveTeamMemberAsync_MapsPublicError()
    {
        using var handler = new RecordingHandler(deleteStatus: HttpStatusCode.Forbidden);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, "project-a");

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Access.RemoveTeamMemberAsync("project-a", "user-a"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(exception.ErrorCode).IsEqualTo("access_denied");
    }

    [Test]
    public async Task RemoveTeamMemberAsync_RequiresNoContentResponse()
    {
        using var handler = new RecordingHandler(deleteStatus: HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, "project-a");

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Access.RemoveTeamMemberAsync("project-a", "user-a"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient, string projectId, int maxRetryAttempts = 0) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key",
        ProjectId = projectId,
        MaxRetryAttempts = maxRetryAttempts,
        RetryDelay = TimeSpan.Zero,
        IdempotencyKeyFactory = static () => "configured-key"
    });

    private sealed class RecordingHandler(bool failFirstMutation = false, HttpStatusCode deleteStatus = HttpStatusCode.NoContent) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        public List<string?> IdempotencyKeys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped),
                body));
            IdempotencyKeys.Add(request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null);
            if (failFirstMutation && request.Method == HttpMethod.Post)
            {
                throw new HttpRequestException("transient failure");
            }

            if (request.Method == HttpMethod.Delete)
            {
                return deleteStatus == HttpStatusCode.NoContent
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : new HttpResponseMessage(deleteStatus)
                    {
                        Content = new StringContent("{\"code\":\"access_denied\",\"message\":\"Access denied.\"}", Encoding.UTF8, "application/json")
                    };
            }

            var response = request.Method == HttpMethod.Get
                ? "{\"items\":[{\"userId\":\"user-a\",\"email\":\"user@example.test\",\"displayName\":\"User A\",\"role\":\"member\",\"joinedAt\":\"2026-08-07T12:00:00+00:00\"}],\"totalCount\":1,\"nextCursor\":null}"
                : request.Method == HttpMethod.Patch
                    ? "{\"userId\":\"user-a\",\"email\":\"user@example.test\",\"displayName\":\"User A\",\"role\":\"owner\",\"joinedAt\":\"2026-08-07T12:00:00+00:00\"}"
                    : "{\"userId\":\"user-a\",\"email\":\"user@example.test\",\"displayName\":\"User A\",\"role\":\"member\",\"joinedAt\":\"2026-08-07T12:00:00+00:00\"}";
            return new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Body);
}
