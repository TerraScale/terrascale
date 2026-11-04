using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E.Features.Usage
{
    public sealed class ReplicationHealthContractTests
    {
        [Test]
        public async Task ATerraBaseWithoutReplicationReportingStaysUnreportedInsteadOfStopped()
        {
            using var wire = new Wire { Failure = 404 };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var result = await new UsageReplicationBackend(wireRpc.Client, new Tokens(), new Regions()).ReadAsync(Selection(), CancellationToken.None);
            await Assert.That(result.Error).IsEqualTo("");
            await Assert.That(result.Regions).IsEmpty();
            await Assert.That(result.WorstLagMs).IsNull();
            await Assert.That(result.Empty).Contains("has not reported replication").Because("a missing producer is a pending state, never a stopped classification");
        }

        [Test]
        public async Task UnobservedRegionsClassifyAsStartingWhileObservedOnesKeepTheirHealth()
        {
            using var wire = new Wire
            {
                Rows =
                [
                    ("gru", "healthy", 0d, true),
                    ("fra", "lagging", 2500d, false),
                    ("sjc", "stopped", null, null)
                ]
            };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var result = await new UsageReplicationBackend(wireRpc.Client, new Tokens(), new Regions()).ReadAsync(Selection(), CancellationToken.None);
            await Assert.That(result.Error).IsEqualTo("");
            var byRegion = result.Regions.ToDictionary(row => row.Region, StringComparer.OrdinalIgnoreCase);
            await Assert.That(byRegion["gru"].Status).IsEqualTo("healthy");
            await Assert.That(byRegion["gru"].Starting).IsFalse();
            await Assert.That(byRegion["fra"].Status).IsEqualTo("lagging");
            await Assert.That(byRegion["fra"].Starting).IsFalse();
            await Assert.That(byRegion["fra"].LagMs).IsEqualTo(2500d);
            await Assert.That(byRegion["sjc"].Starting).IsTrue().Because("a region with no observation is starting, not stopped");
            await Assert.That(result.WorstLagMs).IsEqualTo(2500d).Because("worst lag comes only from measured regions");
        }

        private static UsageSelection Selection()
        {
            var project = new ProjectRecord { Id = "01JA0000000000000000000001", OrganizationId = "01JA0000000000000000000002", Name = "project" };
            var database = new DatabaseRecord { Id = "01JA0000000000000000000004", ProjectId = project.Id, Name = "app" };
            database.Regions = ["GRU", "FRA", "SJC"];
            project.Databases.Add(database.Id, database);
            var organization = new OrganizationRecord { Id = project.OrganizationId };
            organization.Projects.Add(project.Id, project);
            var session = new SessionRecord("user", "Synthetic", "synthetic@example.test", "token", null, DateTimeOffset.Parse("2026-10-12T00:20:00Z", CultureInfo.InvariantCulture));
            var context = new AccountContext(session, new() { UserId = "user" }, [organization], organization, project);
            return new(context, "1h", "", true);
        }

        private sealed class Tokens : IProjectAccessTokens
        {
            public Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
            {
                return Task.FromResult("meter-project-token");
            }
        }

        private sealed class Regions : IDatabaseBackend
        {
            public Task<IReadOnlyList<DatabaseRegion>> RegionsAsync(CancellationToken cancellationToken)
            {
                IReadOnlyList<DatabaseRegion> catalog = [new("gru", "gru", "São Paulo", "br", new("https://gru.api.terrascale.tech"), true),
                    new("fra", "fra", "Frankfurt", "de", new("https://fra.api.terrascale.tech"), true),
                    new("sjc", "sjc", "San Jose", "us", new("https://sjc.api.terrascale.tech"), true)];
                return Task.FromResult(catalog);
            }

            public Task<IReadOnlyList<DatabaseRegion>> CatalogAsync(CancellationToken cancellationToken) => RegionsAsync(cancellationToken);
            public Task<IReadOnlyList<ManagedDatabase>> ListAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<ManagedDatabase> CreateAsync(DatabaseScope scope, string name, DatabaseRegion home, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<ManagedDatabase> BranchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<ManagedDatabase> RenameAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task DeleteAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<IReadOnlyList<CollectionStats>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<DatabaseSqlCatalog> SqlTablesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<DatabaseSqlResult> SqlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string statement, bool write, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<IReadOnlyList<DatabaseIndex>> IndexesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<DatabaseIndex> CreateIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string collection, IReadOnlyList<string> fields, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task DropIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken) => throw new InvalidOperationException();
        }

        private sealed class Wire : HttpMessageHandler, IHttpClientFactory
        {
            public int Failure { get; init; }
            public (string Region, string Status, double? Lag, bool? Divergence)[] Rows { get; init; } = [];
            public HttpClient CreateClient(string name)
            {
                return new(this, false);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path != "/v1/projects/01JA0000000000000000000001/replication" || request.Headers.Authorization?.Parameter != "meter-project-token")
                {
                    throw new InvalidOperationException("Noncanonical replication request.");
                }

                if (Failure > 0)
                {
                    return Task.FromResult(Response((HttpStatusCode)Failure, "{\"error\":{\"code\":\"not_found\",\"message\":\"absent\"}}"));
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var end = DateTimeOffset.FromUnixTimeSeconds((now / 60 + 1) * 60);
                var first = end.AddHours(-1);
                var items = Rows.Select(row => new
                {
                    databaseId = "01JA0000000000000000000004",
                    databaseName = "app",
                    region = row.Region,
                    status = row.Status,
                    replicatedOps = 12L,
                    lagMs = row.Lag,
                    divergence = row.Divergence,
                    convergenceMs = (double?)null,
                    lastApplied = (DateTimeOffset?)null,
                    bandwidth = Array.Empty<object>()
                }).ToArray();
                var value = JsonSerializer.Serialize(new { projectId = "01JA0000000000000000000001", range = "1h", from = first, to = end, bucketSeconds = 60, items });
                return Task.FromResult(Response(HttpStatusCode.OK, value));
            }

            private static HttpResponseMessage Response(HttpStatusCode status, string body)
            {
                return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }
    }
}
