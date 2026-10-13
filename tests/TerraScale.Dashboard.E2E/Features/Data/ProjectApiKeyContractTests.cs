using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.ApiKeys;

namespace TerraScale.Dashboard.E2E.Features.Data;

public sealed class ProjectApiKeyContractTests
{
    private const string OrganizationId = "01JA0000000000000000000002";
    private const string ProjectId = "01JA0000000000000000000001";
    private const string KeyId = "01JA0000000000000000000003";
    private const string DatabaseId = "01JA0000000000000000000004";

    [Test]
    public async Task IssuanceUsesOriginalUserBearerAndCanonicalRestrictionsWithoutPersistingSecret()
    {
        using var wire = new Wire();
        var context = Context();
        var service = new ApiKeyService(wire, TimeProvider.System);
        var created = await service.CreateAsync(context, "api-key", "Synthetic", DatabaseId,
            ["database:read", "database:write"], CancellationToken.None);
        using var request = JsonDocument.Parse(wire.Body!);
        await Assert.That(request.RootElement.GetProperty("database").GetString()).IsEqualTo("app");
        await Assert.That(request.RootElement.GetProperty("scopes").EnumerateArray().Select(item => item.GetString()))
            .IsEquivalentTo(new[] { "documents:read", "documents:write" });
        await Assert.That(created.Secret).IsEqualTo(KeyId + "_synthetic-secret");
        await Assert.That(created.Key.LastFour).IsEqualTo("");
        await Assert.That(ApiKeyService.Metadata(created.Key).DatabaseName).IsEqualTo("app");
        var listed = await service.ListAsync(context, CancellationToken.None);
        await Assert.That(wire.Query).IsEqualTo("?limit=100");
        await Assert.That(listed.Single().Id).IsEqualTo(KeyId);
        await Assert.That(JsonSerializer.Serialize(listed).Contains(created.Secret, StringComparison.Ordinal)).IsFalse();
        await Assert.That(wire.Requests).IsEqualTo(2);
    }

    [Test]
    public async Task RestrictionSurvivesMissingLocalDatabaseAndRevokeVerifiesKeyBeforeDelete()
    {
        using var wire = new Wire();
        var context = Context();
        context.Project!.Databases.Clear();
        var service = new ApiKeyService(wire, TimeProvider.System);
        var key = (await service.ListAsync(context, CancellationToken.None)).Single();
        await Assert.That(key.DatabaseId).IsNull();
        await Assert.That(ApiKeyService.CredentialMetadata(key).DatabaseName).IsEqualTo("app");
        await service.RevokeAsync(context, KeyId, CancellationToken.None);
        await Assert.That(wire.Methods).IsEquivalentTo(new[] { HttpMethod.Get, HttpMethod.Get, HttpMethod.Delete }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("foreign")]
    [Arguments("repeated")]
    public async Task ListingRefusesForeignIdentityOrRepeatedPagination(string failure)
    {
        using var wire = new Wire { Failure = failure };
        HttpRequestException? rejected = null;
        try { await new ApiKeyService(wire, TimeProvider.System).ListAsync(Context(), CancellationToken.None); }
        catch (HttpRequestException error) { rejected = error; }
        await Assert.That(rejected).IsNotNull();
        await Assert.That(wire.Requests).IsEqualTo(failure == "foreign" ? 1 : 2);
        await Assert.That(wire.Methods.All(method => method == HttpMethod.Get)).IsTrue();
    }

    private static AccountContext Context()
    {
        var project = new ProjectRecord { Id = ProjectId, OrganizationId = OrganizationId, Name = "project" };
        project.Databases.Add(DatabaseId, new() { Id = DatabaseId, ProjectId = ProjectId, Name = "app" });
        var organization = new OrganizationRecord { Id = OrganizationId, Name = "org", OwnerUserId = "user" };
        organization.Members.Add("user", new() { UserId = "user", Role = "owner", Status = "active" });
        organization.Projects.Add(ProjectId, project);
        return new(new("user", "Synthetic", "synthetic@example.test", "original-user-token", null, DateTimeOffset.UtcNow.AddMinutes(5)),
            new() { UserId = "user" }, [organization], organization, project);
    }

    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public string Failure { get; init; } = "";
        public int Requests { get; private set; }
        public string? Body { get; private set; }
        public string? Query { get; private set; }
        public List<HttpMethod> Methods { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var root = $"/v1/terrascale/organizations/{OrganizationId}/projects/{ProjectId}/api-keys";
            if (request.RequestUri!.Host != "auth.terrascale.tech" || request.Headers.Authorization?.Parameter != "original-user-token" ||
                request.RequestUri.AbsolutePath != root && request.RequestUri.AbsolutePath != root + "/" + KeyId)
                throw new InvalidOperationException("Keys must use the original user authority at ts-auth's project boundary.");
            Requests++;
            Methods.Add(request.Method);
            Query = request.RequestUri.Query;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            var key = new
            {
                id = KeyId,
                name = "Synthetic",
                organization_id = OrganizationId,
                project_id = Failure == "foreign" ? "01JA0000000000000000000005" : ProjectId,
                database = "app",
                scopes = new[] { "documents:read", "documents:write" },
                created_by = "user",
                created_at = "2026-01-01T00:00:00+00:00",
                expires_at = (string?)null,
                previous_secret_expires_at = (string?)null
            };
            var response = request.Method == HttpMethod.Post
                ? JsonSerializer.Serialize(new { api_key = key, key = KeyId + "_synthetic-secret" })
                : request.RequestUri.AbsolutePath == root ? JsonSerializer.Serialize(new { items = new[] { key }, next = Failure == "repeated" ? "opaque+/cursor=" : null })
                : JsonSerializer.Serialize(key);
            return new(request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
