using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The catalog (<c>_system</c>) as a lattice database placed in every region (docs/terrabase/multi-region-replication.md
/// section 15): each machine writes its own catalog, and the CDC tailer and the peer streams carry every write to every other
/// machine, where it joins like a client database's. Real machines, real CDC.
/// </summary>
public sealed class CatalogLatticeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static readonly string[] Sjc = ["sjc"];

    private static readonly string[] ReadScope = ["documents:read"];

    [Test]
    public async Task An_organization_created_in_one_region_reaches_every_region_with_the_same_document_and_version()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc", "fra");
        var organization = await cluster["gru"].Api.CreateOrganizationAsync("Replicated");

        foreach (var region in cluster.Regions)
        {
            await Assert.That(await region.UntilAsync("organizations", organization, json => json is not null)).IsTrue().Because(region.Name);
        }

        var stored = await Task.WhenAll(cluster.Regions.Select(region => region.ReadStoredAsync("organizations", organization)));
        await Assert.That(stored.Select(document => document!.Version).Distinct().Count()).IsEqualTo(1);
        await Assert.That(stored.Select(document => Encoding.UTF8.GetString(document!.Json.Span)).Distinct().Count()).IsEqualTo(1);
        // The membership and the creator's organization-name index (a scalar) came with it.
        await Assert.That(await cluster["fra"].UntilAsync($"organizations/{organization}/members", cluster["gru"].Api.User, json => json is not null)).IsTrue();
        await Assert.That(await cluster["fra"].UntilAsync($"users/{cluster["gru"].Api.User}/organization_names", "replicated", json => json == $"\"{organization}\"")).IsTrue();
    }

    [Test]
    public async Task Concurrent_writes_of_one_catalog_document_in_every_region_converge_to_one_value()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc", "fra");
        var id = EntityIds.New().ToString();
        await Task.WhenAll(cluster.Regions.Select(region => region.WriteAsync(async transaction =>
            await transaction.PutAsync("dashboard/plans", id, Json($$"""{"region":"{{region.Name}}","shared":1}"""), CancellationToken.None))));

        await Assert.That(await cluster.UntilConvergedAsync("dashboard/plans", id)).IsTrue();
        var value = await cluster["gru"].ReadAsync("dashboard/plans", id);
        await Assert.That(value).IsNotNull();
        await Assert.That(cluster.Regions.Select(region => region.Name)).Contains(JsonElement.Parse(value!).GetProperty("region").GetString()!);
    }

    [Test]
    public async Task A_catalog_deletion_replicates_as_a_tombstone_that_an_older_concurrent_write_does_not_undo()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction => await transaction.PutAsync("api_keys", id, Json("""{"name":"first"}"""), CancellationToken.None));
        await Assert.That(await sjc.UntilAsync("api_keys", id, json => json is not null)).IsTrue();

        // Cut apart: sjc rewrites a field, then gru deletes the document later (a newer stamp). Neither has seen the other.
        var members = await cluster.SeparateAsync();
        await sjc.WriteAsync(async transaction => await transaction.PatchAsync("api_keys", id, Json("""{"name":"renamed"}"""),
            [new FieldOperation(FieldOperationKind.Set, "name", JsonElement.Parse("\"renamed\""))], CancellationToken.None));
        await Task.Delay(TimeSpan.FromMilliseconds(5));
        await gru.WriteAsync(async transaction => await Assert.That(await transaction.DeleteAsync("api_keys", id, CancellationToken.None)).IsTrue());
        cluster.Rejoin(members);

        // The delete's clear is newer than the rename: the document is gone everywhere, as a tombstone.
        await Assert.That(await sjc.UntilAsync("api_keys", id, json => json is null)).IsTrue();
        foreach (var region in cluster.Regions)
        {
            var version = await region.VersionAsync("api_keys", id);
            await Assert.That(version is { Deleted: true }).IsTrue().Because(region.Name);
        }
    }

    [Test]
    public async Task A_catalog_write_made_while_a_region_was_away_reaches_it_when_it_returns()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc", "fra");
        var (gru, fra) = (cluster["gru"], cluster["fra"]);
        await fra.StopAsync();
        var organization = await gru.Api.CreateOrganizationAsync("While away");
        await Assert.That(await cluster["sjc"].UntilAsync("organizations", organization, json => json is not null)).IsTrue();

        await fra.StartAsync();
        await Assert.That(await fra.UntilAsync("organizations", organization, json => json is not null)).IsTrue();
        await Assert.That(await cluster.UntilConvergedAsync("organizations", organization)).IsTrue();
    }

    [Test]
    public async Task A_database_deleted_in_its_home_goes_from_its_former_replica_and_its_name_serves_a_new_database()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var user = gru.Api.User;
        var (organization, project, database) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.CommitAsync(user,
            new { op = "create_organization", id = organization, name = "Deleting" },
            new { op = "create_project", organization, id = project, name = "p" },
            new { op = "create_database", organization, project, name = "main", id = database, replica_regions = Sjc });
        var tenant = new TenantContext(organization, PlanTier.Hobby, project, database);

        // sjc holds the database gru created once its record replicated, and joins gru's documents into it.
        await Assert.That(await sjc.UntilAsync($"projects/{project}/databases", database, json => json is not null)).IsTrue();
        await Assert.That(await sjc.Tenants.EnsureLocalDatabaseAsync(tenant, CancellationToken.None) is Done).IsTrue();
        await Assert.That(File.Exists(sjc.FileOf(tenant))).IsTrue();
        await gru.WriteAsync(tenant, async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", "one", Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilDocumentAsync(tenant, "items", "one", json => json == """{"n":1}""")).IsTrue();

        // Deleted at home (after its replicas were removed): sjc deletes its file, and the record stays a tombstone.
        await gru.CommitAsync(user, new { op = "set_database_regions", organization, project, database, replica_regions = Array.Empty<string>() });
        await gru.CommitAsync(user, new { op = "delete_database", organization, project, database });
        await Assert.That(await Until(() => Task.FromResult(!File.Exists(sjc.FileOf(tenant))))).IsTrue();
        await Assert.That(await sjc.VersionAsync($"projects/{project}/databases", database) is { Deleted: true }).IsTrue();
        await Assert.That(await sjc.Tenants.RegisteredAsync(tenant, CancellationToken.None) is false).IsTrue();

        // The name serves a new database, created in sjc; the deleted one does not come back anywhere.
        var recreated = EntityIds.New().ToString();
        await sjc.CommitAsync(user, new { op = "create_database", organization, project, name = "main", id = recreated });
        foreach (var region in cluster.Regions)
        {
            await Assert.That(await region.UntilAsync($"projects/{project}/database_names", "main", json => json == $"\"{recreated}\"")).IsTrue().Because(region.Name);
            await Assert.That(await region.VersionAsync($"projects/{project}/databases", database) is { Deleted: true }).IsTrue().Because(region.Name);
        }

        await Assert.That(File.Exists(gru.FileOf(tenant))).IsFalse();
    }

    [Test]
    public async Task Two_regions_creating_one_name_at_once_keep_the_database_of_the_smaller_id_everywhere()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var user = gru.Api.User;
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.CommitAsync(user,
            new { op = "create_organization", id = organization, name = "Racing" },
            new { op = "create_project", organization, id = project, name = "p" });
        await Assert.That(await sjc.UntilAsync($"organizations/{organization}/projects", project, json => json is not null)).IsTrue();

        // Cut apart, each region creates a database named "twin"; the larger id is created last, so its name index write is
        // the newer one, which the catalog's last-writer-wins alone would keep.
        string[] ids = [EntityIds.New().ToString(), EntityIds.New().ToString()];
        Array.Sort(ids, StringComparer.Ordinal);
        var (winner, loser) = (ids[0], ids[1]);
        var members = await cluster.SeparateAsync();
        await sjc.CommitAsync(user, new { op = "create_database", organization, project, name = "twin", id = winner });
        await gru.CommitAsync(user, new { op = "create_database", organization, project, name = "twin", id = loser });
        cluster.Rejoin(members);

        var lost = new TenantContext(organization, PlanTier.Hobby, project, loser);
        foreach (var region in cluster.Regions)
        {
            await Assert.That(await region.UntilAsync($"projects/{project}/database_names", "twin", json => json == $"\"{winner}\"")).IsTrue().Because(region.Name);
            await Assert.That(await Until(async () => await region.VersionAsync($"projects/{project}/databases", loser) is { Deleted: true })).IsTrue().Because(region.Name);
            await Assert.That(await region.ReadAsync($"projects/{project}/databases", winner)).IsNotNull().Because(region.Name);
        }

        await Assert.That(await Until(() => Task.FromResult(!File.Exists(gru.FileOf(lost))))).IsTrue();
        await Assert.That(File.Exists(sjc.FileOf(lost with { DatabaseId = winner }))).IsTrue();
    }

    [Test]
    public async Task Usage_counted_in_two_regions_sums_in_both_and_no_concurrent_flush_loses_a_count()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (organization, project, database) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.CommitAsync(gru.Api.User,
            new { op = "create_organization", id = organization, name = "Metered" },
            new { op = "create_project", organization, id = project, name = "p" },
            new { op = "create_database", organization, project, name = "main", id = database, replica_regions = Sjc });
        await Assert.That(await sjc.UntilAsync($"projects/{project}/databases", database, json => json is not null)).IsTrue();
        var tenant = new TenantContext(organization, PlanTier.Hobby, project, database);

        // Both regions serve and flush the same database's usage at the same time, three rounds each.
        var hour = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600;
        await Task.WhenAll(cluster.Regions.Select(async region =>
        {
            var meter = region.Api.Server.Services.GetRequiredService<UsageMeter>();
            for (var round = 0; round < 3; round++)
            {
                meter.Add(new UsageKey(DatabaseOwner.Of(tenant), hour), new OpCounts(2, 1));
                await meter.FlushAsync(CancellationToken.None);
            }
        }));

        foreach (var region in cluster.Regions)
        {
            var meter = region.Api.Server.Services.GetRequiredService<UsageMeter>();
            await Assert.That(await Until(async () => await meter.GetAsync(tenant, new UsageWindow(UsageWindow.Hour, hour, 1), CancellationToken.None)
                is UsageCounts { Lifetime: { Reads: 12, Writes: 6 }, Series: [{ Reads: 12, Writes: 6 }] })).IsTrue().Because(region.Name);
        }
    }

    /// <summary>
    /// The restore gate (docs/terrabase/system-catalog-invariants.md section 2a). sjc's catalog is restored from a copy taken
    /// before gru revoked an API key, removed a member and deleted a database, while sjc cannot reach gru: sjc's catalog still
    /// holds all three live, yet sjc serves none of it (503 catalog_unverified, not ready) instead of accepting the key, the
    /// member or a creation of the deleted id. Once gru is reachable, sjc pulls the catalog from it and then refuses all three.
    /// </summary>
    [Test]
    public async Task A_restored_catalog_serves_nothing_until_it_pulled_from_every_machine_then_refuses_the_revoked_key_removed_member_and_deleted_id()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (owner, member) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        var (organization, project, removed, kept) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.CommitAsync(owner,
            new { op = "create_organization", id = organization, name = "Gated" },
            new { op = "set_plan", organization, plan = "enterprise" },
            new { op = "create_project", organization, id = project, name = "p" },
            new { op = "create_database", organization, project, name = "removed", id = removed },
            new { op = "create_database", organization, project, name = "kept", id = kept, replica_regions = Sjc });
        var organizationPath = $"/v1/management/organizations/{organization}";
        await Assert.That(await gru.SendAsync(owner, HttpMethod.Put, $"{organizationPath}/members/{member}", new { email = "member@example.com" })).IsEqualTo(HttpStatusCode.OK);
        var (key, secret) = await gru.CreateKeyAsync(owner, $"{organizationPath}/projects/{project}");
        var collections = $"/v1/projects/{project}/databases/kept/collections";

        // sjc serves the key and the member; its catalog is copied while it is stopped.
        await Assert.That(await Until(async () => await sjc.DataStatusAsync(secret, collections) == HttpStatusCode.OK)).IsTrue();
        await Assert.That(await Until(async () => await sjc.SendAsync(member, HttpMethod.Get, organizationPath) == HttpStatusCode.OK)).IsTrue();
        var copy = await sjc.CopyCatalogAsync();

        // gru revokes the key, removes the member and deletes a database; sjc applies all three.
        await Assert.That(await gru.SendAsync(owner, HttpMethod.Delete, $"{organizationPath}/projects/{project}/api-keys/{key}")).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await gru.SendAsync(owner, HttpMethod.Delete, $"{organizationPath}/members/{member}")).IsEqualTo(HttpStatusCode.NoContent);
        await gru.CommitAsync(owner, new { op = "delete_database", organization, project, database = removed });
        await Assert.That(await Until(async () => await sjc.DataStatusAsync(secret, collections) == HttpStatusCode.Unauthorized)).IsTrue();
        await Assert.That(await sjc.UntilAsync($"projects/{project}/databases", removed, json => json is null)).IsTrue();

        // sjc's catalog is restored from the copy, and neither machine can reach the other.
        cluster.Members.Map = (node, other) => (node == sjc.Machine && other.Name == gru.Machine) || (node == gru.Machine && other.Name == sjc.Machine)
            ? other with { PeerAddress = "http://127.0.0.1:9" }
            : other;
        await sjc.RestoreCatalogAsync(copy);
        await Assert.That(await sjc.ReadAsync($"projects/{project}/databases", removed)).IsNotNull().Because("the restored copy holds the deleted database live");
        await Assert.That(await sjc.StatusAsync("/readyz")).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await sjc.DataStatusAsync(secret, collections)).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await sjc.SendAsync(member, HttpMethod.Get, organizationPath)).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var again = new { id = EntityIds.New().ToString(), ops = new object[] { new { op = "create_database", organization, project, name = "again", id = removed } } };
        await Assert.That(await sjc.SendAsync(owner, HttpMethod.Post, "/v1/management/commits", again)).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await sjc.ReadAsync($"projects/{project}/databases", removed)).IsNotNull().Because("nothing repaired sjc yet");

        // gru is reachable again: sjc pulls the catalog from it, serves, and refuses all three.
        cluster.Members.Map = null;
        await Assert.That(await Until(async () => await sjc.StatusAsync("/readyz") == HttpStatusCode.OK)).IsTrue();
        await Assert.That(await sjc.DataStatusAsync(secret, collections)).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await sjc.SendAsync(member, HttpMethod.Get, organizationPath)).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await sjc.SendAsync(owner, HttpMethod.Post, "/v1/management/commits", again)).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await sjc.VersionAsync($"projects/{project}/databases", removed) is { Deleted: true }).IsTrue();
    }

    /// <summary>
    /// A machine that cannot reach a started machine stays closed, however long; only an operator opens it, with a reason,
    /// and the override is recorded in the catalog.
    /// </summary>
    [Test]
    public async Task An_unreachable_machine_keeps_the_gate_closed_until_an_operator_opens_it_with_a_recorded_reason()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        cluster.Members.Map = (node, other) => node == sjc.Machine && other.Name == gru.Machine ? other with { PeerAddress = "http://127.0.0.1:9" } : other;
        await sjc.StopAsync();
        await sjc.StartAsync();
        await Assert.That(await sjc.StatusAsync("/readyz")).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await sjc.SendAsync(gru.Api.User, HttpMethod.Get, "/v1/management/organizations")).IsEqualTo(HttpStatusCode.ServiceUnavailable);

        await Assert.That(await sjc.AdminAsync("/admin/catalog-gate/open", new { reason = "" })).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await sjc.StatusAsync("/readyz")).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await sjc.AdminAsync("/admin/catalog-gate/open", new { reason = "gru lost its volume; ticket 42" })).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await sjc.StatusAsync("/readyz")).IsEqualTo(HttpStatusCode.OK);
        var store = await sjc.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var overrides = await transaction.ListAsync($"machines/{sjc.Machine}/catalog_gate_overrides", null, 10, 1024 * 1024, CancellationToken.None);
        var recorded = JsonDocument.Parse(overrides.Documents.Single().Json).RootElement;
        await Assert.That(recorded.GetProperty("reason").GetString()).IsEqualTo("gru lost its volume; ticket 42");
        await Assert.That(recorded.GetProperty("unverified").EnumerateArray().Select(item => item.GetString()!).ToArray()).IsEquivalentTo([gru.Machine]);
    }

    // Whether `condition` holds within the deadline.
    private static async Task<bool> Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    private sealed class Cluster : IAsyncDisposable
    {
        private readonly Dictionary<string, Region> _regions = [with(StringComparer.Ordinal)];

        public FakeMemberStore Members { get; } = new();

        public IEnumerable<Region> Regions => _regions.Values;

        public Region this[string name] => _regions[name];

        public static async Task<Cluster> StartAsync(params string[] regions)
        {
            var cluster = new Cluster();
            try
            {
                foreach (var name in regions)
                {
                    var region = new Region(cluster, name);
                    cluster._regions[name] = region;
                    await region.StartAsync();
                }

                return cluster;
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }
        }

        // Unlists every machine and waits until no machine streams to another; Rejoin lists them again.
        public async Task<List<ClusterMember>> SeparateAsync()
        {
            var members = (await Members.ListAsync(CancellationToken.None)).ToList();
            members.ForEach(member => Members.Leave(member.Name));
            foreach (var region in Regions)
            {
                await Assert.That(await Until(() => Task.FromResult(region.Api.Server.Services.GetRequiredService<IReplicationStatus>().Snapshot().Peers.Count == 0)))
                    .IsTrue().Because(region.Name);
            }

            return members;
        }

        public void Rejoin(List<ClusterMember> members) => members.ForEach(Members.Join);

        // Whether every running region holds the same stored document (or none) at the same version within the deadline.
        public async Task<bool> UntilConvergedAsync(string collection, string id)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                var stored = await Task.WhenAll(Regions.Select(region => region.ReadStoredAsync(collection, id)));
                if (stored.Select(document => document is null ? "" : $"{document.Version}|{Encoding.UTF8.GetString(document.Json.Span)}").Distinct().Count() == 1)
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var region in _regions.Values)
            {
                await region.DisposeAsync();
            }
        }
    }

    private sealed class Region(Cluster cluster, string name) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-catalog-lattice-").FullName;
        private ManagementApi? _api;

        public string Name { get; } = name;

        public string Machine { get; } = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{Name} is stopped");

        public async Task StartAsync() =>
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = Name;
                settings["node-name"] = Machine;
                settings["data-directory"] = _directory;
            }, services => services.AddSingleton<IMemberDirectory>(cluster.Members));

        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
        }

        public ITenantDirectory Tenants => Api.Server.Services.GetRequiredService<ITenantDirectory>();

        // Stops this machine, copies its catalog files (_system.db and its sidecars) and starts it again.
        public async Task<string> CopyCatalogAsync()
        {
            var copy = Directory.CreateTempSubdirectory("terrabase-catalog-copy-").FullName;
            await StopAsync();
            foreach (var file in Directory.EnumerateFiles(_directory, "_system*"))
            {
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            }

            await StartAsync();
            return copy;
        }

        // Stops this machine, puts the catalog files of `copy` in place of its own, and starts it again.
        public async Task RestoreCatalogAsync(string copy)
        {
            await StopAsync();
            foreach (var file in Directory.EnumerateFiles(_directory, "_system*"))
            {
                File.Delete(file);
            }

            foreach (var file in Directory.EnumerateFiles(copy))
            {
                File.Copy(file, Path.Combine(_directory, Path.GetFileName(file)));
            }

            Directory.Delete(copy, recursive: true);
            await StartAsync();
        }

        // A request as `user` through the platform operator; its status.
        public async Task<HttpStatusCode> SendAsync(string user, HttpMethod method, string path, object? body = null)
        {
            using var service = Api.Service(user);
            using var request = new HttpRequestMessage(method, ManagementApi.Uri(path)) { Content = body is null ? null : JsonContent.Create(body) };
            using var response = await service.SendAsync(request);
            return response.StatusCode;
        }

        // An internal-listener request without a user (administration); its status.
        public async Task<HttpStatusCode> AdminAsync(string path, object body)
        {
            using var response = await Api.Server.Client.PostAsJsonAsync(ManagementApi.Uri(path), body);
            return response.StatusCode;
        }

        public async Task<HttpStatusCode> StatusAsync(string path)
        {
            using var response = await Api.Server.Client.GetAsync(ManagementApi.Uri(path));
            return response.StatusCode;
        }

        // A data API GET with an API key on the client listener; its status.
        public async Task<HttpStatusCode> DataStatusAsync(string secret, string path)
        {
            using var data = Api.DataClient(secret);
            using var response = await data.GetAsync(ManagementApi.Uri(path));
            return response.StatusCode;
        }

        // A project API key made by `user`: its id and secret.
        public async Task<(string Id, string Secret)> CreateKeyAsync(string user, string projectPath)
        {
            using var service = Api.Service(user);
            using var response = await service.PostAsJsonAsync(ManagementApi.Uri($"{projectPath}/api-keys"), new { name = "gated", scopes = ReadScope });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (body.GetProperty("key").GetProperty("id").GetString()!, body.GetProperty("secret").GetString()!);
        }

        // The file a client database is stored in on this machine.
        public string FileOf(TenantContext database) =>
            Path.Combine(_directory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");

        // Sends one management commit as `user`, through the platform operator.
        public async Task CommitAsync(string user, params object[] ops)
        {
            using var service = Api.Service(user);
            using var response = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new { id = EntityIds.New().ToString(), ops });
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        }

        // Writes into this machine's copy of a client database.
        public async Task WriteAsync(TenantContext database, Func<IStorageTransaction, Task> work)
        {
            var store = await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException($"{Name} does not hold the database");
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            await work(transaction);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        // Whether a client document's JSON (null when there is none) satisfies `expected` within the deadline.
        public Task<bool> UntilDocumentAsync(TenantContext database, string collection, string id, Func<string?, bool> expected) =>
            Until(async () =>
            {
                if (await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None) is not { } store)
                {
                    return expected(null);
                }

                await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
                return expected(await transaction.GetAsync(collection, id, CancellationToken.None) is { } document ? Encoding.UTF8.GetString(document.Json.Span) : null);
            });

        private Task<IDatabaseStore> SystemAsync() =>
            Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None).AsTask();

        public async Task WriteAsync(Func<IStorageTransaction, Task> work)
        {
            var store = await SystemAsync();
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            await work(transaction);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        public async Task<StoredDocument?> ReadStoredAsync(string collection, string id)
        {
            var store = await SystemAsync();
            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            return await transaction.GetAsync(collection, id, CancellationToken.None);
        }

        public async Task<string?> ReadAsync(string collection, string id) =>
            await ReadStoredAsync(collection, id) is { } document ? Encoding.UTF8.GetString(document.Json.Span) : null;

        public async Task<DocumentVersion?> VersionAsync(string collection, string id)
        {
            var store = await SystemAsync();
            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            return await transaction.GetVersionAsync(collection, id, CancellationToken.None);
        }

        // Whether the document's JSON (null when there is none) satisfies `expected` within the deadline.
        public async Task<bool> UntilAsync(string collection, string id, Func<string?, bool> expected)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                if (expected(await ReadAsync(collection, id)))
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (_api is not null)
            {
                await _api.DisposeAsync();
            }

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A file the stopped server still held: the temporary directory is left for the OS to clean.
            }
        }
    }
}
