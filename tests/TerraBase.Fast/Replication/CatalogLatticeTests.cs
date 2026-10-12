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
        private readonly string _machine = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-catalog-lattice-").FullName;
        private ManagementApi? _api;

        public string Name { get; } = name;

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{Name} is stopped");

        public async Task StartAsync() =>
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = Name;
                settings["node-name"] = _machine;
                settings["data-directory"] = _directory;
            }, services => services.AddSingleton<IMemberDirectory>(cluster.Members));

        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
        }

        public ITenantDirectory Tenants => Api.Server.Services.GetRequiredService<ITenantDirectory>();

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
