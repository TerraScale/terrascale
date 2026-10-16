using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
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

    [Test]
    public async Task Catalog_replication_frontiers_and_stability_use_the_system_store()
    {
        await using var cluster = await Cluster.StartAsync("gru");
        var region = cluster["gru"];
        var organization = await region.Api.CreateOrganizationAsync("System frontier");
        var services = region.Api.Server.Services;
        var source = services.GetRequiredService<IChangeGroupSource>();
        var states = services.GetRequiredService<IReplicaStates>();
        var sink = services.GetRequiredService<IChangeGroupSink>();
        var peer = ReplicaId.Of(1, 9, 0);
        var frontier = FeedCursor.At(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2));

        await sink.RecordFrontierAsync(ChangeGroups.System, peer, frontier, CancellationToken.None);
        var incarnation = (await states.DigestAsync(ChangeGroups.System, null, CancellationToken.None))!.Incarnation;
        await Assert.That(await source.RecordStableAsync(ChangeGroups.System, Ulid.NewUlid(), peer, frontier, CancellationToken.None)).IsFalse()
            .Because("a stable report for another copy of the catalog is refused");
        await Assert.That(await source.RecordStableAsync(ChangeGroups.System, incarnation, peer, frontier, CancellationToken.None)).IsTrue();

        await Assert.That(await states.StableAsync(ChangeGroups.System, CancellationToken.None)).IsEqualTo(frontier);
        await Assert.That((await states.HorizonAsync(ChangeGroups.System, CancellationToken.None)).Value).IsGreaterThan(0UL);
        await Assert.That(await states.ForgetAsync(ChangeGroups.System, incarnation, [("organizations", organization)], default, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await region.ReadAsync("organizations", organization)).IsNotNull();
    }

    // Every mutating route not anchored before it acknowledges: grants, creations, renames, preferences, reads through POST,
    // restores into new databases, tenant data, and two that anchor themselves: the management commit (per operation) and
    // setting a backup destination (when it replaces one).
    private static readonly string[] NotAnchored =
    [
        "DELETE /v1/projects/{project}/databases/{database}/collections/{collection}",
        "DELETE /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key",
        "DELETE /v1/projects/{project}/databases/{database}/collections/{collection}/schema",
        "DELETE /v1/projects/{project}/databases/{database}/indexes/{index}",
        "PATCH /v1/management/organizations/{organization}",
        "PATCH /v1/management/organizations/{organization}/projects/{project}",
        "PATCH /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key",
        "POST /admin/api-keys",
        "POST /v1/management/commits",
        "POST /v1/management/documents/{collection}/query",
        "POST /v1/management/invitations/accept",
        "POST /v1/management/invitations/lookup",
        "POST /v1/management/organizations",
        "POST /v1/management/organizations/{organization}/invitations",
        "POST /v1/management/organizations/{organization}/projects",
        "POST /v1/management/organizations/{organization}/projects/{project}/api-keys",
        "POST /v1/management/organizations/{organization}/projects/{project}/binding",
        "POST /v1/projects/{project}/databases",
        "POST /v1/projects/{project}/databases/{database}/backups",
        "POST /v1/projects/{project}/databases/{database}/backups/{backup}/restore",
        "POST /v1/projects/{project}/databases/{database}/branches",
        "POST /v1/projects/{project}/databases/{database}/collections",
        "POST /v1/projects/{project}/databases/{database}/collections/{collection}/documents",
        "POST /v1/projects/{project}/databases/{database}/collections/{collection}/query",
        "POST /v1/projects/{project}/databases/{database}/index-recommendations/{recommendation}/apply",
        "POST /v1/projects/{project}/databases/{database}/indexes",
        "POST /v1/projects/{project}/databases/{database}/point-in-time/plan",
        "POST /v1/projects/{project}/databases/{database}/point-in-time/restore",
        "POST /v1/projects/{project}/databases/{database}/sql",
        "POST /v1/projects/{project}/databases/{database}/sql/check",
        "POST /v1/projects/{project}/databases/{database}/sql/explain",
        "POST /v1/projects/{project}/databases/{database}/transactions",
        "POST /v1/projects/{project}/saved-queries",
        "PUT /v1/management/current-organization",
        // Retention, not access: a shortened retention lost to a restore lists older audit events again and grants nothing.
        "PUT /v1/management/organizations/{organization}/audit-retention",
        "PUT /v1/management/organizations/{organization}/members/{user}",
        "PUT /v1/projects/{project}/backup-destination",
        "PUT /v1/projects/{project}/databases/{database}",
        "PUT /v1/projects/{project}/databases/{database}/auto-index/{collection}",
        "PUT /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key",
        "PUT /v1/projects/{project}/databases/{database}/collections/{collection}/schema",
        "PUT /v1/projects/{project}/databases/{database}/schema/{collection}",
        "PUT /v1/projects/{project}/saved-queries/{query}",
    ];

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

        // Unlists every machine, so no machine streams to another; Rejoin lists them again.
        public async Task<List<ClusterMember>> SeparateAsync()
        {
            var members = (await Members.ListAsync(CancellationToken.None)).ToList();
            members.ForEach(member => Members.Leave(member.Name));
            // A heartbeat (1 s) for every stream to notice it lost its peer.
            await Task.Delay(TimeSpan.FromSeconds(2));
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

        // The ids of a catalog collection's live documents on this machine.
        public async Task<string[]> IdsAsync(string collection)
        {
            var store = await SystemAsync();
            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            var page = await transaction.ListAsync(collection, null, 100, 1024 * 1024, CancellationToken.None);
            return [.. page.Documents.Select(document => document.Id).Order(StringComparer.Ordinal)];
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
