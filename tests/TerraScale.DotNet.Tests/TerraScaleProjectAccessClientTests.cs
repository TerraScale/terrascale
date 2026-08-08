using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleProjectAccessClientTests
{
    [Test]
    public async Task TeamMemberOperations_UseEscapedRoutesTypedJsonConfiguredHeadersAndNoRetries()
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
        await Assert.That(handler.Requests[0]).IsEqualTo(new RecordedRequest(HttpMethod.Get, "https://api.example.invalid/api/v1/projects/project%20%2F%20a/access/team-members?skip=2&take=3", null, "test-key", "project / a"));
        await Assert.That(handler.Requests[1]).IsEqualTo(new RecordedRequest(HttpMethod.Post, "https://api.example.invalid/api/v1/projects/project%20%2F%20a/access/team-members", "{\"userId\":\"user / a\",\"role\":\"member\"}", "test-key", "project / a"));
        await Assert.That(handler.Requests[2]).IsEqualTo(new RecordedRequest(HttpMethod.Patch, "https://api.example.invalid/api/v1/projects/project%20%2F%20a/access/team-members/user%20%2F%20a", "{\"role\":\"owner\"}", "test-key", "project / a"));
        await Assert.That(handler.Requests[3]).IsEqualTo(new RecordedRequest(HttpMethod.Delete, "https://api.example.invalid/api/v1/projects/project%20%2F%20a/access/team-members/user%20%2F%20a", null, "test-key", "project / a"));
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

    [Test]
    public async Task TeamMemberResponses_RequireEveryDescriptorFieldAndAllowExplicitNullJoinedAt()
    {
        string[] requiredFields = ["userId", "email", "displayName", "role", "joinedAt"];
        foreach (var requiredField in requiredFields)
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Patch })
            {
                using var handler = new MalformedMemberResponseHandler(method, requiredField);
                using var httpClient = new HttpClient(handler);
                var client = CreateClient(httpClient, "project-a");

                var exception = method == HttpMethod.Get
                    ? await Assert.ThrowsAsync<JsonException>(() => client.Access.ListTeamMembersAsync("project-a"))
                    : method == HttpMethod.Post
                        ? await Assert.ThrowsAsync<JsonException>(() => client.Access.AddTeamMemberAsync("project-a", new("user-a", "member")))
                        : await Assert.ThrowsAsync<JsonException>(() => client.Access.UpdateTeamMemberAsync("project-a", "user-a", new("member")));

                await Assert.That(exception).IsNotNull();
            }
        }

        using var nullHandler = new MalformedMemberResponseHandler(HttpMethod.Get, excludedField: null, joinedAtIsNull: true);
        using var nullHttpClient = new HttpClient(nullHandler);
        var nullClient = CreateClient(nullHttpClient, "project-a");
        var page = await nullClient.Access.ListTeamMembersAsync("project-a");

        await Assert.That(page.Items[0].JoinedAt).IsNull();
    }

    [Test]
    public async Task TeamMemberPageResponse_RequiresItemsTotalCountAndNextCursor()
    {
        foreach (var requiredField in new[] { "items", "totalCount", "nextCursor" })
        {
            using var handler = new MalformedPageResponseHandler(requiredField);
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(httpClient, "project-a");

            var exception = await Assert.ThrowsAsync<JsonException>(() => client.Access.ListTeamMembersAsync("project-a"));

            await Assert.That(exception).IsNotNull();
        }
    }

    [Test]
    public async Task TeamMemberResponses_RejectExplicitNullForRequiredStringsAndItems()
    {
        foreach (var requiredField in new[] { "userId", "email", "displayName", "role" })
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Patch })
            {
                using var handler = new MalformedMemberResponseHandler(method, excludedField: null, nullField: requiredField);
                using var httpClient = new HttpClient(handler);
                var client = CreateClient(httpClient, "project-a");

                var exception = method == HttpMethod.Get
                    ? await Assert.ThrowsAsync<JsonException>(() => client.Access.ListTeamMembersAsync("project-a"))
                    : method == HttpMethod.Post
                        ? await Assert.ThrowsAsync<JsonException>(() => client.Access.AddTeamMemberAsync("project-a", new("user-a", "member")))
                        : await Assert.ThrowsAsync<JsonException>(() => client.Access.UpdateTeamMemberAsync("project-a", "user-a", new("member")));

                await Assert.That(exception).IsNotNull();
            }
        }

        using var itemsHandler = new NullItemsPageResponseHandler();
        using var itemsHttpClient = new HttpClient(itemsHandler);
        var itemsClient = CreateClient(itemsHttpClient, "project-a");
        var itemsException = await Assert.ThrowsAsync<JsonException>(() => itemsClient.Access.ListTeamMembersAsync("project-a"));

        await Assert.That(itemsException).IsNotNull();
    }

    [Test]
    public async Task TeamMemberModels_PreservePositionalConstructorsAndDeconstruction()
    {
        var member = new TerraScaleProjectTeamMember(
            "user-a",
            "user@example.test",
            "User A",
            "member",
            DateTimeOffset.UnixEpoch);
        var page = new TerraScaleProjectTeamMemberPage([member], 1, null);
        var (userId, email, displayName, role, joinedAt) = member;
        var (items, totalCount, nextCursor) = page;

        await Assert.That(userId).IsEqualTo("user-a");
        await Assert.That(email).IsEqualTo("user@example.test");
        await Assert.That(displayName).IsEqualTo("User A");
        await Assert.That(role).IsEqualTo("member");
        await Assert.That(joinedAt).IsEqualTo(DateTimeOffset.UnixEpoch);
        await Assert.That(items[0]).IsEqualTo(member);
        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(nextCursor).IsNull();
    }

    [Test]
    public async Task TeamMemberModels_PreserveRequiredPropertyObjectInitializerConstruction()
    {
        var member = new TerraScaleProjectTeamMember
        {
            UserId = "user-a",
            Email = "user@example.test",
            DisplayName = "User A",
            Role = "member",
            JoinedAt = null
        };
        var page = new TerraScaleProjectTeamMemberPage
        {
            Items = [member],
            TotalCount = 1,
            NextCursor = null
        };

        await Assert.That(page.Items[0]).IsEqualTo(member);
        await Assert.That(page.TotalCount).IsEqualTo(1);
        await Assert.That(member.JoinedAt).IsNull();
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
                body,
                Header(request, "X-TerraScale-Auth-Key"),
                Header(request, "X-TerraScale-Project-Id")));
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

    private sealed class MalformedMemberResponseHandler(
        HttpMethod expectedMethod,
        string? excludedField,
        bool joinedAtIsNull = false,
        string? nullField = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != expectedMethod)
            {
                throw new InvalidOperationException("Unexpected request method.");
            }

            var member = MemberJson(excludedField, joinedAtIsNull, nullField);
            var response = request.Method == HttpMethod.Get
                ? $"{{\"items\":[{member}],\"totalCount\":1,\"nextCursor\":null}}"
                : member;
            return Task.FromResult(new HttpResponseMessage(
                request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MalformedPageResponseHandler(string excludedField) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fields = new[]
            {
                ("items", "\"items\":[{\"userId\":\"user-a\",\"email\":\"user@example.test\",\"displayName\":\"User A\",\"role\":\"member\",\"joinedAt\":null}]"),
                ("totalCount", "\"totalCount\":1"),
                ("nextCursor", "\"nextCursor\":null")
            };
            var response = $"{{{string.Join(',', fields.Where(field => field.Item1 != excludedField).Select(field => field.Item2))}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class NullItemsPageResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":null,\"totalCount\":1,\"nextCursor\":null}", Encoding.UTF8, "application/json")
            });
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static string MemberJson(string? excludedField, bool joinedAtIsNull, string? nullField = null) =>
        $"{{{string.Join(',', new[]
        {
            ("userId", nullField == "userId" ? "\"userId\":null" : "\"userId\":\"user-a\""),
            ("email", nullField == "email" ? "\"email\":null" : "\"email\":\"user@example.test\""),
            ("displayName", nullField == "displayName" ? "\"displayName\":null" : "\"displayName\":\"User A\""),
            ("role", nullField == "role" ? "\"role\":null" : "\"role\":\"member\""),
            ("joinedAt", joinedAtIsNull ? "\"joinedAt\":null" : "\"joinedAt\":\"2026-08-07T12:00:00+00:00\"")
        }.Where(field => field.Item1 != excludedField).Select(field => field.Item2))}}}";

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Body, string? ApiKey, string? ProjectId);
}
