using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// A joined collection drop applies its clear lattice to the collection's documents and indexes: a write newer than the drop
/// survives it on every replica in any arrival order, and documents, fields and indexes older than the drop stay gone. Each
/// test starts its own servers in regions gru and sjc, and each node numbers the groups it receives per origin, as a stream does.
/// </summary>
public sealed class CollectionDropConvergenceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static readonly long Base = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static readonly ReplicaId Low = ReplicaId.Of(1, 7, 0);

    private static readonly ReplicaId High = ReplicaId.Of(2, 7, 0);

    // A newer write than the drop, and the drop, in one order on one node and the opposite order on the other.
    [Test]
    public async Task A_newer_put_and_field_set_survive_a_drop_in_either_arrival_order_and_older_documents_do_not()
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var created = Created(Low, At(1), "items", EntityIds.New().ToString());
        var old = new[] { Put(Low, At(2), "items", "o", """{"a":1,"b":2}"""), Put(Low, At(2), "items", "p", """{"a":1}""") };
        var drop = Drop(Low, At(3), "items");
        var newer = new[] { Put(High, At(4), "items", "n", """{"x":1}"""), Set(High, At(5), "items", "o", "c", "\"3\"") };

        await a.DeliverAsync(Low, [created], old, [drop]);
        await a.DeliverAsync(High, newer);
        await b.DeliverAsync(High, newer);
        await b.DeliverAsync(Low, [created], old, [drop]);

        foreach (var node in new[] { a, b })
        {
            var view = await ViewAsync(node, "items", "n", "o", "p");
            // The SET survives; the PUT's fields a and b are older than the drop and are gone with it.
            await Assert.That(view.Documents.GetValueOrDefault("n")).IsEqualTo("""{"x":1}""");
            await Assert.That(view.Documents.GetValueOrDefault("o")).IsEqualTo("""{"c":"3"}""");
            await Assert.That(view.Documents.ContainsKey("p")).IsFalse();
        }

        await AssertSameStateAsync(a, b, "items", "n", "o", "p");
    }

    // With no newer write, every document older than the drop is absent, including one that arrives after the drop.
    [Test]
    public async Task A_plain_drop_leaves_no_collection_and_no_older_document_even_when_the_document_arrives_after_it()
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var created = Created(Low, At(1), "items", EntityIds.New().ToString());
        var old = new[] { Put(Low, At(2), "items", "o", """{"a":1}"""), Put(Low, At(2), "items", "p", """{"b":2}""") };
        var drop = Drop(Low, At(3), "items");

        await a.DeliverAsync(Low, [created], old, [drop]);
        await b.DeliverAsync(Low, [created], [drop, .. old]);
        // A replayed older group, after the drop has landed on the node that already had the documents.
        await a.DeliverAsync(Low, old);

        foreach (var node in new[] { a, b })
        {
            var view = await ViewAsync(node, "items", "o", "p");
            await Assert.That(view.Entity is null).IsTrue();
            await Assert.That(view.Documents.Count).IsEqualTo(0);
        }

        await AssertSameStateAsync(a, b, "items", "o", "p");
    }

    // The same stamp on both sides: the greater node decides, whichever side the write and the drop come from.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_equal_stamp_drop_and_write_resolve_by_node_in_both_directions(bool writerWins)
    {
        var writer = writerWins ? High : Low;
        var dropper = writerWins ? Low : High;
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var created = Created(dropper, At(1), "items", EntityIds.New().ToString());
        var drop = Drop(dropper, At(2), "items");
        var survivor = new[] { Put(writer, At(2), "items", "n", """{"x":1}""") };

        await a.DeliverAsync(dropper, [created], [drop]);
        await a.DeliverAsync(writer, survivor);
        await b.DeliverAsync(writer, survivor);
        await b.DeliverAsync(dropper, [created], [drop]);

        foreach (var node in new[] { a, b })
        {
            var view = await ViewAsync(node, "items", "n");
            if (writerWins)
            {
                await Assert.That(view.Entity is null).IsFalse();
                await Assert.That(view.Documents.GetValueOrDefault("n")).IsEqualTo("""{"x":1}""");
            }
            else
            {
                await Assert.That(view.Entity is null).IsTrue();
                await Assert.That(view.Documents.ContainsKey("n")).IsFalse();
            }
        }

        await AssertSameStateAsync(a, b, "items", "n");
    }

    // A document replayed after the newer write that survived the drop must not come back.
    [Test]
    public async Task A_document_replayed_after_a_newer_survivor_stays_absent()
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var created = Created(Low, At(1), "items", EntityIds.New().ToString());
        var old = new[] { Put(Low, At(2), "items", "p", """{"a":1}""") };
        var drop = Drop(Low, At(3), "items");
        var survivor = new[] { Put(High, At(4), "items", "n", """{"x":1}""") };

        await a.DeliverAsync(Low, [created], old, [drop]);
        await a.DeliverAsync(High, survivor);
        await a.DeliverAsync(Low, old);
        await b.DeliverAsync(High, survivor);
        await b.DeliverAsync(Low, [created], old, [drop]);
        await b.DeliverAsync(Low, old);

        foreach (var node in new[] { a, b })
        {
            var view = await ViewAsync(node, "items", "n", "p");
            await Assert.That(view.Documents.GetValueOrDefault("n")).IsEqualTo("""{"x":1}""");
            await Assert.That(view.Documents.ContainsKey("p")).IsFalse();
        }

        await AssertSameStateAsync(a, b, "items", "n", "p");
    }

    // A collection dropped and re-created by a newer metadata document has that document's identity on every replica.
    [Test]
    public async Task A_collection_recreated_after_a_drop_has_one_identity_on_every_replica()
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var first = EntityIds.New().ToString();
        var second = EntityIds.New().ToString();
        var created = Created(Low, At(1), "items", first);
        var drop = Drop(Low, At(2), "items");
        var recreated = Created(High, At(3), "items", second);
        var survivor = new[] { Put(High, At(4), "items", "n", """{"x":1}""") };

        await a.DeliverAsync(Low, [created], [drop]);
        await a.DeliverAsync(High, [recreated], survivor);
        await b.DeliverAsync(High, [recreated], survivor);
        await b.DeliverAsync(Low, [created], [drop]);

        foreach (var node in new[] { a, b })
        {
            var view = await ViewAsync(node, "items", "n");
            await Assert.That(view.Entity).IsEqualTo(second);
            await Assert.That(view.Documents.GetValueOrDefault("n")).IsEqualTo("""{"x":1}""");
        }

        await AssertSameStateAsync(a, b, "items", "n");
    }

    // Recreation updates metadata fields without replacing the retained deleted clear.
    [Test]
    public async Task A_recreate_after_a_drop_keeps_the_drop_against_an_older_replayed_delta()
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var first = EntityIds.New().ToString();
        var second = EntityIds.New().ToString();
        var created = Created(Low, At(1), "items", first);
        var old = new[] { Put(Low, At(2), "items", "p", """{"a":1}""") };
        var drop = Drop(Low, At(3), "items");
        var recreated = Created(Low, At(4), "items", second);
        var survivor = new[] { Put(High, At(5), "items", "n", """{"x":1}""") };

        await a.DeliverAsync(Low, [created], old, [drop], [recreated]);
        await a.DeliverAsync(High, survivor);
        await a.DeliverAsync(Low, old);
        await b.DeliverAsync(High, survivor);
        await b.DeliverAsync(Low, [created], [drop], [recreated]);
        await b.DeliverAsync(Low, old);

        foreach (var node in new[] { a, b })
        {
            var view = await ViewAsync(node, "items", "n", "p");
            await Assert.That(view.Entity).IsEqualTo(second);
            await Assert.That(view.Documents.GetValueOrDefault("n")).IsEqualTo("""{"x":1}""");
            await Assert.That(view.Documents.ContainsKey("p")).IsFalse();
        }

        await AssertSameStateAsync(a, b, "items", "n", "p");
    }

    // A newer document tombstone survives the collection drop and rejects a PUT between those versions.
    [Test]
    public async Task A_newer_document_tombstone_survives_a_drop_and_rejects_an_older_put_in_either_order()
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var created = Created(Low, At(1), "items", EntityIds.New().ToString());
        var old = new[] { Put(Low, At(2), "items", "p", """{"a":1}""") };
        var drop = Drop(Low, At(3), "items");
        var deletedAt = new EntryVersion(new Hlc(At(5)), High);
        var tombstone = new ChangeDelta("items", ReplicationDocuments.Id("p"), deletedAt.Stamp, DocumentState.Bottom.Delete(deletedAt));
        var delayed = Put(High, At(4), "items", "p", """{"a":2}""");

        await a.DeliverAsync(Low, [created], old, [drop]);
        await a.DeliverAsync(High, [tombstone]);
        await b.DeliverAsync(High, [tombstone]);
        await b.DeliverAsync(Low, [created], old, [drop]);
        await a.DeliverAsync(High, [delayed]);
        await b.DeliverAsync(High, [delayed]);

        foreach (var node in new[] { a, b })
        {
            var view = await ViewAsync(node, "items", "p");
            await Assert.That(view.Entity).IsNotNull();
            await Assert.That(view.Documents.Count).IsEqualTo(0);
        }

        await AssertSameStateAsync(a, b, "items", "p");
    }

    // Several drops and recreations: the newest recreation's identity wins on every replica in both delivery orders.
    [Test]
    public async Task Several_drops_and_recreations_leave_the_latest_identity_on_every_replica()
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var firstEntity = EntityIds.New().ToString();
        var secondEntity = EntityIds.New().ToString();
        var latestEntity = EntityIds.New().ToString();
        var firstCreate = Created(Low, At(1), "items", firstEntity);
        var firstDrop = Drop(Low, At(2), "items");
        var secondCreate = Created(High, At(3), "items", secondEntity);
        var secondDrop = Drop(High, At(4), "items");
        var latestCreate = Created(Low, At(5), "items", latestEntity);

        await a.DeliverAsync(Low, [firstCreate], [firstDrop]);
        await a.DeliverAsync(High, [secondCreate], [secondDrop]);
        await a.DeliverAsync(Low, [latestCreate]);
        await b.DeliverAsync(Low, [latestCreate]);
        await b.DeliverAsync(High, [secondCreate], [secondDrop]);
        await b.DeliverAsync(Low, [firstCreate], [firstDrop]);

        foreach (var node in new[] { a, b })
        {
            var view = await ViewAsync(node, "items");
            await Assert.That(view.Entity).IsEqualTo(latestEntity);
        }

        await AssertSameStateAsync(a, b, "items");
    }

    // Index definitions older and newer than a drop, arriving on either side: the older one is suppressed, the newer one
    // comes back when the collection is recreated by a newer write.
    [Test]
    public async Task An_index_older_than_a_drop_is_suppressed_and_one_newer_than_it_is_restored_on_either_side()
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var second = EntityIds.New().ToString();
        var created = Created(Low, At(1), "items", EntityIds.New().ToString());
        var olderIndex = Index(Low, At(2), "by_old", "items", "a");
        var drop = Drop(Low, At(3), "items");
        var newerIndex = Index(High, At(4), "by_new", "items", "n");
        var recreated = Created(High, At(5), "items", second);
        var survivor = new[] { Put(High, At(6), "items", "n", """{"x":1}""") };

        await a.DeliverAsync(Low, [created], [olderIndex], [drop]);
        await a.DeliverAsync(High, [newerIndex], [recreated], survivor);
        await b.DeliverAsync(High, [newerIndex], [recreated], survivor);
        await b.DeliverAsync(Low, [created], [olderIndex], [drop]);

        foreach (var node in new[] { a, b })
        {
            await Assert.That(await IndexesAsync(node)).IsEqualTo("by_new@items(n)");
            var view = await ViewAsync(node, "items", "n");
            await Assert.That(view.Entity).IsEqualTo(second);
            await Assert.That(view.Documents.GetValueOrDefault("n")).IsEqualTo("""{"x":1}""");
        }

        await AssertSameStateAsync(a, b, "items", "n");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_index_reassigned_to_a_dropped_collection_removes_its_previous_physical_definition(bool newer)
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var created = Created(Low, At(1), "items", EntityIds.New().ToString());
        var other = Created(Low, At(1), "other", EntityIds.New().ToString());
        var first = Index(Low, At(2), "by_v", "other", "v");
        var moved = Index(High, At(newer ? 5 : 3), "by_v", "items", "v");
        var drop = Drop(Low, At(4), "items");
        await a.DeliverAsync(Low, [created, other], [first], [drop]);
        await a.DeliverAsync(High, [moved]);
        await b.DeliverAsync(Low, [created, other]);
        await b.DeliverAsync(High, [moved]);
        await b.DeliverAsync(Low, [first], [drop]);
        await Assert.That(await IndexesAsync(a)).IsEqualTo(string.Empty);
        await Assert.That(await IndexesAsync(b)).IsEqualTo(string.Empty);
        await AssertSameStateAsync(a, b, "items");
        await AssertSameStateAsync(a, b, "other");
    }

    // Local writes made through HTTP on two regions, with A's drop held back while B writes. Real change groups from each
    // side's CDC are delivered afterwards, in both directions.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_local_http_drop_and_a_write_made_before_its_delivery_converge_once_both_sides_deliver(bool writeBeforeDrop)
    {
        await using var apiA = await ManagementApi.StartAsync(configure: values => values["region"] = "gru");
        // B's clock runs ahead, so its write is newer than A's drop.
        await using var apiB = await ManagementApi.StartAsync(
            configure: values => values["region"] = "sjc", time: new AheadTime(TimeSpan.FromHours(1)));
        var tenant = await ReplicatedTenantAsync(apiA.Server, apiB.Server);
        var database = tenant.Database("data");
        var managed = new ManagedDatabaseRef(tenant.OrganizationId, tenant.ProjectId, database.DatabaseId!, "data");
        using var dataA = await apiA.CreateClientAsync(tenant.OrganizationId, "documents:write", "documents:read");
        using var dataB = await apiB.CreateClientAsync(tenant.OrganizationId, "documents:write", "documents:read");

        await ManagementApi.CreateCollectionAsync(dataA, managed, "items");
        var created = await GroupsAsync(apiA.Server, database, after: 0, ready: groups => groups.Count >= 1);
        await DeliverCapturedAsync(apiB.Server, database, created);
        await DeclareDefinitionsAsync(apiB, tenant, managed);
        await PutDocumentAsync(dataA, managed, "items", "old", """{"v":1}""");
        var written = await GroupsAsync(apiA.Server, database, after: created.Groups[^1].ChangeId, ready: groups => groups.Count >= 1);
        var originA = OriginOf(written.Groups[0]);
        await Assert.That(written.Groups.All(group => OriginOf(group) == originA)).IsTrue();
        await DeliverCapturedAsync(apiB.Server, database, written);

        SourceGroups wrote = new(Ulid.Empty, [], Retained: true);
        if (writeBeforeDrop)
        {
            await PutDocumentAsync(dataB, managed, "items", "new", """{"v":2}""");
            wrote = await GroupsAsync(apiB.Server, database, after: 0, ready: groups => groups.Count >= 3);
            await DeliverCapturedAsync(apiA.Server, database, wrote);
        }

        using (var drop = await dataA.DeleteAsync(ManagementApi.Uri($"{managed.Data}/collections/items")))
        {
            drop.EnsureSuccessStatusCode();
        }

        var dropped = await GroupsAsync(apiA.Server, database, after: written.Groups[^1].ChangeId, ready: groups => groups.Count >= 1);
        await Assert.That(dropped.Groups.All(group => OriginOf(group) == originA)).IsTrue();

        if (!writeBeforeDrop)
        {
            await PutDocumentAsync(dataB, managed, "items", "new", """{"v":2}""");
            wrote = await GroupsAsync(apiB.Server, database, after: 0, ready: groups => groups.Count >= 3);
            await DeliverCapturedAsync(apiA.Server, database, wrote);
        }

        var originB = OriginOf(wrote.Groups[0]);
        await Assert.That(originB == originA).IsFalse();
        await Assert.That(wrote.Groups.All(group => OriginOf(group) == originB)).IsTrue();
        await DeliverCapturedAsync(apiB.Server, database, dropped);

        var a = new Node(apiA.Server, database);
        var b = new Node(apiB.Server, database);
        foreach (var node in new[] { a, b })
        {
            var store = (await node.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
            await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That((await read.GetUniqueConstraintsAsync("items", CancellationToken.None)).Count).IsEqualTo(1);
            var view = await ViewAsync(node, "items", "old", "new");
            await Assert.That(view.Documents.ContainsKey("old")).IsFalse();
            await Assert.That(view.Documents.GetValueOrDefault("new")).IsEqualTo("""{"v":2}""");
        }

        await AssertSameStateAsync(a, b, "items", "old", "new");
    }

    [Test]
    public async Task Local_writes_in_a_collection_retained_by_a_future_drop_keep_increasing_stamps_and_converge()
    {
        await using var apiA = await ManagementApi.StartAsync(configure: values => values["region"] = "gru", time: new AheadTime(TimeSpan.FromMinutes(10)));
        await using var apiB = await ManagementApi.StartAsync(configure: values => values["region"] = "sjc");
        var tenant = await ReplicatedTenantAsync(apiA.Server, apiB.Server);
        var database = tenant.Database("data");
        var managed = new ManagedDatabaseRef(tenant.OrganizationId, tenant.ProjectId, database.DatabaseId!, "data");
        using var dataA = await apiA.CreateClientAsync(tenant.OrganizationId, "documents:write", "documents:read");
        using var dataB = await apiB.CreateClientAsync(tenant.OrganizationId, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(dataA, managed, "items");
        var created = await GroupsAsync(apiA.Server, database, 0, groups => groups.Count > 0);
        await DeliverCapturedAsync(apiB.Server, database, created);
        using (var response = await dataA.DeleteAsync(ManagementApi.Uri($"{managed.Data}/collections/items")))
        {
            response.EnsureSuccessStatusCode();
        }

        var dropped = await GroupsAsync(apiA.Server, database, created.Groups[^1].ChangeId, groups => groups.Count > 0);
        var drop = dropped.Groups.SelectMany(group => group.Deltas).Single(delta => delta.Collection == DocumentDelta.Collections);
        var at = new EntryVersion(new Hlc(drop.Stamp.Value + 1), High);
        var tombstone = new ChangeDelta("items", ReplicationDocuments.Id("tombstone"), at.Stamp, DocumentState.Bottom.Delete(at));
        var a = new Node(apiA.Server, database);
        var b = new Node(apiB.Server, database);
        await b.DeliverAsync(High, [tombstone]);
        await DeliverCapturedAsync(apiB.Server, database, dropped);
        await a.DeliverAsync(High, [tombstone]);

        await PutDocumentAsync(dataB, managed, "items", "first", """{"v":1}""");
        await PutDocumentAsync(dataB, managed, "items", "second", """{"v":2}""");
        var wrote = await GroupsAsync(apiB.Server, database, 0, groups => groups.Count >= 2);
        var stamps = wrote.Groups.SelectMany(group => group.Deltas).Select(delta => delta.Stamp.Value).ToArray();
        await Assert.That(stamps.Length).IsEqualTo(2);
        await Assert.That(stamps[0]).IsGreaterThan(drop.Stamp.Value);
        await Assert.That(stamps[1]).IsGreaterThan(stamps[0]);
        await DeliverCapturedAsync(apiA.Server, database, wrote);
        foreach (var node in new[] { a, b })
        {
            var view = await ViewAsync(node, "items", "first", "second", "tombstone");
            await Assert.That(view.Documents.Count).IsEqualTo(2);
            await Assert.That(view.Documents.GetValueOrDefault("first")).IsEqualTo("""{"v":1}""");
            await Assert.That(view.Documents.GetValueOrDefault("second")).IsEqualTo("""{"v":2}""");
        }

        await AssertSameStateAsync(a, b, "items", "first", "second", "tombstone");
    }

    [Test]
    public async Task A_local_http_recreation_retains_the_drop_barrier_on_both_replicas()
    {
        await using var apiA = await ManagementApi.StartAsync(configure: values => values["region"] = "gru", time: new AheadTime(TimeSpan.FromMinutes(10)));
        await using var apiB = await ManagementApi.StartAsync(configure: values => values["region"] = "sjc");
        var tenant = await ReplicatedTenantAsync(apiA.Server, apiB.Server);
        var database = tenant.Database("data");
        var managed = new ManagedDatabaseRef(tenant.OrganizationId, tenant.ProjectId, database.DatabaseId!, "data");
        using var data = await apiA.CreateClientAsync(tenant.OrganizationId, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var created = await GroupsAsync(apiA.Server, database, 0, groups => groups.Count > 0);
        await DeliverCapturedAsync(apiB.Server, database, created);
        using (var response = await data.DeleteAsync(ManagementApi.Uri($"{managed.Data}/collections/items")))
        {
            response.EnsureSuccessStatusCode();
        }

        var dropped = await GroupsAsync(apiA.Server, database, created.Groups[^1].ChangeId, groups => groups.Count > 0);
        var drop = dropped.Groups.SelectMany(group => group.Deltas).Single(delta => delta.Collection == DocumentDelta.Collections);
        await DeliverCapturedAsync(apiB.Server, database, dropped);
        using var dataB = await apiB.CreateClientAsync(tenant.OrganizationId, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(dataB, managed, "items");
        await DeclareDefinitionsAsync(apiB, tenant, managed);
        await PutDocumentAsync(dataB, managed, "items", "new", """{"v":1}""");
        using var patchBody = new StringContent("""{"ops":[{"op":"set","path":"/v","value":2}]}""", Encoding.UTF8, "application/vnd.terrabase.ops+json");
        using (var patch = await dataB.PatchAsync(
            ManagementApi.Uri(ManagementApi.DocumentPath(managed, "items", ReplicationDocuments.Id("new"))), patchBody))
        {
            patch.EnsureSuccessStatusCode();
        }

        var recreated = await GroupsAsync(apiB.Server, database, 0, groups => groups.Count >= 5);
        var metadata = recreated.Groups.SelectMany(group => group.Deltas).Single(delta => delta.Collection == DocumentDelta.Collections);
        await Assert.That(metadata.State.Clear).IsNull().Because("the retained drop is not overwritten by a fresh live clear");
        await Assert.That(recreated.Groups.SelectMany(group => group.Deltas).All(delta => delta.Stamp.Value > drop.Stamp.Value)).IsTrue();
        await DeliverCapturedAsync(apiA.Server, database, recreated);
        var a = new Node(apiA.Server, database);
        var b = new Node(apiB.Server, database);
        var stale = Put(Low, drop.Stamp.Value - 1, "items", "stale", """{"old":true}""");
        await a.DeliverAsync(Low, [stale]);
        await b.DeliverAsync(Low, [stale]);
        await Assert.That((await ViewAsync(a, "items", "stale")).Documents.Count).IsEqualTo(0);
        await Assert.That((await ViewAsync(b, "items", "stale")).Documents.Count).IsEqualTo(0);
        foreach (var node in new[] { a, b })
        {
            var store = (await node.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
            await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That((await read.GetUniqueConstraintsAsync("items", CancellationToken.None)).Count).IsEqualTo(1);
            var view = await ViewAsync(node, "items", "new");
            await Assert.That(view.Documents.GetValueOrDefault("new")).IsEqualTo("""{"v":2}""");
        }

        await AssertSameStateAsync(a, b, "items", "stale", "new");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_newer_hidden_unique_loser_survives_the_drop_of_its_older_winner(bool removeConstraint)
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        const string Winner = "01AAAAAAAAAAAAAAAAAAAAAAAA";
        const string Loser = "01BBBBBBBBBBBBBBBBBBBBBBBB";
        var created = Created(Low, At(1), "items", EntityIds.New().ToString());
        using var schema = JsonDocument.Parse("""{"constraints":[{"name":"email","fields":["email"],"normalization":"none"}]}""");
        var at = new EntryVersion(new Hlc(At(5)), High);
        var unique = new ChangeDelta(DocumentDelta.Unique, "items", at.Stamp, DocumentState.Bottom.Put(at, schema.RootElement));
        var winner = Put(Low, At(3), "items", Winner, """{"email":"same"}""");
        var loser = Put(High, At(6), "items", Loser, """{"email":"same"}""");
        var drop = Drop(Low, At(4), "items");
        var uniqueAt = new EntryVersion(new Hlc(At(7)), Low);
        var uniqueDrop = new ChangeDelta(DocumentDelta.Unique, "items", uniqueAt.Stamp, DocumentState.Bottom.Delete(uniqueAt));
        await a.DeliverAsync(Low, [created, winner]);
        await a.DeliverAsync(High, [unique, loser]);
        var original = (await serverA.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
        await using (var read = await original.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            var hidden = await read.GetAsync("items", Loser, CancellationToken.None);
            await Assert.That(hidden).IsNotNull();
            await Assert.That(hidden!.UniqueConflict).IsTrue();
        }

        await a.DeliverAsync(Low, removeConstraint ? [drop, uniqueDrop] : [drop]);
        await b.DeliverAsync(Low, [created, winner], removeConstraint ? [uniqueDrop, drop] : [drop]);
        await b.DeliverAsync(High, [unique, loser]);
        foreach (var node in new[] { a, b })
        {
            var store = (await node.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
            await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That(await read.GetAsync("items", Winner, CancellationToken.None)).IsNull();
            var survivor = await read.GetAsync("items", Loser, CancellationToken.None);
            await Assert.That(survivor).IsNotNull();
            await Assert.That(survivor!.UniqueConflict).IsFalse();
        }

        await AssertSameStateAsync(a, b, "items", Winner, Loser);
    }

    [Test]
    public async Task An_older_unique_definition_stays_inactive_when_it_arrives_before_or_after_the_drop()
    {
        await using var serverA = await StartQuietAsync("gru");
        await using var serverB = await StartQuietAsync("sjc");
        var database = (await ReplicatedTenantAsync(serverA, serverB)).Database("data");
        var a = new Node(serverA, database);
        var b = new Node(serverB, database);
        var created = Created(Low, At(1), "items", EntityIds.New().ToString());
        using var schema = JsonDocument.Parse("""{"constraints":[{"name":"email","fields":["email"],"normalization":"none"}]}""");
        var at = new EntryVersion(new Hlc(At(2)), High);
        var unique = new ChangeDelta(DocumentDelta.Unique, "items", at.Stamp, DocumentState.Bottom.Put(at, schema.RootElement));
        var drop = Drop(Low, At(3), "items");
        var recreated = Created(Low, At(4), "items", EntityIds.New().ToString());
        await a.DeliverAsync(Low, [created]);
        await a.DeliverAsync(High, [unique]);
        await a.DeliverAsync(Low, [drop], [recreated]);
        await b.DeliverAsync(Low, [created], [drop], [recreated]);
        await b.DeliverAsync(High, [unique]);
        foreach (var node in new[] { a, b })
        {
            var store = (await node.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
            await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That((await read.GetUniqueConstraintsAsync("items", CancellationToken.None)).Count).IsEqualTo(0);
        }

        await AssertSameStateAsync(a, b, "items");
    }

    [Test]
    public async Task A_schema_can_be_removed_after_local_drop_and_recreation_on_both_replicas()
    {
        await using var apiA = await ManagementApi.StartAsync(configure: values => values["region"] = "gru");
        await using var apiB = await ManagementApi.StartAsync(configure: values => values["region"] = "sjc");
        var tenant = await ReplicatedTenantAsync(apiA.Server, apiB.Server);
        var database = tenant.Database("data");
        var managed = new ManagedDatabaseRef(tenant.OrganizationId, tenant.ProjectId, database.DatabaseId!, "data");
        using var dataA = await apiA.CreateClientAsync(tenant.OrganizationId, "documents:write", "documents:read");
        using var dataB = await apiB.CreateClientAsync(tenant.OrganizationId, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(dataA, managed, "items");
        using (var drop = await dataA.DeleteAsync(ManagementApi.Uri($"{managed.Data}/collections/items")))
        {
            drop.EnsureSuccessStatusCode();
        }

        await ManagementApi.CreateCollectionAsync(dataA, managed, "items");
        var path = ManagementApi.Uri($"{managed.Data}/collections/items/schema");
        using var content = new StringContent("""{"type":"object","required":["name"]}""", Encoding.UTF8, "application/json");
        using (var set = await dataA.PutAsync(path, content))
        {
            set.EnsureSuccessStatusCode();
        }

        var attached = await GroupsAsync(apiA.Server, database, 0, groups =>
            groups.SelectMany(group => group.Deltas).Any(delta => delta.State.Fields.ContainsKey("schema")));
        await DeliverCapturedAsync(apiB.Server, database, attached);
        using (var removed = await dataA.DeleteAsync(path))
        {
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var detached = await GroupsAsync(apiA.Server, database, attached.Groups[^1].ChangeId, groups => groups.Count > 0);
        await DeliverCapturedAsync(apiB.Server, database, detached);
        foreach (var data in new[] { dataA, dataB })
        {
            using var response = await data.GetAsync(path);
            response.EnsureSuccessStatusCode();
            using var schema = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            await Assert.That(schema.RootElement.GetProperty("schema").ValueKind).IsEqualTo(JsonValueKind.Null);
            await PutDocumentAsync(data, managed, "items", "arbitrary", """{"arbitrary":true}""");
        }
    }

    // A server in the given region, whose change groups come from an empty fake source: the tests deliver every group themselves.
    private static async Task<TerraBaseTestServer> StartQuietAsync(string region) =>
        await TerraBaseTestServer.StartAsync(
            settings: values => values["region"] = region,
            services: services => services.AddSingleton<IChangeGroupSource>(provider =>
                new FakeChangeGroupSource(async (database, token) =>
                {
                    var engine = provider.GetRequiredService<IStorageEngine>();
                    return database == ChangeGroups.System
                        ? (await engine.OpenSystemDatabaseAsync(token)).Incarnation
                        : (await engine.OpenDatabaseAsync(database, token))?.Incarnation;
                })));

    private static async Task<ReplicationTenant> ReplicatedTenantAsync(TerraBaseTestServer seed, TerraBaseTestServer peer)
    {
        var tenant = await ReplicationTenant.CreateAsync(seed, ["data"], CancellationToken.None, replicaRegions: ["gru", "sjc"]);
        await tenant.BindAsync(peer);
        // BindAsync copies data-API catalog rows; management also needs the project-to-organization lookup.
        var source = await seed.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var read = await source.BeginTransactionAsync(write: false, CancellationToken.None);
        var mapping = await read.GetAsync("project_organizations", tenant.ProjectId, CancellationToken.None)
            ?? throw new InvalidOperationException("the project mapping is missing from the seed catalog");
        var target = await peer.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var write = await target.BeginTransactionAsync(write: true, CancellationToken.None);
        await write.CreateCollectionAsync(new CollectionInfo("project_organizations", 0), CancellationToken.None);
        await write.PutAsync("project_organizations", tenant.ProjectId, mapping.Json, CancellationToken.None);
        await write.CommitAsync(CancellationToken.None);
        return tenant;
    }

    private static ulong At(long offset) => (ulong)(Base + offset) << 16;

    private static ChangeDelta Drop(ReplicaId origin, ulong stamp, string collection)
    {
        var at = new EntryVersion(new Hlc(stamp), origin);
        return new ChangeDelta(DocumentDelta.Collections, collection, at.Stamp, DocumentState.Bottom.Delete(at).DeltaAt(at.Stamp));
    }

    private static ChangeDelta Created(ReplicaId origin, ulong stamp, string collection, string entity)
    {
        var at = new EntryVersion(new Hlc(stamp), origin);
        using var body = JsonDocument.Parse($$"""{"created_at":{{at.Stamp.Milliseconds}},"entity_id":"{{entity}}"}""");
        var state = body.RootElement.EnumerateObject().Aggregate(DocumentState.Bottom,
            (current, property) => current.Set(at, property.Name, property.Value));
        return new ChangeDelta(DocumentDelta.Collections, collection, at.Stamp, state);
    }

    private static ChangeDelta Index(ReplicaId origin, ulong stamp, string name, string collection, string field)
    {
        var at = new EntryVersion(new Hlc(stamp), origin);
        using var body = JsonDocument.Parse($$"""{"collection":"{{collection}}","fields":["{{field}}"]}""");
        return new ChangeDelta(DocumentDelta.Indexes, name, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }

    // Documents are keyed by their label; the replicated id is the label's ULID (see ReplicationDocuments).
    private static ChangeDelta Put(ReplicaId origin, ulong stamp, string collection, string label, string json)
    {
        var at = new EntryVersion(new Hlc(stamp), origin);
        using var body = JsonDocument.Parse(json);
        return new ChangeDelta(collection, ReplicationDocuments.Id(label), at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }

    private static ChangeDelta Set(ReplicaId origin, ulong stamp, string collection, string label, string field, string json)
    {
        var at = new EntryVersion(new Hlc(stamp), origin);
        using var value = JsonDocument.Parse(json);
        return new ChangeDelta(collection, ReplicationDocuments.Id(label), at.Stamp, DocumentState.Bottom.Set(at, field, value.RootElement).DeltaAt(at.Stamp));
    }

    private static OriginGroup Group(long changeId, ChangeDelta[] deltas) => new(changeId, deltas);

    private static ReplicaId OriginOf(OriginGroup group)
    {
        var origins = group.Deltas.SelectMany(delta => delta.State.Fields.Values.Select(field => field.Base?.Version.Node)
            .Append(delta.State.Clear?.Version.Node)).Where(origin => origin is not null).Distinct().ToArray();
        return origins is [{ } origin] ? origin : throw new InvalidOperationException("a change group's deltas must share one origin");
    }

    // Pushes one batch per origin, with the change ids the source numbered them with and the source's incarnation.
    private static async Task DeliverCapturedAsync(TerraBaseTestServer server, TenantContext database, SourceGroups source)
    {
        foreach (var origin in source.Groups.Select(OriginOf).Distinct())
        {
            await PushAsync(server, database, source.Incarnation, origin, [.. source.Groups.Where(group => OriginOf(group) == origin)]);
        }
    }

    // A push stream is one origin's groups; the receiver refuses only its own node or a zero origin, so synthetic origins are accepted.
    private static async Task PushAsync(TerraBaseTestServer server, TenantContext database, Ulid incarnation, ReplicaId origin, IReadOnlyList<OriginGroup> groups)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        using var channel = PeerChannel.Create(server.BaseAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        using var call = client.Push(cancellationToken: deadline.Token);
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "gru" } });
        _ = await call.ResponseStream.MoveNext(deadline.Token);
        await call.RequestStream.WriteAsync(new PushFrame
        {
            Batch = new Batch
            {
                Database = ChangeGroups.Wire(database, incarnation),
                LastChangeId = (ulong)groups[^1].ChangeId,
                Groups = ChangeGroups.Encode(groups),
            },
        });
        await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
        var ack = call.ResponseStream.Current.Ack;
        await Assert.That(ack.Refused).IsFalse();
        await Assert.That(ack.ChangeId).IsEqualTo((ulong)groups[^1].ChangeId);
        await call.RequestStream.CompleteAsync();
    }

    // Waits until the server's CDC source holds groups after <paramref name="after"/> that satisfy <paramref name="ready"/>.
    private static async Task<SourceGroups> GroupsAsync(
        TerraBaseTestServer server, TenantContext database, long after, Func<IReadOnlyList<OriginGroup>, bool> ready)
    {
        var source = server.Services.GetRequiredService<IChangeGroupSource>();
        SourceGroups groups = new(Ulid.Empty, [], Retained: true);
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            var incarnation = await IncarnationAsync(server, database, token);
            if (await source.ReadAsync(database, incarnation, after, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, token) is not { } read)
            {
                return false;
            }

            groups = read;
            return ready(read.Groups);
        });
        return groups;
    }

    // The incarnation of the database's copy on the server, which its change groups, digests and pushes name.
    private static async Task<Ulid> IncarnationAsync(TerraBaseTestServer server, TenantContext database, CancellationToken cancellationToken) =>
        (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, cancellationToken))?.Incarnation
        ?? throw new InvalidOperationException("the database is not open on this node");

    private static async Task DeclareDefinitionsAsync(ManagementApi api, ReplicationTenant tenant, ManagedDatabaseRef managed)
    {
        using var owner = api.As(tenant.UserId);
        var managementToken = api.Issuer.Mint(tenant.UserId, token =>
        {
            token.Claims["project_id"] = tenant.ProjectId;
            token.Claims["scope"] = "databases:read databases:write";
        });
        using var management = api.WithToken(managementToken);
        using var uniqueBody = new StringContent("""{"fields":[{"name":"v","unique":true}]}""", Encoding.UTF8, "application/json");
        using (var unique = await management.PutAsync(ManagementApi.Uri(managed.Data + "/schema/items"), uniqueBody))
        {
            unique.EnsureSuccessStatusCode();
        }

        using var indexBody = new StringContent("""{"collection":"items","fields":["v"],"name":"by_v"}""", Encoding.UTF8, "application/json");
        using var index = await management.PostAsync(ManagementApi.Uri(managed.Data + "/indexes"), indexBody);
        index.EnsureSuccessStatusCode();
    }

    private static async Task PutDocumentAsync(HttpClient data, ManagedDatabaseRef database, string collection, string label, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await data.PutAsync(
            ManagementApi.Uri(ManagementApi.DocumentPath(database, collection, ReplicationDocuments.Id(label))), content);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> DigestAsync(Node node)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        using var channel = PeerChannel.Create(node.Server.BaseAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var incarnation = await IncarnationAsync(node.Server, node.Database, deadline.Token);
        var digest = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(node.Database, incarnation) }, cancellationToken: deadline.Token);
        await Assert.That(digest.Held).IsTrue();
        await Assert.That(new Ulid(digest.Incarnation.Span)).IsEqualTo(incarnation);
        return Convert.ToHexString(digest.Buckets.ToByteArray());
    }

    // The collection's identity and the live documents among the labels, read from the node's storage.
    private static async Task<View> ViewAsync(Node node, string collection, params string[] labels)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        var store = await node.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(node.Database, deadline.Token)
            ?? throw new InvalidOperationException("the database is not open on this node");
        await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
        var info = await read.GetCollectionAsync(collection, deadline.Token);
        Dictionary<string, string> documents = [];
        foreach (var label in labels)
        {
            if (await read.GetAsync(collection, ReplicationDocuments.Id(label), deadline.Token) is { } document)
            {
                documents[label] = Encoding.UTF8.GetString(document.Json.Span);
            }
        }

        return new View(info?.Id, documents);
    }

    // The index definitions of the node, as name@collection(fields), sorted by name.
    private static async Task<string> IndexesAsync(Node node)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        var store = await node.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(node.Database, deadline.Token)
            ?? throw new InvalidOperationException("the database is not open on this node");
        await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
        var indexes = await read.ListIndexesAsync(deadline.Token);
        return string.Join(';', indexes.OrderBy(index => index.Name, StringComparer.Ordinal)
            .Select(index => $"{index.Name}@{index.Collection}({string.Join(',', index.Fields.Select(field => field.Path))})"));
    }

    // Both nodes hold the same digest, the same indexes and the same live documents among the labels.
    private static async Task AssertSameStateAsync(Node a, Node b, string collection, params string[] labels)
    {
        await Assert.That(await DigestAsync(a)).IsEqualTo(await DigestAsync(b));
        await Assert.That(await IndexesAsync(a)).IsEqualTo(await IndexesAsync(b));
        var left = await ViewAsync(a, collection, labels);
        var right = await ViewAsync(b, collection, labels);
        await Assert.That(left.Entity).IsEqualTo(right.Entity);
        await Assert.That(Flatten(left)).IsEqualTo(Flatten(right));
    }

    private static string Flatten(View view) =>
        string.Join(';', view.Documents.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

    private sealed record View(string? Entity, IReadOnlyDictionary<string, string> Documents);

    // One node and the database's tenant on it. Each origin's groups get the next change ids on this node, in delivery order.
    private sealed class Node(TerraBaseTestServer server, TenantContext database)
    {
        private readonly Dictionary<ReplicaId, long> _delivered = [];

        // The pushed origins have no copy here, so the node names its batches with one incarnation of its own.
        private readonly Ulid _incarnation = Ulid.NewUlid();

        public TerraBaseTestServer Server => server;

        public TenantContext Database => database;

        public async Task DeliverAsync(ReplicaId origin, params ChangeDelta[][] groups)
        {
            var first = _delivered.GetValueOrDefault(origin) + 1;
            _delivered[origin] = first + groups.Length - 1;
            var numbered = groups.Select((deltas, index) => Group(first + index, deltas)).ToList();
            await PushAsync(server, database, _incarnation, origin, numbered);
        }
    }

    private sealed class AheadTime(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + ahead;
    }
}
