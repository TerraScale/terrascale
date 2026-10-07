using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Cluster;
using TerraBase.Features.CrossRegion;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.CrossRegion;

/// <summary>
/// Two regions, gru-1 and sfo-1, one member each, replicating over an in-memory region bus with NATS request/reply
/// semantics. Each test registers its database in both regions under the same ids, as an operator does with a
/// management commit (docs/cross-region.md), and reads and writes through the real client listeners.
/// </summary>
public sealed class CrossRegionTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] Scopes = ["documents:read", "documents:write"];

    [Test]
    public async Task A_write_in_either_region_is_readable_in_the_other_with_the_same_version()
    {
        await using var regions = await Regions.StartAsync();
        var gruId = ReplicationDocuments.Id("gru");
        var sfoId = ReplicationDocuments.Id("sfo");

        var gruValue = Faker.Lorem.Sentence();
        using var written = await regions.Gru.PutAsJsonAsync(regions.Document(gruId), new { value = gruValue });
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var (_, sfoVersion) = await Regions.EventuallyReadAsync(regions.Sfo, regions.Document(gruId), gruValue);
        // The version token read in one region is the version in the other: it holds as If-Match there.
        await Assert.That(sfoVersion).IsEqualTo(written.Headers.ETag!.Tag);

        var sfoValue = Faker.Lorem.Sentence();
        using var answered = await regions.Sfo.PutAsJsonAsync(regions.Document(sfoId), new { value = sfoValue });
        answered.EnsureSuccessStatusCode();
        _ = await Regions.EventuallyReadAsync(regions.Gru, regions.Document(sfoId), sfoValue);

        var update = Faker.Lorem.Sentence();
        using var conditional = new HttpRequestMessage(HttpMethod.Put, regions.Document(gruId)) { Content = JsonContent.Create(new { value = update }) };
        conditional.Headers.IfMatch.Add(new EntityTagHeaderValue(sfoVersion));
        using var updated = await regions.Sfo.SendAsync(conditional);
        await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        _ = await Regions.EventuallyReadAsync(regions.Gru, regions.Document(gruId), update);

        await TerraBaseTestServer.EventuallyAsync(async token =>
            await regions.GruApi.Server.MetricAsync("terrabase_wan_lag", "region=\"sfo-1\"", token) == 0
            && await regions.SfoApi.Server.MetricAsync("terrabase_wan_lag", "region=\"gru-1\"", token) == 0);
        // The collection and the first document; the update is sfo's own group.
        await Assert.That(await regions.SfoApi.Server.MetricAsync("terrabase_wan_pulled_groups_total", "region=\"gru-1\"")).IsEqualTo(2);
        await Assert.That(await regions.GruApi.Server.MetricAsync("terrabase_wan_members", "region=\"sfo-1\"")).IsEqualTo(1);
    }

    [Test]
    public async Task Partitioned_regions_keep_serving_their_writes_and_converge_after_heal()
    {
        await using var regions = await Regions.StartAsync();
        regions.Network.Partition("gru", "sfo");
        // Both sides went through rounds that reached no member of the other.
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await Regions.CountAsync(regions.SfoApi.Server, "terrabase_wan_failures_total", "reason=\"heads\"", token) > 0
            && await Regions.CountAsync(regions.GruApi.Server, "terrabase_wan_failures_total", "reason=\"heads\"", token) > 0);

        var onlyGru = ReplicationDocuments.Id("only-gru");
        var onlySfo = ReplicationDocuments.Id("only-sfo");
        var both = ReplicationDocuments.Id("both");
        (await regions.Gru.PutAsJsonAsync(regions.Document(onlyGru), new { value = "gru" })).EnsureSuccessStatusCode();
        (await regions.Sfo.PutAsJsonAsync(regions.Document(onlySfo), new { value = "sfo" })).EnsureSuccessStatusCode();
        (await regions.Gru.PutAsJsonAsync(regions.Document(both), new { value = "written in gru" })).EnsureSuccessStatusCode();
        (await regions.Sfo.PutAsJsonAsync(regions.Document(both), new { value = "written in sfo" })).EnsureSuccessStatusCode();
        // Each region serves its own writes during the partition.
        await Assert.That((await regions.Gru.GetAsync(regions.Document(onlyGru))).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await regions.Sfo.GetAsync(regions.Document(onlySfo))).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await regions.Sfo.GetAsync(regions.Document(onlyGru))).StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        regions.Network.Heal();
        _ = await Regions.EventuallyReadAsync(regions.Sfo, regions.Document(onlyGru), "gru");
        _ = await Regions.EventuallyReadAsync(regions.Gru, regions.Document(onlySfo), "sfo");
        // The concurrent writes of one document settle on one winner, the same body and version in both regions.
        await regions.ConvergedAsync();
    }

    [Test]
    public async Task Concurrent_writes_in_both_regions_converge_on_the_same_documents()
    {
        await using var regions = await Regions.StartAsync();
        var ids = Enumerable.Range(0, 12).Select(i => ReplicationDocuments.Id($"doc-{i}")).ToArray();
        var values = Enumerable.Range(0, 2 * ids.Length * 4).Select(_ => Faker.Random.Utf16String(1, 64)).ToArray();
        var writes = new List<Task>();
        foreach (var (client, side) in new[] { (regions.Gru, 0), (regions.Sfo, 1) })
        {
            writes.AddRange(ids.Select((id, i) => Task.Run(async () =>
            {
                for (var round = 0; round < 4; round++)
                {
                    using var response = round == 3 && i == 0
                        ? await client.DeleteAsync(regions.Document(id))
                        : await client.PutAsJsonAsync(regions.Document(id), new { value = values[(((side * ids.Length) + i) * 4) + round], round });
                    if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                    {
                        throw new InvalidOperationException($"a write answered {response.StatusCode}");
                    }
                }
            })));
        }

        await Task.WhenAll(writes);
        await regions.ConvergedAsync();
    }

    [Test]
    public async Task A_group_larger_than_one_reply_crosses_in_parts()
    {
        await using var regions = await Regions.StartAsync();
        var id = ReplicationDocuments.Id("large");
        // Random base64url does not compress (nor need JSON escapes): about 1 MB of envelope, more than one NATS reply holds.
        var value = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(740_000));
        (await regions.Gru.PutAsJsonAsync(regions.Document(id), new { value })).EnsureSuccessStatusCode();
        _ = await Regions.EventuallyReadAsync(regions.Sfo, regions.Document(id), value);
        await Assert.That(await regions.SfoApi.Server.MetricAsync("terrabase_wan_pulled_encoded_bytes_total", "region=\"gru-1\"")).IsGreaterThan(WanProtocol.ReplyBudget);
    }

    [Test]
    public async Task A_database_of_a_project_only_one_region_registers_stays_there()
    {
        await using var regions = await Regions.StartAsync();
        var project = Guid.NewGuid().ToString("D");
        using var registered = await regions.GruApi.Client.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("D"),
            ops = new object[] { new { op = "create_project", organization = regions.Organization, id = project, name = "gru-only" } },
        }), Encoding.UTF8, "application/json"));
        registered.EnsureSuccessStatusCode();
        var created = await regions.GruApi.Server.Services.GetRequiredService<ITenantDirectory>()
            .CreateDatabaseAsync(regions.Organization, project, "private", CancellationToken.None);
        var path = $"/v1/projects/{project}/databases/private";
        (await regions.Gru.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
        (await regions.Gru.PutAsJsonAsync(new Uri($"{path}/collections/items/documents/{ReplicationDocuments.Id("secret")}", UriKind.Relative), new { value = "here" }))
            .EnsureSuccessStatusCode();

        await TerraBaseTestServer.EventuallyAsync(async token =>
            await regions.SfoApi.Server.MetricAsync("terrabase_wan_unregistered", "region=\"gru-1\"", token) == 1);
        var database = created!.Value.Database;
        await Assert.That(File.Exists(Path.Combine(regions.SfoApi.Server.DataDirectory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db"))).IsFalse();
    }

    [Test]
    public async Task A_database_created_in_either_region_of_a_shared_project_appears_in_the_other()
    {
        await using var regions = await Regions.StartAsync();
        foreach (var (writer, reader, name) in new[] { (regions.Gru, regions.Sfo, "orders"), (regions.Sfo, regions.Gru, "events") })
        {
            var path = $"/v1/projects/{regions.Project}/databases/{name}";
            using var created = await writer.PutAsync(new Uri(path, UriKind.Relative), null);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            (await writer.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
            var document = new Uri($"{path}/collections/items/documents/{ReplicationDocuments.Id(name)}", UriKind.Relative);
            (await writer.PutAsJsonAsync(document, new { value = name })).EnsureSuccessStatusCode();

            // Registered in the other region under the same id and name, with its documents.
            _ = await Regions.EventuallyReadAsync(reader, document, name);
            using var listed = await reader.PutAsync(new Uri(path, UriKind.Relative), null);
            await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()).IsEqualTo(id);
        }
    }

    [Test]
    public async Task A_database_deleted_in_one_region_is_not_brought_back_and_a_name_clash_is_counted()
    {
        await using var regions = await Regions.StartAsync();

        // sfo deletes the canary; gru keeps writing to it.
        using var deleted = await regions.SfoApi.Client.DeleteAsync(
            new Uri($"/v1/management/organizations/{regions.Organization}/projects/{regions.Project}/databases/{regions.Database}", UriKind.Relative));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        (await regions.Gru.PutAsJsonAsync(regions.Document(ReplicationDocuments.Id("after-delete")), new { value = "gru" })).EnsureSuccessStatusCode();

        // sfo has a database "clash" that was never written; gru then creates and writes its own "clash".
        (await regions.Sfo.PutAsync(new Uri($"/v1/projects/{regions.Project}/databases/clash", UriKind.Relative), null)).EnsureSuccessStatusCode();
        var clash = $"/v1/projects/{regions.Project}/databases/clash";
        (await regions.Gru.PutAsync(new Uri(clash, UriKind.Relative), null)).EnsureSuccessStatusCode();
        (await regions.Gru.PostAsJsonAsync(new Uri($"{clash}/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();

        await TerraBaseTestServer.EventuallyAsync(async token =>
            await regions.SfoApi.Server.MetricAsync("terrabase_wan_unregistered", "region=\"gru-1\"", token) == 1
            && await regions.SfoApi.Server.MetricAsync("terrabase_wan_conflicts", "region=\"gru-1\"", token) == 1);
        using var gone = await regions.Sfo.GetAsync(regions.Document(ReplicationDocuments.Id("after-delete")));
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var own = await regions.Sfo.GetAsync(new Uri($"{clash}/collections/items", UriKind.Relative));
        await Assert.That(own.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_commit_registers_a_database_under_a_chosen_id_once()
    {
        await using var regions = await Regions.StartAsync();
        // A new organization and project, but the canary's database id.
        var commit = Regions.Registration(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), regions.Database, "other");
        using var taken = await regions.GruApi.Client.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
        await Assert.That(taken.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var error = (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("id_taken");
        await Assert.That(error.GetProperty("op").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Every_member_of_a_region_reaches_every_member_of_the_other_through_single_replies()
    {
        // gru has two members, a catalog primary and its follower, which replicate to each other over the peer link; the
        // link between regions gives a request one reply, so sfo learns gru-b from gru-a's member list.
        using var peerTls = new PeerTls();
        var network = new FakeRegionNetwork();
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-members-").FullName;
        var credentials = Path.Combine(directory, "user.creds");
        await File.WriteAllTextAsync(credentials, Regions.Credentials);
        var gruMembers = new FakeMemberStore(TimeProvider.System);
        var (portA, portB) = (TerraBaseTestServer.FreePort(), TerraBaseTestServer.FreePort());
        Task<ManagementApi> StartGruAsync(string name, int port)
        {
            return ManagementApi.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                settings["peer-port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                settings["peers"] = $"gru-a=localhost:{portA},gru-b=localhost:{portB}";
                settings["catalog-primary"] = "gru-a";
                Regions.Configure(settings, credentials, "gru-1", name);
            }, services => Regions.Join(services, network, "gru", gruMembers, name));
        }

        try
        {
            await using var gruA = await StartGruAsync("gru-a", portA);
            await using var gruB = await StartGruAsync("gru-b", portB);
            await using var sfo = await ManagementApi.StartAsync(
                settings => Regions.Configure(settings, credentials, "sfo-1", "sfo-a"),
                services => Regions.Join(services, network, "sfo", new FakeMemberStore(TimeProvider.System)));
            var (organization, project, database) = (Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
            foreach (var api in new[] { gruA, sfo })
            {
                using var registered = await api.Client.PostAsync(
                    new Uri("/v1/management/commits", UriKind.Relative), new StringContent(Regions.Registration(organization, project, database, "canary"), Encoding.UTF8, "application/json"));
                registered.EnsureSuccessStatusCode();
            }

            var key = await gruA.Server.IssueAsync(organization, Scopes);
            using var clientA = gruA.DataClient(key);
            using var clientB = gruB.DataClient(key);
            using var clientSfo = await sfo.CreateClientAsync(organization, Scopes);
            var collection = new Uri($"/v1/projects/{project}/databases/canary/collections/items", UriKind.Relative);
            (await clientA.PostAsJsonAsync(new Uri($"/v1/projects/{project}/databases/canary/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
            await TerraBaseTestServer.EventuallyAsync(async token =>
                (await clientB.GetAsync(collection, token)).StatusCode == HttpStatusCode.OK && (await clientSfo.GetAsync(collection, token)).StatusCode == HttpStatusCode.OK);

            Uri Document(string id)
            {
                return new($"/v1/projects/{project}/databases/canary/collections/items/documents/{ReplicationDocuments.Id(id)}", UriKind.Relative);
            }

            (await clientA.PutAsJsonAsync(Document("from-a"), new { value = "a" })).EnsureSuccessStatusCode();
            (await clientB.PutAsJsonAsync(Document("from-b"), new { value = "b" })).EnsureSuccessStatusCode();
            (await clientSfo.PutAsJsonAsync(Document("from-sfo"), new { value = "sfo" })).EnsureSuccessStatusCode();
            _ = await Regions.EventuallyReadAsync(clientSfo, Document("from-a"), "a");
            _ = await Regions.EventuallyReadAsync(clientSfo, Document("from-b"), "b");
            _ = await Regions.EventuallyReadAsync(clientA, Document("from-sfo"), "sfo");
            _ = await Regions.EventuallyReadAsync(clientB, Document("from-sfo"), "sfo");
            await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", "region=\"gru-1\"")).IsEqualTo(2);

            // gru-b stops answering: sfo is no longer known to be caught up, although nothing it can measure is behind.
            network.Mute("gru-b");
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await sfo.Server.MetricAsync("terrabase_wan_members", "region=\"gru-1\"", token) == 1
                && await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", "region=\"gru-1\"", token) > 3);
            await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", "region=\"gru-1\"")).IsEqualTo(0);
            network.Heal();
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", "region=\"gru-1\"", token) < 3);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("gru-1=gru", true)]
    [Arguments("sfo-1=sfo,iad-1=iad", true)]
    [Arguments("gru-1=gru,sfo-1=gru", true)]
    [Arguments("gru-1=GRU,sfo-1=sfo", true)]
    [Arguments("gru-1=gru,sfo-1=sfo", false)]
    public async Task A_node_with_invalid_wan_regions_or_no_nats_does_not_start(string regions, bool nats)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-config-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Regions.Credentials);
            await Assert.That(async () =>
            {
                await using var started = await TerraBaseTestServer.StartAsync(settings =>
                {
                    settings["region"] = "gru-1";
                    settings["node-name"] = "gru-member";
                    settings["wan-regions"] = regions;
                    if (nats)
                    {
                        settings["nats-url"] = "nats://127.0.0.1:4222";
                        settings["nats-credentials-file"] = credentials;
                    }
                });
            }).Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Both regions with one database, "canary", registered in each by a commit with the same organization, project and
    // database ids, a collection "items", and a data client per region.
    private sealed class Regions : IAsyncDisposable
    {
        private readonly string _credentials;

        private Regions(FakeRegionNetwork network, string credentials, ManagementApi gru, ManagementApi sfo)
        {
            (Network, _credentials, GruApi, SfoApi) = (network, credentials, gru, sfo);
        }

        // A NATS user credentials file in shape only: the region bus and member store of these servers are in memory.
        public const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";

        public FakeRegionNetwork Network { get; }

        public ManagementApi GruApi { get; }

        public ManagementApi SfoApi { get; }

        public HttpClient Gru { get; private set; } = null!;

        public HttpClient Sfo { get; private set; } = null!;

        public string Organization { get; } = Guid.NewGuid().ToString("D");

        public string Project { get; } = Guid.NewGuid().ToString("D");

        public string Database { get; } = Guid.NewGuid().ToString("D");

        public Uri Document(string id) => new($"/v1/projects/{Project}/databases/canary/collections/items/documents/{id}", UriKind.Relative);

        public static async Task<Regions> StartAsync()
        {
            var network = new FakeRegionNetwork();
            var credentials = Path.Combine(Directory.CreateTempSubdirectory("terrabase-wan-").FullName, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials);
            ManagementApi? gru = null;
            try
            {
                gru = await StartRegionAsync(network, credentials, "gru-1", "gru");
                var regions = new Regions(network, credentials, gru, await StartRegionAsync(network, credentials, "sfo-1", "sfo"));
                await regions.RegisterAsync();
                return regions;
            }
            catch
            {
                if (gru is not null)
                {
                    await gru.DisposeAsync();
                }

                Directory.Delete(Path.GetDirectoryName(credentials)!, recursive: true);
                throw;
            }
        }

        // An organization, project and database with chosen ids, in one commit.
        public static string Registration(string organization, string project, string database, string name) => JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("D"),
            ops = new object[]
            {
                new { op = "create_organization", id = organization, name = $"canary-{organization[..8]}" },
                new { op = "create_project", organization, id = project, name = "canary" },
                new { op = "create_database", organization, project, name, id = database },
            },
        });

        // The document at uri once its value is expected, with its version.
        public static async Task<(JsonElement Document, string Version)> EventuallyReadAsync(HttpClient client, Uri uri, string value)
        {
            (JsonElement Document, string Version) read = default;
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var response = await client.GetAsync(uri, token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                read = (body.GetProperty("document"), response.Headers.ETag!.Tag);
                return read.Document.GetProperty("value").GetString() == value;
            });
            return read;
        }

        // The samples of a counter, 0 before its first.
        public static async Task<double> CountAsync(TerraBaseTestServer server, string name, string label, CancellationToken token)
        {
            try
            {
                return await server.MetricAsync(name, label, token);
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        // Both regions hold the same documents, each with the same body and version, and neither is behind the other.
        public Task ConvergedAsync() =>
            TerraBaseTestServer.EventuallyAsync(async token =>
                await GruApi.Server.MetricAsync("terrabase_wan_lag", "region=\"sfo-1\"", token) == 0
                && await SfoApi.Server.MetricAsync("terrabase_wan_lag", "region=\"gru-1\"", token) == 0
                && await ListAsync(Gru, token) == await ListAsync(Sfo, token));

        public async ValueTask DisposeAsync()
        {
            Gru?.Dispose();
            Sfo?.Dispose();
            try
            {
                await GruApi.DisposeAsync();
            }
            finally
            {
                await SfoApi.DisposeAsync();
                Directory.Delete(Path.GetDirectoryName(_credentials)!, recursive: true);
            }
        }

        // A member of the group gru-1=gru, sfo-1=sfo, on the in-memory region bus and member store.
        public static void Configure(Dictionary<string, string> settings, string credentials, string region, string node)
        {
            settings["region"] = region;
            settings["node-name"] = node;
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }

        public static void Join(IServiceCollection services, FakeRegionNetwork network, string cell, FakeMemberStore members, string? node = null)
        {
            services.AddSingleton(network.Join(cell, node));
            services.AddSingleton<IMemberStore>(members);
        }

        private static Task<ManagementApi> StartRegionAsync(FakeRegionNetwork network, string credentials, string region, string cell) =>
            ManagementApi.StartAsync(
                settings => Configure(settings, credentials, region, $"{cell}-member"),
                services => Join(services, network, cell, new FakeMemberStore(TimeProvider.System)));

        // Every document of "items" with its version and body, in id order.
        private async Task<string> ListAsync(HttpClient client, CancellationToken token)
        {
            var listed = new StringBuilder();
            string? cursor = null;
            do
            {
                var page = await client.GetFromJsonAsync<JsonElement>(
                    new Uri($"/v1/projects/{Project}/databases/canary/collections/items/documents?limit=100{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}", UriKind.Relative), token);
                foreach (var document in page.GetProperty("items").EnumerateArray())
                {
                    _ = listed.Append(document.GetProperty("id").GetString()).Append(' ').Append(document.GetProperty("version").GetString())
                        .Append(' ').Append(document.GetProperty("document").GetRawText()).Append('\n');
                }

                cursor = page.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            }
            while (cursor is not null);

            return listed.ToString();
        }

        private async Task RegisterAsync()
        {
            foreach (var api in new[] { GruApi, SfoApi })
            {
                var commit = Registration(Organization, Project, Database, "canary");
                using var response = await api.Client.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
            }

            Gru = await GruApi.CreateClientAsync(Organization, Scopes);
            Sfo = await SfoApi.CreateClientAsync(Organization, Scopes);
            (await Gru.PostAsJsonAsync(new Uri($"/v1/projects/{Project}/databases/canary/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
            await TerraBaseTestServer.EventuallyAsync(async token =>
                (await Sfo.GetAsync(new Uri($"/v1/projects/{Project}/databases/canary/collections/items", UriKind.Relative), token)).StatusCode == HttpStatusCode.OK);
        }
    }
}
