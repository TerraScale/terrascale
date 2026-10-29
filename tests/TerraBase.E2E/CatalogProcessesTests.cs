using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.E2E;

/// <summary>Catalog creation, name races and revocation across independently killed and restarted node processes.</summary>
public sealed class CatalogProcessesTests
{
    [Test]
    [Scenario(Nodes.Processes)]
    public async Task A_database_created_in_one_region_process_serves_in_the_others_and_a_same_name_race_keeps_one()
    {
        Skip.When(Cluster.Live, "runs local node processes");
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        var tenant = await Tenant.CreateAsync(regions, Cluster.Sjc);

        // Created in sjc, placed in every region: gru and fra serve it, and gru's write reaches sjc.
        _ = await tenant.CreateDatabaseAsync(Cluster.Sjc, "made-in-sjc", [Cluster.Gru, Cluster.Fra]);
        _ = await Docs.PostAsync(regions[Cluster.Gru], tenant.Path("made-in-sjc", "/collections"), new JsonObject { ["name"] = "items" });
        var item = tenant.Path("made-in-sjc", $"/collections/items/documents/by-key?id={EntityIds.New()}");
        await Docs.PutAsync(regions[Cluster.Gru], item, new JsonObject { ["from"] = "gru" });
        _ = await Converge.UntilAsync(() => tenant.ReadAsync(Cluster.Sjc, item), document => document?["from"]?.GetValue<string>() == "gru", "gru's write on sjc");

        // The same name at once in gru and sjc (each its home, the other its replica): a region that already sees the other's
        // answers 409. Whatever each created, the name ends serving one database: a document written through gru's name is read
        // through sjc's, and the other way round.
        var attempts = await Task.WhenAll(
            tenant.TryCreateDatabaseAsync(Cluster.Gru, "twin", [Cluster.Sjc]),
            tenant.TryCreateDatabaseAsync(Cluster.Sjc, "twin", [Cluster.Gru]));
        await Assert.That(attempts.OfType<string>()).IsNotEmpty();
        var twin = tenant.Path("twin", $"/collections/items/documents/by-key?id={EntityIds.New()}");
        _ = await Converge.UntilAsync(async () =>
        {
            foreach (var region in new[] { Cluster.Gru, Cluster.Sjc })
            {
                using var collection = await regions[region].PostAsJsonAsync(tenant.Path("twin", "/collections"), new { name = "items" });
                if (!collection.IsSuccessStatusCode && !await Cluster.TakenAsync(collection))
                {
                    await Docs.EnsureSuccessAsync(collection);
                }

                using var written = await regions[region].PutAsJsonAsync(twin, new { from = region });
                if (!written.IsSuccessStatusCode)
                {
                    return false;
                }

                // The other region's read through the name finds this write: both names reach one database.
                var other = region == Cluster.Gru ? Cluster.Sjc : Cluster.Gru;
                if ((await tenant.ReadAsync(other, twin))?["from"]?.GetValue<string>() != region)
                {
                    return false;
                }
            }

            return true;
        }, same => same, "one database behind the twin name in gru and sjc");
    }

    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task A_database_deleted_while_a_region_process_was_killed_stays_deleted_and_its_name_serves_a_new_one()
    {
        Skip.When(Cluster.Live, "runs local node processes");
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        var tenant = await Tenant.CreateAsync(regions, Cluster.Sjc);
        var doomed = await tenant.CreateDatabaseAsync(Cluster.Sjc, "doomed", [Cluster.Gru, Cluster.Fra]);
        _ = await Docs.PostAsync(regions[Cluster.Sjc], tenant.Path("doomed", "/collections"), new JsonObject { ["name"] = "items" });
        var old = tenant.Path("doomed", $"/collections/items/documents/by-key?id={EntityIds.New()}");
        await Docs.PutAsync(regions[Cluster.Sjc], old, new JsonObject { ["generation"] = 1 });
        _ = await Converge.UntilAsync(() => tenant.ReadAsync(Cluster.Fra, old), document => document?["generation"]?.GetValue<int>() == 1, "the document on fra");

        // fra is killed (SIGKILL) while sjc removes the replicas and deletes the database.
        await regions.KillAsync(Cluster.Fra);
        await tenant.ChangeDatabaseAsync(Cluster.Sjc, doomed, HttpMethod.Patch, new { replicaRegions = Array.Empty<string>() });
        await tenant.ChangeDatabaseAsync(Cluster.Sjc, doomed, HttpMethod.Delete);
        await regions.StartAgainAsync(Cluster.Fra);
        _ = await Converge.UntilAsync(() => regions[Cluster.Fra].GetFromJsonAsync<JsonElement>(new Uri("/v1/catalog-gate", UriKind.Relative)),
            gate => gate.GetProperty("open").GetBoolean(), "fra's catalog verifies after its restart");
        foreach (var region in regions.Regions)
        {
            _ = await Converge.UntilAsync(() => Docs.StatusAsync(regions[region], tenant.Path("doomed", "/collections")),
                status => status == HttpStatusCode.NotFound, $"the deleted database gone on {region}");
        }

        // The name serves a new database created in fra; the old database's document does not come back anywhere.
        var recreated = await tenant.CreateDatabaseAsync(Cluster.Fra, "doomed", [Cluster.Gru, Cluster.Sjc]);
        await Assert.That(recreated).IsNotEqualTo(doomed);
        _ = await Docs.PostAsync(regions[Cluster.Fra], tenant.Path("doomed", "/collections"), new JsonObject { ["name"] = "items" });
        var fresh = tenant.Path("doomed", $"/collections/items/documents/by-key?id={EntityIds.New()}");
        await Docs.PutAsync(regions[Cluster.Fra], fresh, new JsonObject { ["generation"] = 2 });
        foreach (var region in regions.Regions)
        {
            _ = await Converge.UntilAsync(() => tenant.ReadAsync(region, fresh), document => document?["generation"]?.GetValue<int>() == 2, $"the recreated database on {region}");
            await Assert.That(await tenant.ReadAsync(region, old)).IsNull().Because(region);
        }
    }

    [Test]
    [Scenario(Nodes.Processes)]
    public async Task Schemas_and_collection_drops_made_in_one_region_process_take_effect_in_the_others()
    {
        Skip.When(Cluster.Live, "runs local node processes");
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        var tenant = await Tenant.CreateAsync(regions, Cluster.Gru);
        _ = await tenant.CreateDatabaseAsync(Cluster.Gru, "ddl", [Cluster.Sjc, Cluster.Fra]);
        _ = await Docs.PostAsync(regions[Cluster.Gru], tenant.Path("ddl", "/collections"), new JsonObject { ["name"] = "people" });
        using (var attached = await regions[Cluster.Gru].PutAsync(tenant.Path("ddl", "/collections/people/schema"),
            new StringContent("""{"type":"object","required":["email"],"properties":{"email":{"type":"string"}}}""", Encoding.UTF8, "application/json")))
        {
            await Docs.EnsureSuccessAsync(attached);
        }

        foreach (var region in new[] { Cluster.Sjc, Cluster.Fra })
        {
            _ = await Converge.UntilAsync(async () =>
            {
                using var invalid = await regions[region].PutAsJsonAsync(tenant.Path("ddl", $"/collections/people/documents/by-key?id={EntityIds.New()}"), new { email = 123 });
                if (invalid.StatusCode != HttpStatusCode.BadRequest)
                {
                    return false;
                }

                return (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString() == "schema_validation_failed";
            }, rejected => rejected, $"the schema enforced on {region}");
        }

        await Docs.DeleteAsync(regions[Cluster.Gru], tenant.Path("ddl", "/collections/people"));
        foreach (var region in new[] { Cluster.Sjc, Cluster.Fra })
        {
            _ = await Converge.UntilAsync(() => Docs.StatusAsync(regions[region], tenant.Path("ddl", "/collections/people")),
                status => status == HttpStatusCode.NotFound, $"the dropped collection on {region}");
        }
    }

    // An organization and project of a new user, made in one region; the user owns them in every region the catalog reaches.
    private sealed class Tenant(RegionProcesses regions, string user, string organization, string project)
    {
        private string User { get; } = user;

        public string Organization { get; } = organization;

        public string Project { get; } = project;

        public static async Task<Tenant> CreateAsync(RegionProcesses regions, string region)
        {
            var user = EntityIds.New().ToString();
            var organization = await CatalogApi.CreateResourceAsync(regions[region], user, "/v1/management/organizations", new { name = "catalog" });
            var project = await CatalogApi.CreateResourceAsync(regions[region], user, $"/v1/management/organizations/{organization}/projects", new { name = "catalog" });
            using var plan = await CatalogApi.SendAsync(regions[region], user, HttpMethod.Put, $"/v1/management/organizations/{organization}/plan", new { plan = "enterprise" });
            plan.EnsureSuccessStatusCode();
            var tenant = new Tenant(regions, user, organization, project);
            foreach (var peer in regions.Regions)
            {
                _ = await Converge.UntilAsync(async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        new Uri($"/v1/management/organizations/{tenant.Organization}/projects/{tenant.Project}", UriKind.Relative))
                    {
                        Version = HttpVersion.Version20,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    };
                    request.Headers.Add("X-TerraBase-User-Id", tenant.User);
                    using var response = await regions[peer].SendAsync(request);
                    return response.StatusCode;
                }, status => status == HttpStatusCode.OK, $"the new project on {peer}");
            }

            return tenant;
        }

        public Uri Path(string database, string suffix = "") => new($"/v1/projects/{Project}/databases/{database}{suffix}", UriKind.Relative);

        public async Task<string> CreateDatabaseAsync(string region, string name, string[] replicas)
        {
            using var response = await SendAsync(region, name, replicas);
            await Docs.EnsureSuccessAsync(response);
            var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            foreach (var placed in replicas.Prepend(region))
            {
                _ = await Converge.UntilAsync(() => ServesAsync(placed, name), served => served, $"the new database on {placed}");
            }

            return id;
        }

        // The id created, or null when the region already held the name.
        public async Task<string?> TryCreateDatabaseAsync(string region, string name, string[] replicas)
        {
            using var response = await SendAsync(region, name, replicas);
            if (await Cluster.TakenAsync(response))
            {
                return null;
            }

            await Docs.EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        // Whether the region serves the database: its collections answer 200. A region whose catalog does not hold the
        // project yet answers 401 to the operator, one without the database 404.
        public async Task<bool> ServesAsync(string region, string name)
        {
            using var response = await regions[region].GetAsync(Path(name, "/collections"));
            return response.StatusCode == HttpStatusCode.OK;
        }

        public Task<JsonNode?> ReadAsync(string region, Uri document) => Docs.ReadAsync(regions[region], document);

        public async Task ChangeDatabaseAsync(string region, string database, HttpMethod method, object? body = null)
        {
            using var response = await CatalogApi.SendAsync(regions[region], User, method,
                $"/v1/management/organizations/{Organization}/projects/{Project}/databases/{database}", body);
            await Docs.EnsureSuccessAsync(response);
        }

        private Task<HttpResponseMessage> SendAsync(string region, string name, string[] replicas) =>
            CatalogApi.SendAsync(regions[region], User, HttpMethod.Post,
                $"/v1/management/organizations/{Organization}/projects/{Project}/databases", new { name, replicaRegions = replicas });

    }
}
