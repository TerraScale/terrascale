using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    // Platform admins read TerraScale's own databases through TerraBase's explore API with their own access token; nothing
    // else reaches TerraBase for the platform organization, and the dashboard treats its members as viewers.
    public sealed class PlatformDatabaseContractTests
    {
        private const string Database = "00000000000000000000000003";

        [Test]
        public async Task ReadsUseExploreWithTheUserTokenAndWritesNeverLeave()
        {
            using var wire = new Wire();
            var tokens = new Tokens();
            var documents = new DocumentBackend(wire, tokens);
            var home = DatabaseRegion.FromId("gru")!;

            var collections = await documents.CollectionsAsync(Scope(), home, "ts-auth", CancellationToken.None);
            await Assert.That(collections.Single().Name).IsEqualTo("users");
            await Assert.That(wire.Paths[^1]).IsEqualTo("/v1/platform/databases/ts-auth/collections");

            var page = await documents.ListAsync(Scope(), home, "ts-auth", "users", 25, null, CancellationToken.None);
            await Assert.That(page.Items.Single().Document.GetProperty("passwordHash").GetString()).IsEqualTo("[redacted]");
            await Assert.That(wire.Paths[^1]).IsEqualTo("/v1/platform/databases/ts-auth/collections/users/documents?limit=25");
            await Assert.That(wire.Bearers.Distinct()).IsEquivalentTo(new[] { "user-token" });

            using var body = JsonDocument.Parse("{}");
            var requests = wire.Paths.Count;
            await Assert.That(async () => await documents.WriteAsync(Scope(), home, "ts-auth", "users", "replace", "a", body.RootElement, "", CancellationToken.None))
                .Throws<DatabaseProviderException>();
            await Assert.That(async () => await new DatabaseBackend(wire, tokens).CollectionsAsync(Scope(), home, Database, CancellationToken.None))
                .Throws<DatabaseProviderException>();
            await Assert.That(async () => await documents.ListAsync(Scope(), home, "customer-db", "users", 25, null, CancellationToken.None))
                .Throws<DatabaseProviderException>();
            await Assert.That(wire.Paths.Count).IsEqualTo(requests);
            await Assert.That(tokens.Calls).IsEqualTo(0);
        }

        [Test]
        public async Task PlatformMembersAreViewersAndOtherDatabasesAreForbidden()
        {
            var owner = new MemberRecord { UserId = "user", Role = "owner" };
            var platform = new OrganizationRecord { Id = PlatformDatabases.OrganizationId, Members = { ["user"] = owner } };
            var customer = new OrganizationRecord { Id = "01JA0000000000000000000002", Members = { ["user"] = owner } };
            await Assert.That(platform.RoleFor("user")).IsEqualTo("viewer");
            await Assert.That(platform.CanWrite("user")).IsFalse();
            await Assert.That(customer.RoleFor("user")).IsEqualTo("owner");
        }

        [Test]
        public async Task ExploreHydrationPreservesAllRegionsPlacement()
        {
            using var wire = new Wire();
            using var http = wire.CreateClient("");
            var project = new ProjectRecord { Id = "00000000000000000000000002" };
            var organization = new OrganizationRecord { Id = PlatformDatabases.OrganizationId, Projects = { [project.Id] = project } };
            await PlatformDatabases.HydrateAsync(organization, http, Scope().Session!, CancellationToken.None);
            var database = project.Databases[Database];
            await Assert.That(database.AllRegions).IsTrue();
            await Assert.That(database.Region).IsEqualTo("gru");
            var regions = new[] { DatabaseRegion.FromId("gru")!, DatabaseRegion.FromId("fra")!, DatabaseRegion.FromId("sjc")! };
            await Assert.That(DatabaseService.ServingRegions(database, regions).Select(region => region.ToLowerInvariant())).IsEquivalentTo(new[] { "gru", "fra", "sjc" });
            await Assert.That(wire.Bearers.Single()).IsEqualTo("user-token");
        }

        private static DatabaseScope Scope()
        {
            return new("user", "user-token", PlatformDatabases.OrganizationId, "TerraScale", "00000000000000000000000002", "ts-auth")
            { Session = new("user", "Owner", "owner@example.test", "user-token", null, DateTimeOffset.UtcNow.AddMinutes(5)) };
        }

        private sealed class Tokens : IProjectAccessTokens
        {
            public int Calls { get; private set; }
            public Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult("project-token");
            }
        }

        private sealed class Wire : HttpMessageHandler, IHttpClientFactory
        {
            public List<string> Paths { get; } = [];
            public List<string> Bearers { get; } = [];
            public HttpClient CreateClient(string name)
            {
                return new(this, false);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Paths.Add(request.RequestUri!.PathAndQuery);
                Bearers.Add(request.Headers.Authorization!.Parameter!);
                var json = request.RequestUri.AbsolutePath == "/v1/platform/databases"
                    ? "{\"items\":[{\"id\":\"00000000000000000000000003\",\"name\":\"ts-auth\",\"homeRegion\":\"gru\",\"replicaRegions\":[],\"allRegions\":true}]}"
                    : request.RequestUri.AbsolutePath.EndsWith("/collections", StringComparison.Ordinal)
                    ? "{\"items\":[{\"name\":\"users\",\"created_at\":\"1700000000000\",\"id\":\"c1\"}],\"next_cursor\":null}"
                    : "{\"items\":[{\"id\":\"u1\",\"version\":\"tbv2-1\",\"state\":\"value\",\"document\":{\"email\":\"a@example.test\",\"passwordHash\":\"[redacted]\"}}],\"next_cursor\":null}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
        }
    }
}
