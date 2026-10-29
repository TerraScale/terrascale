using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Databases;

public sealed class DatabaseBackendContractTests
{
    [Test]
    [Arguments("list", "databases:read", false)]
    [Arguments("create", "databases:write", false)]
    [Arguments("branch", "databases:write", true)]
    [Arguments("delete", "databases:write", true)]
    [Arguments("set-regions", "databases:write", true)]
    [Arguments("set-all-regions", "databases:write", true)]
    [Arguments("collections", "databases:read", true)]
    [Arguments("sql-catalog", "documents:read", true)]
    [Arguments("sql", "documents:read", true)]
    [Arguments("sql-write", "documents:write", true)]
    [Arguments("indexes", "databases:read", true)]
    [Arguments("create-index", "databases:write", true)]
    [Arguments("drop-index", "databases:write", true)]
    public async Task EveryActionRefusesWithExactAuthorityBeforeItsProducerRequest(string action, string permission, bool resolvesName)
    {
        foreach (var status in new[] { 401, 403, 404, 429, 503, 0 })
            foreach (var refusalAt in resolvesName ? new[] { 1, 2 } : new[] { 1 })
            {
                using var wire = new Wire();
                var scope = Scope();
                var tokens = new Tokens(scope, status, refusalAt);
                var backend = new DatabaseBackend(wire, tokens);
                var home = DatabaseRegion.FromId("gru")!;
                HttpRequestException? refused = null;
                try
                {
                    Task request = action switch
                    {
                        "list" => backend.ListAsync(scope, home, CancellationToken.None),
                        "create" => backend.CreateAsync(scope, "new-app", [home], false, CancellationToken.None),
                        "branch" => backend.BranchAsync(scope, home, DatabaseId, "branch", CancellationToken.None),
                        "delete" => backend.DeleteAsync(scope, home, DatabaseId, CancellationToken.None),
                        "set-regions" => backend.SetRegionsAsync(scope, home, DatabaseId, [home], false, CancellationToken.None),
                        "set-all-regions" => backend.SetRegionsAsync(scope, home, DatabaseId, [home], true, CancellationToken.None),
                        "collections" => backend.CollectionsAsync(scope, home, DatabaseId, CancellationToken.None),
                        "sql-catalog" => backend.SqlTablesAsync(scope, home, DatabaseId, CancellationToken.None),
                        "sql" => backend.SqlAsync(scope, home, DatabaseId, "SELECT * FROM items", false, CancellationToken.None),
                        "sql-write" => backend.SqlAsync(scope, home, DatabaseId, "DELETE FROM items", true, CancellationToken.None),
                        "indexes" => backend.IndexesAsync(scope, home, DatabaseId, CancellationToken.None),
                        "create-index" => backend.CreateIndexAsync(scope, home, DatabaseId, "items", ["title"], CancellationToken.None),
                        "drop-index" => backend.DropIndexAsync(scope, home, DatabaseId, "items-title", CancellationToken.None),
                        _ => throw new InvalidOperationException("Unknown test action.")
                    };
                    await request;
                }
                catch (HttpRequestException error) { refused = error; }
                await Assert.That(refused).IsNotNull();
                await Assert.That(refused is DocumentWriteOutcomeUnknownException).IsFalse();
                await Assert.That(refused!.StatusCode).IsEqualTo(status == 0 ? (HttpStatusCode?)null : (HttpStatusCode)status);
                var expected = resolvesName
                    ? refusalAt == 1 ? new[] { "databases:read" } : new[] { "databases:read", permission }
                    : new[] { permission };
                await Assert.That(tokens.Scopes.SequenceEqual(expected)).IsTrue();
                // ID resolution is one canonical read; a refused action never sends its own data request.
                await Assert.That(wire.Resolutions).IsEqualTo(refusalAt - 1);
                await Assert.That(wire.ActionRequests).IsEqualTo(0);
            }
    }

    private const string ProjectId = "01JA0000000000000000000001";
    private const string OrganizationId = "01JA0000000000000000000002";
    private const string DatabaseId = "01JA0000000000000000000004";
    private static DatabaseScope Scope() => new("user", "original-user-token", OrganizationId, "org", ProjectId, "project")
    { Session = new("user", "Synthetic", "synthetic@example.test", "original-user-token", null, DateTimeOffset.UtcNow.AddMinutes(5)) };

    private sealed class Tokens(DatabaseScope scope, int status, int refusalAt) : IProjectAccessTokens
    {
        public List<string> Scopes { get; } = [];
        public Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(session, scope.Session) || organizationId != OrganizationId || projectId != ProjectId ||
                session.AccessToken != "original-user-token" || cancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("Database authority changed.");
            // A SQL write asks for read and write together; every other action asks for exactly one scope.
            Scopes.Add(scopes.Count == 2 && scopes[0] == "documents:read" ? scopes[1] : scopes.Single());
            if (Scopes.Count == refusalAt) throw new HttpRequestException("Synthetic exchange refusal.", null, status == 0 ? null : (HttpStatusCode)status);
            return Task.FromResult("resolution-token");
        }
    }

    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public int Resolutions { get; private set; }
        public int ActionRequests { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != "/v1/projects/" + ProjectId + "/databases")
            {
                ActionRequests++;
                throw new InvalidOperationException("A refused action sent a data request.");
            }
            if (request.Headers.Authorization?.Parameter != "resolution-token" || request.Headers.GetValues("X-TerraScale-Region-Pin").Single() != "gru")
                throw new InvalidOperationException("Canonical name resolution changed authority or region.");
            Resolutions++;
            var body = JsonSerializer.Serialize(new
            {
                items = new[] { new { id = DatabaseId, projectId = ProjectId, name = "app", branchedFrom = (string?)null, createdAt = DateTimeOffset.UnixEpoch, region = "gru", homeRegion = "gru", replicaRegions = Array.Empty<string>(), allRegions = false } },
                next = (string?)null
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
