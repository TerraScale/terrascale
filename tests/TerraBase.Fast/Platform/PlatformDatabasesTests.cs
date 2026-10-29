using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

/// <summary>
/// Every TerraBase start seeds TerraScale's own databases (ts-auth, ts-dashboard) under fixed ids, in the
/// background, idempotently: restarts, concurrent runs and independent regions all converge on the same records. The internal
/// operator and correctly scoped global-issuer tokens reach them; other projects and catalog members cannot.
/// </summary>
public sealed class PlatformDatabasesTests
{
    private static readonly string[] Regions = ["gru", "sjc", "fra"];

    private static readonly string Auth = $"/v1/projects/{PlatformDatabases.AuthProjectId}/databases/{PlatformDatabases.Auth}";
    private static readonly string Dashboard = $"/v1/projects/{PlatformDatabases.DashboardProjectId}/databases/{PlatformDatabases.Dashboard}";

    [Test]
    public async Task Every_start_seeds_both_databases_and_the_operator_stores_in_them_without_a_credential()
    {
        await using var server = await TerraBaseTestServer.StartAsync(Seeding);
        await SeededAsync(server);
        using var storage = PeerTls.CreateClient(server.BaseAddress);

        using var collection = await storage.PostAsJsonAsync(ManagementApi.Uri(Auth + "/collections"), new { name = "users" });
        using var written = await storage.PutAsJsonAsync(ManagementApi.Uri(Auth + "/collections/users/documents/by-key?id=" + ManagementApi.DocumentId("ada")), new { name = "Ada" });
        using var read = await storage.GetAsync(ManagementApi.Uri(Auth + "/collections/users/documents/by-key?id=" + ManagementApi.DocumentId("ada")));
        using var dashboard = await storage.GetAsync(ManagementApi.Uri(Dashboard + "/collections"));

        await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(written.IsSuccessStatusCode).IsTrue();
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("name").GetString()).IsEqualTo("Ada");
        await Assert.That(dashboard.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Each_named_project_owns_exactly_its_database_and_the_other_project_cannot_resolve_it()
    {
        await using var server = await TerraBaseTestServer.StartAsync(Seeding);
        await SeededAsync(server);
        using var storage = PeerTls.CreateClient(server.BaseAddress);

        // These are the shared contracts consumed by auth and dashboard through ts-dotnet.
        using var auth = await storage.GetAsync(ManagementApi.Uri("/v1/projects/00000000000000000000000002/databases/ts-auth/collections"));
        using var dashboard = await storage.GetAsync(ManagementApi.Uri("/v1/projects/00000000000000000000000005/databases/ts-dashboard/collections"));
        using var wrongAuth = await storage.GetAsync(ManagementApi.Uri("/v1/projects/00000000000000000000000005/databases/ts-auth/collections"));
        using var wrongDashboard = await storage.GetAsync(ManagementApi.Uri("/v1/projects/00000000000000000000000002/databases/ts-dashboard/collections"));

        await Assert.That(auth.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(dashboard.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(wrongAuth.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(wrongDashboard.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var records = await RecordsAsync(server);
        await Assert.That(records.Single(record => record.Name == "ts-auth").Id).IsEqualTo("00000000000000000000000003");
        await Assert.That(records.Single(record => record.Name == "ts-dashboard").Id).IsEqualTo("00000000000000000000000004");
    }

    [Test]
    public async Task Restarts_and_concurrent_runs_keep_one_set_of_records_and_the_data()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-platform-").FullName;
        try
        {
            IReadOnlyList<Seeded> first;
            await using (var server = await TerraBaseTestServer.StartAsync(settings =>
            {
                Seeding(settings);
                settings["data-directory"] = directory;
            }))
            {
                await SeededAsync(server);
                first = await RecordsAsync(server);
                using var storage = PeerTls.CreateClient(server.BaseAddress);
                using var collection = await storage.PostAsJsonAsync(ManagementApi.Uri(Auth + "/collections"), new { name = "signing_keys" });
                using var written = await storage.PutAsJsonAsync(ManagementApi.Uri(Auth + "/collections/signing_keys/documents/by-key?id=" + ManagementApi.DocumentId("k1")), new { kid = "k1" });
                await Assert.That(written.IsSuccessStatusCode).IsTrue();

                // Eight more runs at once on a seeded node: every one succeeds and changes nothing.
                var catalog = server.Services.GetRequiredService<IPlatformCatalog>();
                var runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => catalog.EnsurePlatformAsync(CancellationToken.None).AsTask()));
                await Assert.That(runs.All(run => run is Done)).IsTrue();
                await Assert.That(await RecordsAsync(server)).IsEquivalentTo(first);
            }

            await using (var restarted = await TerraBaseTestServer.StartAsync(settings =>
            {
                Seeding(settings);
                settings["data-directory"] = directory;
            }))
            {
                await SeededAsync(restarted);
                using var storage = PeerTls.CreateClient(restarted.BaseAddress);
                using var kept = await storage.GetAsync(ManagementApi.Uri(Auth + "/collections/signing_keys/documents/by-key?id=" + ManagementApi.DocumentId("k1")));

                await Assert.That(await RecordsAsync(restarted)).IsEquivalentTo(first);
                await Assert.That(kept.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Regions_seeding_at_the_same_time_write_the_same_ids_with_local_placement()
    {
        var servers = await Task.WhenAll(Regions.Select(region => TerraBaseTestServer.StartAsync(settings =>
        {
            Seeding(settings);
            settings["region"] = region;
        })));
        try
        {
            await Task.WhenAll(servers.Select(SeededAsync));
            var records = await Task.WhenAll(servers.Select(RecordsAsync));

            for (var index = 0; index < records.Length; index++)
            {
                var local = records[index];
                await Assert.That(local).IsEquivalentTo(records[0].Select(record => record with { HomeRegion = Regions[index] }));

                foreach (var database in local)
                {
                    await Assert.That(database.Id).IsEqualTo(PlatformDatabases.Databases[database.Name].Id);
                    await Assert.That(database.HomeRegion).IsEqualTo(Regions[index]);
                    await Assert.That(database.Replicas).IsEqualTo("");
                    await Assert.That(database.AllRegions).IsFalse();
                }
            }
        }
        finally
        {
            await Task.WhenAll(servers.Select(server => server.DisposeAsync().AsTask()));
        }
    }

    [Test]
    public async Task Customer_credentials_never_reach_the_platform_databases()
    {
        await using var api = await ManagementApi.StartAsync(Seeding);
        await SeededAsync(api.Server);
        var organization = await api.CreateOrganizationAsync("customer");
        using var customerKey = await api.CreateClientAsync(organization, "documents:read", "documents:write");
        // A token from another issuer naming the platform project: the seed only trusts the global issuer.
        using var projectToken = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = PlatformDatabases.AuthProjectId;
            token.Claims["scope"] = "documents:read databases:read";
        }));
        using var anonymous = api.WithToken(null);

        using var byKey = await customerKey.GetAsync(ManagementApi.Uri(Auth + "/collections"));
        using var byProjectToken = await projectToken.GetAsync(ManagementApi.Uri(Auth + "/collections"));
        using var byNobody = await anonymous.GetAsync(ManagementApi.Uri(Auth + "/collections"));
        using var organizations = await api.Client.GetAsync(ManagementApi.Uri("/v1/management/organizations"));
        using var platform = await api.Client.GetAsync(ManagementApi.Uri($"/v1/management/organizations/{PlatformDatabases.OrganizationId}"));
        // Not even a user of the platform operator: the platform organization has no members.
        using var operatorUser = api.Service(EntityIds.New().ToString());
        using var asOperatorUser = await operatorUser.GetAsync(ManagementApi.Uri($"/v1/management/organizations/{PlatformDatabases.OrganizationId}"));

        await Assert.That(byKey.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(byProjectToken.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(byNobody.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await organizations.Content.ReadAsStringAsync()).DoesNotContain(PlatformDatabases.OrganizationId);
        await Assert.That(platform.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(asOperatorUser.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Global_issuer_project_tokens_resolve_their_seeded_database_without_auth_enrollment()
    {
        // Exercise the parsed production trust pair over real local listeners.
        var production = TerraBaseOptions.FromValues(new Dictionary<string, string>(), production: true);
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            Seeding(settings);
            settings["auth-issuer"] = production.AuthIssuer ?? string.Empty;
            settings["auth-audience"] = string.Join(',', production.AuthAudiences);
        }, services => services.AddHttpClient("ts-auth").AddHttpMessageHandler(() => new GlobalIssuerRoute()));
        await SeededAsync(api.Server);
        foreach (var (name, database) in PlatformDatabases.Databases)
        {
            using var client = api.WithToken(api.Issuer.Mint(api.User, token =>
            {
                token.Issuer = TerraBaseOptions.PlatformAuthIssuer;
                token.Audience = TerraBaseOptions.PlatformAuthAudience;
                token.Claims["project_id"] = database.ProjectId;
                token.Claims["database"] = database.Id;
                token.Claims["scope"] = "databases:read documents:read";
            }));
            using var read = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{database.ProjectId}/databases/{name}/collections"));
            using var other = await client.GetAsync(ManagementApi.Uri(name == PlatformDatabases.Auth ? Dashboard + "/collections" : Auth + "/collections"));
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        foreach (var mismatch in new[] { "issuer", "audience" })
        {
            using var rejected = api.WithToken(api.Issuer.Mint(api.User, token =>
            {
                token.Issuer = mismatch == "issuer" ? TestIssuer.Issuer : TerraBaseOptions.PlatformAuthIssuer;
                token.Audience = mismatch == "audience" ? TestIssuer.Audience : TerraBaseOptions.PlatformAuthAudience;
                token.Claims["project_id"] = PlatformDatabases.AuthProjectId;
                token.Claims["scope"] = "databases:read documents:read";
            }));
            using var response = await rejected.GetAsync(ManagementApi.Uri(Auth + "/collections"));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(0);
    }

    // Test-only transport: serve the production issuer's discovery/JWKS with this test's private signing keys.
    internal sealed class GlobalIssuerRoute : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(request.RequestUri!.AbsoluteUri.Replace(TerraBaseOptions.PlatformAuthIssuer, TestIssuer.Issuer, StringComparison.Ordinal));
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri.AbsoluteUri == TestIssuer.Discovery && response.IsSuccessStatusCode)
            {
                var discovery = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Content.Dispose();
                response.Content = new StringContent(discovery.Replace(TestIssuer.Issuer, TerraBaseOptions.PlatformAuthIssuer, StringComparison.Ordinal),
                    Encoding.UTF8, "application/json");
            }

            return response;
        }
    }

    private static void Seeding(Dictionary<string, string> settings) => settings[TerraBaseTestServer.PlatformSeedKey] = "true";

    private sealed record Seeded(string Id, string Name, string? HomeRegion, string Replicas, long Version, DateTimeOffset CreatedAt, bool AllRegions);

    // The seed runs in the background after startup.
    private static Task SeededAsync(TerraBaseTestServer server) => TerraBaseTestServer.EventuallyAsync(async token =>
        (await RecordsAsync(server, token)).Count == PlatformDatabases.Databases.Count);

    private static async Task<IReadOnlyList<Seeded>> RecordsAsync(TerraBaseTestServer server) => await RecordsAsync(server, CancellationToken.None);

    private static async Task<IReadOnlyList<Seeded>> RecordsAsync(TerraBaseTestServer server, CancellationToken cancellationToken)
    {
        var management = server.Services.GetRequiredService<IManagementService>();
        var records = new List<Seeded>();
        foreach (var (name, database) in PlatformDatabases.Databases)
        {
            if (await management.FindDatabaseRecordAsync(PlatformDatabases.OrganizationId, database.ProjectId, name, cancellationToken) is ManagedDatabase record)
            {
                records.Add(new Seeded(record.Id, record.Name, record.HomeRegion, string.Join(',', record.Replicas), record.PlacementVersion, record.CreatedAt, record.AllRegions));
            }
        }

        return records;
    }
}
