using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
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

        await sink.RecordFrontierAsync(ChangeGroups.System, peer, frontier, default, null, CancellationToken.None);
        var incarnation = (await states.DigestAsync(ChangeGroups.System, null, CancellationToken.None))!.Incarnation;
        await Assert.That(await source.RecordStableAsync(ChangeGroups.System, Ulid.NewUlid(), peer, frontier, CancellationToken.None)).IsFalse()
            .Because("a stable report for another copy of the catalog is refused");
        await Assert.That(await source.RecordStableAsync(ChangeGroups.System, incarnation, peer, frontier, CancellationToken.None)).IsTrue();

        await Assert.That(await states.StableAsync(ChangeGroups.System, CancellationToken.None)).IsEqualTo(frontier);
        await Assert.That((await states.HorizonAsync(ChangeGroups.System, CancellationToken.None)).Value).IsGreaterThan(0UL);
        await Assert.That(await states.ForgetAsync(ChangeGroups.System, incarnation, [("organizations", organization)], default, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await region.ReadAsync("organizations", organization)).IsNotNull();
    }

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
        await Assert.That(await cluster["fra"].UntilAsync($"users/{cluster["gru"].Api.User}/organization_names", "replicated", json => json == $"\"{organization}\"")).IsTrue();
    }

    [Test]
    public async Task Concurrent_writes_of_one_catalog_document_in_every_region_converge_to_one_value()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc", "fra");
        var id = EntityIds.New().ToString();
        await Task.WhenAll(cluster.Regions.Select(region => region.WriteAsync(async transaction =>
            await transaction.PutAsync("catalog_fixture/plans", id, Json($$"""{"region":"{{region.Name}}","shared":1}"""), CancellationToken.None))));

        await Assert.That(await cluster.UntilConvergedAsync("catalog_fixture/plans", id)).IsTrue();
        var value = await cluster["gru"].ReadAsync("catalog_fixture/plans", id);
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
        var renamedVersion = (await sjc.VersionAsync("api_keys", id))!.Value.Version;
        gru.Api.Server.Services.GetRequiredService<LocalReplica>().Clock.Observe(new Hlc(checked((ulong)renamedVersion + 1)));
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
        await CatalogFixture.RegisterAsync(gru.Api, user, new TenantContext(organization, PlanTier.Hobby, project, database), "main", Sjc);
        var tenant = new TenantContext(organization, PlanTier.Hobby, project, database);

        await Assert.That(await sjc.UntilAsync($"projects/{project}/databases", database, json => json is not null)).IsTrue();
        await Assert.That(await sjc.Tenants.EnsureLocalDatabaseAsync(tenant, CancellationToken.None) is Done).IsTrue();
        await Assert.That(File.Exists(sjc.FileOf(tenant))).IsTrue();
        await gru.WriteAsync(tenant, async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", "one", Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilDocumentAsync(tenant, "items", "one", json => json == """{"n":1}""")).IsTrue();

        using var replicaOwner = sjc.Api.As(user);
        var path = ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases/{database}");
        using var wrongRegion = await replicaOwner.DeleteAsync(path);
        await Assert.That(wrongRegion.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        using var deleted = await gru.Api.Client.DeleteAsync(path);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync());
        await Assert.That(await Until(() => Task.FromResult(!File.Exists(sjc.FileOf(tenant))))).IsTrue();
        await Assert.That(await sjc.VersionAsync($"projects/{project}/databases", database) is { Deleted: true }).IsTrue();
        await Assert.That(await sjc.Tenants.RegisteredAsync(tenant, CancellationToken.None) is false).IsTrue();
        await Assert.That(await sjc.UntilAsync($"projects/{project}/database_names", "main", json => json is null)).IsTrue();

        var recreated = EntityIds.New().ToString();
        await CatalogFixture.RegisterAsync(sjc.Api, user, tenant with { DatabaseId = recreated }, "main", []);
        foreach (var region in cluster.Regions)
        {
            await Assert.That(await region.UntilAsync($"projects/{project}/database_names", "main", json => json == $"\"{recreated}\"")).IsTrue().Because(region.Name);
            await Assert.That(await region.VersionAsync($"projects/{project}/databases", database) is { Deleted: true }).IsTrue().Because(region.Name);
        }

        await Assert.That(File.Exists(gru.FileOf(tenant))).IsFalse();
    }

    // docs/terrabase/database-name-conflicts.md: a partition lets both sides take one name and write; healing keeps both.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_name_taken_on_both_sides_of_a_partition_keeps_both_databases_and_their_data_under_unique_names(bool restartLoserHome)
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var user = gru.Api.User;
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await CatalogFixture.RegisterProjectAsync(gru.Api, user, organization, project);
        await Assert.That(await sjc.UntilAsync($"organizations/{organization}/projects", project, json => json is not null)).IsTrue();

        string[] ids = [EntityIds.New().ToString(), EntityIds.New().ToString()];
        Array.Sort(ids, StringComparer.Ordinal);
        var (winner, loser) = (new TenantContext(organization, PlanTier.Hobby, project, ids[0]), new TenantContext(organization, PlanTier.Hobby, project, ids[1]));
        var members = await cluster.SeparateAsync();
        // Isolated HTTP writes can refuse remote durability; seed the two already-committed catalog decisions directly.
        await sjc.SeedDatabaseAsync(winner, "twin");
        var winningVersion = (await sjc.VersionAsync($"projects/{project}/database_names", "twin"))!.Value.Version;
        gru.Api.Server.Services.GetRequiredService<LocalReplica>().Clock.Observe(new Hlc(checked((ulong)winningVersion + 1)));
        await gru.SeedDatabaseAsync(loser, "twin");
        await Assert.That((await gru.VersionAsync($"projects/{project}/database_names", "twin"))!.Value.Version).IsGreaterThan(winningVersion);
        await WriteItemAsync(sjc, winner, "kept by the winner");
        await WriteItemAsync(gru, loser, "kept by the loser");

        cluster.Rejoin(members);
        if (restartLoserHome)
        {
            // The loser's home restarts mid-convergence, whether or not it moved the loser yet: the outcome is the same.
            await Assert.That(await sjc.UntilAsync($"projects/{project}/databases", loser.DatabaseId!, json => json is not null)).IsTrue();
            await gru.StopAsync();
            await gru.StartAsync();
        }

        var renamed = $"twin-conflict-{loser.DatabaseId![^6..].ToLowerInvariant()}";
        foreach (var region in cluster.Regions)
        {
            await Assert.That(await region.UntilAsync($"projects/{project}/database_names", "twin", json => json == $"\"{winner.DatabaseId}\"")).IsTrue().Because(region.Name);
            await Assert.That(await region.UntilAsync($"projects/{project}/database_names", renamed, json => json == $"\"{loser.DatabaseId}\"")).IsTrue().Because(region.Name);
            await Assert.That(await region.UntilAsync($"projects/{project}/databases", loser.DatabaseId!, json => json is not null
                && JsonElement.Parse(json) is var record && record.GetProperty("name").GetString() == renamed
                && record.GetProperty("previousName").GetString() == "twin" && record.GetProperty("conflictOf").GetString() == winner.DatabaseId)).IsTrue().Because(region.Name);
            await Assert.That(JsonElement.Parse((await region.ReadAsync($"projects/{project}/databases", winner.DatabaseId!))!).GetProperty("name").GetString()).IsEqualTo("twin");
            // The name index matches the records: one entry per database, each under its record's name.
            await Assert.That(await Until(async () => (await region.IdsAsync($"projects/{project}/database_names")).SequenceEqual(new[] { "twin", renamed }.Order(StringComparer.Ordinal))))
                .IsTrue().Because(region.Name);
        }

        foreach (var (collection, id) in new[] { ("databases", winner.DatabaseId!), ("databases", loser.DatabaseId!), ("database_names", "twin"), ("database_names", renamed) })
        {
            await Assert.That(await cluster.UntilConvergedAsync($"projects/{project}/{collection}", id)).IsTrue().Because($"{collection} {id}");
        }

        await Assert.That(await gru.UntilDocumentAsync(loser, "items", "one", json => json == """{"by":"kept by the loser"}""")).IsTrue();
        await Assert.That(await sjc.UntilDocumentAsync(winner, "items", "one", json => json == """{"by":"kept by the winner"}""")).IsTrue();
        await Assert.That(File.Exists(gru.FileOf(loser))).IsTrue();

        // By id the loser answers under its new name; the lost name serves the winner only.
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        using var owner = gru.Api.As(user);
        var byId = await owner.GetFromJsonAsync<JsonElement>(ManagementApi.Uri($"{databases}/{loser.DatabaseId}"));
        await Assert.That(byId.GetProperty("name").GetString()).IsEqualTo(renamed);
        await Assert.That(byId.GetProperty("conflictOf").GetString()).IsEqualTo(winner.DatabaseId);
        await Assert.That(await gru.Tenants.FindDatabaseAsync(organization, project, "twin", CancellationToken.None) is TenantContext { DatabaseId: var found } && found == winner.DatabaseId).IsTrue();

    }

    // Both regions accept the same name over HTTP at the same moment: whatever each accepted survives everywhere.
    [Test]
    public async Task Concurrent_creates_of_one_name_in_two_regions_never_delete_an_accepted_database()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var user = gru.Api.User;
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await CatalogFixture.RegisterProjectAsync(gru.Api, user, organization, project);
        await Assert.That(await sjc.UntilAsync($"organizations/{organization}/projects", project, json => json is not null)).IsTrue();

        var path = ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases");
        using var sjcUser = sjc.Api.As(user);
        var responses = await Task.WhenAll(gru.Api.Client.PostAsJsonAsync(path, new { name = "same" }), sjcUser.PostAsJsonAsync(path, new { name = "same" }));
        string[] accepted = [.. await Task.WhenAll(responses.Where(response => response.StatusCode == HttpStatusCode.Created)
            .Select(async response => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!))];
        await Assert.That(accepted).IsNotEmpty();
        foreach (var response in responses)
        {
            await Assert.That(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict).IsTrue().Because(await response.Content.ReadAsStringAsync());
            response.Dispose();
        }

        foreach (var region in cluster.Regions)
        {
            // Every accepted database is live, each under its own name, and the index holds exactly those names.
            await Assert.That(await Until(async () =>
            {
                var records = await Task.WhenAll(accepted.Select(id => region.ReadAsync($"projects/{project}/databases", id)));
                if (records.Any(json => json is null))
                {
                    return false;
                }

                var names = records.Select(json => JsonElement.Parse(json!).GetProperty("name").GetString()!).ToArray();
                var index = await Task.WhenAll(names.Select(name => region.ReadAsync($"projects/{project}/database_names", name)));
                return names.Distinct().Count() == accepted.Length && names.Contains("same")
                    && index.Select((json, i) => json == $"\"{accepted[i]}\"").All(matches => matches)
                    && (await region.IdsAsync($"projects/{project}/database_names")).Length == accepted.Length;
            })).IsTrue().Because(region.Name);
        }
    }

    private static Task WriteItemAsync(Region region, TenantContext database, string by) =>
        region.WriteAsync(database, async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", "one", Json($$"""{"by":"{{by}}"}"""), CancellationToken.None);
        });

    // docs/terrabase/project-closure.md: a fence written in one region is not acceptance. A database another region
    // registered before the fence reached it keeps the closure pending, and survives, until its own deletion.
    [Test]
    public async Task A_project_closure_is_accepted_only_once_every_holder_confirmed_the_project_empty_under_the_fence()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        foreach (var region in cluster.Regions)
        {
            await Assert.That(await Until(async () => (await region.IdsAsync(CatalogGate.Members)).Length == 2)).IsTrue().Because(region.Name);
        }

        var (organization, project, database, closure) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        var members = await cluster.SeparateAsync();
        // sjc registers a database of the project before any fence reaches it: a creation racing the closure.
        await sjc.SeedDatabaseAsync(new TenantContext(organization, PlanTier.Hobby, project, database), "raced");
        await Assert.That(await CloseAsync(gru, project, closure)).IsEqualTo(HttpStatusCode.Accepted);
        cluster.Rejoin(members);

        // sjc confirms under the fence that it holds a database: still pending, and the database stays.
        await Assert.That(await sjc.UntilAsync("project_closures", project, json => json is not null)).IsTrue();
        var receipts = $"project_closure_receipts/{project}";
        await Assert.That(await Until(async () =>
        {
            var held = await gru.IdsAsync(receipts);
            var bodies = await Task.WhenAll(held.Select(id => gru.ReadAsync(receipts, id)));
            return held.Length == 2 && bodies.Any(json => json is not null && json.Contains("\"empty\":false", StringComparison.Ordinal));
        })).IsTrue();
        await Assert.That(await CloseAsync(gru, project, closure)).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(await sjc.ReadAsync($"projects/{project}/databases", database)).IsNotNull();

        // Its customer deletes it in its home region; then every holder confirms the project empty and gru accepts.
        await sjc.WriteAsync(async transaction => _ = await transaction.DeleteAsync($"projects/{project}/databases", database, CancellationToken.None));
        await Assert.That(await Until(async () => await CloseAsync(gru, project, closure) == HttpStatusCode.OK)).IsTrue();
        await Assert.That(await CloseAsync(sjc, project, closure)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Account_erasure_waits_for_every_holder_and_purges_listed_and_all_region_databases()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc", "fra");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        foreach (var region in cluster.Regions)
        {
            await Assert.That(await Until(async () => (await region.Api.Server.Services.GetRequiredService<IRemoteDurability>()
                .OtherRegionsAsync(CancellationToken.None)).Count == 2)).IsTrue();
        }

        var user = gru.Api.User;
        var organization = await gru.Api.CreateOrganizationAsync("Erasing");
        await gru.Api.SetPlanAsync(gru.Api.User, organization, "starter");
        var project = await gru.Api.CreateProjectAsync(organization, "erased");
        var other = await gru.Api.CreateProjectAsync(organization, "retained");
        var live = await gru.Api.CreateManagedDatabaseAsync(organization, other, "live");
        var databases = new List<ManagedDatabaseRef>();
        using var data = await gru.Api.CreateClientAsync(organization, "documents:read", "documents:write");
        foreach (var all in new[] { false, true })
        {
            var name = all ? "everywhere" : "listed";
            object request = all ? new { name, allRegions = true } : new { name, replicaRegions = Sjc };
            using var created = await gru.Api.Client.PostAsJsonAsync(ManagementApi.Uri(
                $"/v1/management/organizations/{organization}/projects/{project}/databases"), request);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            var database = new ManagedDatabaseRef(organization, project, body.GetProperty("id").GetString()!, name);
            databases.Add(database);
            await ManagementApi.CreateCollectionAsync(data, database, "items");
            using var content = new StringContent("""{"n":1}""", Encoding.UTF8, "application/json");
            using var written = await data.PutAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "one")), content);
            await Assert.That(written.IsSuccessStatusCode).IsTrue().Because(await written.Content.ReadAsStringAsync());
            var tenant = new TenantContext(organization, PlanTier.Starter, project, database.Id);
            await Assert.That(await sjc.UntilDocumentAsync(tenant, "items", ManagementApi.DocumentId("one"), json => json == """{"n":1}""")).IsTrue();
            if (all)
            {
                await Assert.That(await cluster["fra"].UntilDocumentAsync(tenant, "items", ManagementApi.DocumentId("one"), json => json == """{"n":1}""")).IsTrue();
            }
        }

        var closure = EntityIds.New().ToString();
        await ErrorAssert.ErrorAsync(gru.Api.Server.Client.PutAsJsonAsync(ManagementApi.Uri($"/admin/projects/{project}/closure"),
            new { version = 1, closureId = closure, authorityOrganizationId = organization }), HttpStatusCode.Conflict, "not_empty", "delete what it contains first");
        await Assert.That(await gru.ReadAsync("project_closures", project)).IsNull();
        foreach (var region in cluster.Regions)
        {
            await Assert.That(await Until(async () => (await region.IdsAsync(CatalogGate.Members)).Length == 3)).IsTrue();
        }

        await sjc.StopAsync();
        await Assert.That(await CloseAsync(gru, project, closure, organization, 2)).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(await Until(() => Task.FromResult(databases.All(database =>
            !File.Exists(gru.FileOf(new TenantContext(organization, PlanTier.Starter, project, database.Id))))))).IsTrue();
        // Local cleanup alone cannot accept: the offline holder still owns actual replicated files.
        await Assert.That(databases.All(database => File.Exists(sjc.FileOf(new TenantContext(organization, PlanTier.Starter, project, database.Id))))).IsTrue();
        await gru.StopAsync();
        await gru.StartAsync();
        await Assert.That(await gru.ReadAsync(ProjectClosure.Collection, project)).IsNotNull();
        var resumed = await CloseAsync(gru, project, closure, organization, 2);
        await Assert.That(resumed is HttpStatusCode.Accepted or HttpStatusCode.ServiceUnavailable).IsTrue();
        await sjc.StartAsync();
        await Assert.That(await Until(async () => await CloseAsync(gru, project, closure, organization, 2) == HttpStatusCode.OK)).IsTrue();
        foreach (var region in cluster.Regions)
        {
            await Assert.That(await Until(async () => await CloseAsync(region, project, closure, organization, 2) == HttpStatusCode.OK)).IsTrue();
            using var owner = region.Api.As(user);
            foreach (var database in databases)
            {
                var tenant = new TenantContext(organization, PlanTier.Starter, project, database.Id);
                await Assert.That(File.Exists(region.FileOf(tenant))).IsFalse();
                await Assert.That(await region.Tenants.RegisteredAsync(tenant, CancellationToken.None) is false).IsTrue();
                await Assert.That(await Until(async () => (await region.IdsAsync(DatabasePurge.Receipts(database.Id))).Length == 3)).IsTrue();
                using var gone = await owner.GetAsync(ManagementApi.Uri(database.Path));
                await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            using var retained = await owner.GetAsync(ManagementApi.Uri(live.Path));
            await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await CloseAsync(gru, project, EntityIds.New().ToString(), organization, 2)).IsEqualTo(HttpStatusCode.Conflict);
        using var survivingOwner = gru.Api.As(user);
        using var late = await survivingOwner.PostAsJsonAsync(ManagementApi.Uri(databases[0].ProjectPath + "/databases"), new { name = "late" });
        await Assert.That(late.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static async Task<HttpStatusCode> CloseAsync(Region region, string project, string closure, string? organization = null, int version = 1)
    {
        using var response = await region.Api.Server.Client.PutAsJsonAsync(ManagementApi.Uri($"/admin/projects/{project}/closure"),
            new { version, closureId = closure, authorityOrganizationId = organization ?? project });
        return response.StatusCode;
    }

    [Test]
    public async Task New_machines_opening_together_seed_the_platform_databases_once_and_identically()
    {
        await using var cluster = await Cluster.StartSeededWithAnchorsAsync("gru", "sjc", "fra");
        var organization = PlatformDatabases.OrganizationId;
        (string Collection, string Id)[] seeded =
        [
            ("organizations", organization),
            .. PlatformDatabases.Databases.Values.SelectMany(database => new (string, string)[]
            {
                ("project_organizations", database.ProjectId),
                ($"organizations/{organization}/projects", database.ProjectId),
                ($"projects/{database.ProjectId}/databases", database.Id),
            }),
        ];
        foreach (var (collection, id) in seeded)
        {
            foreach (var region in cluster.Regions)
            {
                await Assert.That(await region.UntilAsync(collection, id, json => json is not null)).IsTrue().Because($"{region.Name} {collection}");
            }

            await Assert.That(await cluster.UntilConvergedAsync(collection, id)).IsTrue().Because(collection);
        }

        foreach (var region in cluster.Regions)
        {
            await Assert.That(await region.IdsAsync($"organizations/{organization}/projects"))
                .IsEquivalentTo(PlatformDatabases.Databases.Values.Select(database => database.ProjectId)).Because(region.Name);
            foreach (var (id, projectId) in PlatformDatabases.Databases.Values)
            {
                await Assert.That(await region.IdsAsync($"projects/{projectId}/databases")).IsEquivalentTo([id]).Because(region.Name);
            }

            await Assert.That(await Until(() => Task.FromResult(region.Api.Server.Services.GetRequiredService<ICatalogGate>().IsOpen))).IsTrue().Because(region.Name);
        }
    }

    [Test]
    public async Task A_cleanup_interrupted_after_its_file_is_deleted_retries_and_preserves_permanent_deletion()
    {
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        var fail = 1;
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (step == "cleanup.files" && Interlocked.Exchange(ref fail, 0) == 1)
                {
                    throw new IOException("interrupted after file deletion");
                }
            }), time: clock);
        var database = await api.CreateManagedDatabaseAsync();
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        using var deleted = await api.Client.DeleteAsync(ManagementApi.Uri(
            $"/v1/management/organizations/{database.Organization}/projects/{database.Project}/databases/{database.Id}"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await engine.OpenDatabaseAsync(tenant, CancellationToken.None)).IsNull();
        var system = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var read = await system.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await read.GetAsync("pending_database_cleanup", database.Id, CancellationToken.None)).IsNotNull();
        }

        // Await the whole pass: intent deletion commits before its in-memory reservation is released.
        await api.Server.Services.GetRequiredService<EngineManagementService>().ConvergeAsync(CancellationToken.None);
        await using (var read = await system.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await read.GetAsync("pending_database_cleanup", database.Id, CancellationToken.None)).IsNull();
        }
        await Assert.That(await engine.VerifyDatabaseErasureAsync(tenant, CancellationToken.None)).IsTrue();
        await Assert.That(async () => { _ = await engine.CreateDatabaseAsync(tenant, CancellationToken.None); }).Throws<IOException>();
    }

    [Test]
    public async Task A_deleted_database_with_an_uncached_corrupt_file_is_cleaned_without_touching_live_or_unknown_files()
    {
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        await using var api = await ManagementApi.StartAsync(time: clock);
        var database = await api.CreateManagedDatabaseAsync();
        var live = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "live");
        var deleted = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var retained = deleted with { DatabaseId = live.Id };
        var unknown = deleted with { DatabaseId = EntityIds.New().ToString() };
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await Assert.That(await engine.CreateDatabaseAsync(unknown, CancellationToken.None)).IsTrue();
        var corrupt = Json("not a database file");
        foreach (var tenant in new[] { deleted, retained, unknown })
        {
            // Remove the cached store before replacing the owned file; cleanup must not need to open its corrupt bytes.
            await Assert.That(await engine.DeleteDatabaseAsync(tenant, CancellationToken.None)).IsTrue();
            await File.WriteAllBytesAsync(PathOf(tenant), corrupt);
        }

        var system = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var write = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.That(await write.DeleteAsync($"projects/{database.Project}/databases", database.Id, CancellationToken.None)).IsTrue();
            _ = await write.CommitAsync(CancellationToken.None);
        }

        await using (var read = await system.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await read.GetVersionAsync($"projects/{database.Project}/databases", database.Id, CancellationToken.None) is { Deleted: true }).IsTrue();
        }

        // Await the whole pass: intent deletion commits before its in-memory reservation is released.
        await api.Server.Services.GetRequiredService<EngineManagementService>().ConvergeAsync(CancellationToken.None);
        await Assert.That(File.Exists(PathOf(deleted))).IsFalse();
        await using (var read = await system.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await read.GetAsync("pending_database_cleanup", database.Id, CancellationToken.None)).IsNull();
        }
        await Assert.That(await File.ReadAllBytesAsync(PathOf(retained))).IsEquivalentTo(corrupt);
        await Assert.That(await File.ReadAllBytesAsync(PathOf(unknown))).IsEquivalentTo(corrupt);
        // A completed cleanup also releases its engine reservation when no readable store could be opened.
        await Assert.That(await engine.CreateDatabaseAsync(deleted, CancellationToken.None)).IsTrue();

        string PathOf(TenantContext tenant)
        {
            return Path.Combine(api.Server.DataDirectory, tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId + ".db");
        }
    }

    [Test]
    [Arguments("deleted", HttpStatusCode.Created)]
    [Arguments("absent", HttpStatusCode.Conflict)]
    [Arguments("live", HttpStatusCode.Conflict)]
    public async Task A_late_database_name_claim_is_reusable_only_when_its_database_is_tombstoned(string state, HttpStatusCode expected)
    {
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(TimeProvider.System.GetUtcNow()));
        var database = await api.CreateManagedDatabaseAsync();
        var target = state == "absent" ? EntityIds.New().ToString() : database.Id;
        var collection = $"projects/{database.Project}/databases";
        var names = $"projects/{database.Project}/database_names";
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        if (state == "deleted")
        {
            await using var write = await system.BeginTransactionAsync(write: true, CancellationToken.None);
            await Assert.That(await write.DeleteAsync(collection, target, CancellationToken.None)).IsTrue();
            _ = await write.CommitAsync(CancellationToken.None);
        }

        // A name claim can arrive after the database's deletion through a different replication origin.
        await using (var write = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await write.PutAsync(names, database.Name, Json($"\"{target}\""), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }

        await using (var read = await system.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            var version = await read.GetVersionAsync(collection, target, CancellationToken.None);
            if (state == "absent")
            {
                await Assert.That(version).IsNull();
            }
            else
            {
                await Assert.That(version!.Value.Deleted).IsEqualTo(state == "deleted");
                await Assert.That((await read.GetVersionAsync(names, database.Name, CancellationToken.None))!.Value.Version).IsGreaterThan(version.Value.Version);
            }
        }

        using var created = await api.Client.PostAsJsonAsync(ManagementApi.Uri(
            $"/v1/management/organizations/{database.Organization}/projects/{database.Project}/databases"), new { name = database.Name });
        await Assert.That(created.StatusCode).IsEqualTo(expected).Because(await created.Content.ReadAsStringAsync());
        if (expected == HttpStatusCode.Created)
        {
            await Assert.That((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()).IsNotEqualTo(database.Id);
        }
        else
        {
            await using var read = await system.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That(Encoding.UTF8.GetString((await read.GetAsync(names, database.Name, CancellationToken.None))!.Json.Span)).IsEqualTo($"\"{target}\"");
        }
    }

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

        public FakeS3? Bucket { get; init; }

        public bool Seeds { get; init; }

        public IEnumerable<Region> Regions => _regions.Values;

        public Region this[string name] => _regions[name];

        public static Task<Cluster> StartAsync(params string[] regions) => StartAsync(new Cluster(), regions);

        public static Task<Cluster> StartSeededWithAnchorsAsync(params string[] regions) =>
            StartAsync(new Cluster { Bucket = new FakeS3(), Seeds = true }, regions);

        private static async Task<Cluster> StartAsync(Cluster cluster, string[] regions)
        {
            try
            {
                foreach (var name in regions)
                {
                    var region = new Region(cluster, name);
                    cluster._regions[name] = region;
                    if (!cluster.Seeds)
                    {
                        await region.StartAsync();
                    }
                }

                if (cluster.Seeds)
                {
                    await Task.WhenAll(cluster.Regions.Select(region => region.StartAsync()));
                }

                return cluster;
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }
        }

        // Unlists every machine and waits until its streams have stopped; Rejoin lists them again.
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
                cluster.Bucket?.Configure(settings);
                if (cluster.Seeds)
                {
                    settings[TerraBaseTestServer.PlatformSeedKey] = "true";
                }
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(cluster.Members);
                cluster.Bucket?.Route(services);
                if (cluster.Bucket is not null)
                {
                    ManualBackupHosting.Configure(services);
                }
            });

        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
        }

        public ITenantDirectory Tenants => Api.Server.Services.GetRequiredService<ITenantDirectory>();

        public string FileOf(TenantContext database) =>
            Path.Combine(_directory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");

        public async Task SeedDatabaseAsync(TenantContext tenant, string name)
        {
            var engine = Api.Server.Services.GetRequiredService<IStorageEngine>();
            await Assert.That(await engine.CreateDatabaseAsync(tenant, CancellationToken.None)).IsTrue();
            var record = new ManagedDatabase(tenant.DatabaseId!, tenant.ProjectId!, name, null, DateTimeOffset.UnixEpoch, Name, [], 1);
            await WriteAsync(async transaction =>
            {
                _ = await transaction.PutAsync($"projects/{tenant.ProjectId}/database_names", name, Json($"\"{tenant.DatabaseId}\""), CancellationToken.None);
                _ = await transaction.PutAsync($"projects/{tenant.ProjectId}/databases", tenant.DatabaseId!,
                    EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(record, TerraBaseJsonContext.Default.ManagedDatabase), record.Id, nested: true), CancellationToken.None);
            });
        }

        public async Task WriteAsync(TenantContext database, Func<IStorageTransaction, Task> work)
        {
            var store = await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException($"{Name} does not hold the database");
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            await work(transaction);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

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

        public async Task<string[]> IdsAsync(string collection)
        {
            var store = await SystemAsync();
            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            var page = await transaction.ListAsync(collection, null, 100, 1024 * 1024, CancellationToken.None);
            return [.. page.Documents.Select(document => document.Id).Order(StringComparer.Ordinal)];
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
