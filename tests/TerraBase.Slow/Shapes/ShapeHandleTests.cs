using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class ShapeHandleTests
{
    private const string Filter = """{"eq":["/n",1]}""";

    [Test]
    public async Task Repeated_calls_for_the_same_shape_return_the_same_handle()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);

        var collection = await CollectionAsync(store);

        var first = ShapeHandle.Compute(store, collection, Parse(Filter));
        _ = await api.PutAsync(DocumentApi.NewId(), new { n = 2 });
        var second = ShapeHandle.Compute(store, collection, Parse(Filter));

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(first.Handle).IsNotEmpty();
        await Assert.That(first.ShapeId).IsNotEmpty();
        await Assert.That(first.Handle).Matches("^h1-[0-9a-f]{16}$");
        await Assert.That(first.ShapeId).Matches("^[0-9a-f]{32}$");
        await Assert.That(first.IsCurrent(first.Handle)).IsTrue();
    }

    [Test]
    public async Task IsCurrent_compares_the_handle_exactly()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        var shape = ShapeHandle.Compute(store, await CollectionAsync(store), Parse(Filter));

        await Assert.That(shape.IsCurrent(null)).IsFalse();
        await Assert.That(shape.IsCurrent(string.Empty)).IsFalse();
        await Assert.That(shape.IsCurrent(shape.Handle + "x")).IsFalse();
        await Assert.That(shape.IsCurrent(shape.Handle[..^1])).IsFalse();
        await Assert.That(shape.IsCurrent(shape.Handle.ToUpperInvariant())).IsFalse()
            .Because("the comparison is ordinal, so a case-folded handle is another handle");
    }

    [Test]
    public async Task Different_filters_give_different_handles()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);

        var collection = await CollectionAsync(store);

        var unfiltered = ShapeHandle.Compute(store, collection, null);
        await Assert.That(ShapeHandle.Compute(store, collection, Parse("null"))).IsEqualTo(unfiltered);
        await Assert.That(ShapeHandle.Compute(store, collection, default(JsonElement))).IsEqualTo(unfiltered);
        var one = ShapeHandle.Compute(store, collection, Parse(Filter));
        var two = ShapeHandle.Compute(store, collection, Parse("""{"eq":["/n",2]}"""));

        await Assert.That(new[] { unfiltered.Handle, one.Handle, two.Handle }.Distinct().Count()).IsEqualTo(3);
        await Assert.That(new[] { unfiltered.ShapeId, one.ShapeId, two.ShapeId }.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task Whitespace_and_escape_forms_of_one_filter_share_a_handle()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);

        var collection = await CollectionAsync(store);

        var compact = ShapeHandle.Compute(store, collection, Parse("""{"eq":["/s","café"]}"""));
        var spaced = ShapeHandle.Compute(store, collection, Parse("""
            {
              "eq" : [ "/s" , "caf\u00e9" ]
            }
            """));

        await Assert.That(spaced.CanonicalFilter).IsEqualTo(compact.CanonicalFilter);
        await Assert.That(spaced.Handle).IsEqualTo(compact.Handle);
        await Assert.That(spaced.ShapeId).IsEqualTo(compact.ShapeId);
    }

    [Test]
    [Arguments("and")]
    [Arguments("or")]
    public async Task Commutative_children_have_order_independent_encoding(string op)
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);

        var collection = await CollectionAsync(store);

        var ordered = ShapeHandle.Compute(store, collection, Parse($$$"""{"{{{op}}}":[{"eq":["/a",1]},{"not":{"or":[{"eq":["/b",2]},{"exists":"/c"}]}}]}"""));
        var reversed = ShapeHandle.Compute(store, collection, Parse($$$"""{"{{{op}}}":[{"not":{"or":[{"exists":"/c"},{"eq":["/b",2]}]}},{"eq":["/a",1]}]}"""));

        await Assert.That(reversed.CanonicalFilter).IsEqualTo(ordered.CanonicalFilter);
        await Assert.That(reversed.Handle).IsEqualTo(ordered.Handle);
        await Assert.That(reversed.ShapeId).IsEqualTo(ordered.ShapeId);
    }

    [Test]
    [Arguments("in")]
    [Arguments("nin")]
    public async Task Membership_values_have_order_independent_encoding(string op)
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);

        var collection = await CollectionAsync(store);

        var forward = ShapeHandle.Compute(store, collection, Parse($$"""{"{{op}}":["/a",["<",1,false,null]]}"""));
        var backward = ShapeHandle.Compute(store, collection, Parse($$"""{"{{op}}":["/a",[null,false,1,"\u003c"]]}"""));

        await Assert.That(backward).IsEqualTo(forward);
        await Assert.That(forward.CanonicalFilter).IsEqualTo($$"""{"{{op}}":["/a",["<",1,false,null]]}""");
    }

    [Test]
    public async Task Positional_operands_are_preserved()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);

        var collection = await CollectionAsync(store);
        var forward = ShapeHandle.Compute(store, collection, Parse("""{"eq":["/a","/b"]}"""));
        var backward = ShapeHandle.Compute(store, collection, Parse("""{"eq":["/b","/a"]}"""));

        await Assert.That(forward.CanonicalFilter).IsEqualTo("""{"eq":["/a","/b"]}""");
        await Assert.That(backward.Handle).IsNotEqualTo(forward.Handle);
    }

    [Test]
    public async Task A_different_collection_gets_a_different_handle()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);

        var items = await CollectionAsync(store);
        await CreateCollectionAsync(store, "other");
        var other = await CollectionAsync(store, "other");

        var a = ShapeHandle.Compute(store, items, Parse(Filter));
        var b = ShapeHandle.Compute(store, other, Parse(Filter));

        await Assert.That(b.Handle).IsNotEqualTo(a.Handle);
    }

    [Test]
    public async Task A_different_database_gets_a_different_handle()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var tenant = await api.TenantOfAsync();
        var store = await StoreAsync(api);

        var collection = await CollectionAsync(store);

        var otherTenant = tenant with { DatabaseId = DocumentApi.NewId() };
        await Assert.That(await engine.CreateDatabaseAsync(otherTenant, CancellationToken.None)).IsTrue();
        var otherStore = (await engine.OpenDatabaseAsync(otherTenant, CancellationToken.None))!;
        await CreateCollectionAsync(otherStore, DocumentApi.Collection);
        var otherCollection = await CollectionAsync(otherStore);

        var mine = ShapeHandle.Compute(store, collection, Parse(Filter));
        var theirs = ShapeHandle.Compute(otherStore, otherCollection, Parse(Filter));

        await Assert.That(theirs.Handle).IsNotEqualTo(mine.Handle);
    }

    [Test]
    public async Task Dropping_and_recreating_a_collection_rotates_the_handle()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);

        var before = await CollectionAsync(store);
        var old = ShapeHandle.Compute(store, before, Parse(Filter));

        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.That(await transaction.DropCollectionAsync(DocumentApi.Collection, CancellationToken.None)).IsTrue();
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        await CreateCollectionAsync(store, DocumentApi.Collection);
        var after = await CollectionAsync(store);
        var recreated = ShapeHandle.Compute(store, after, Parse(Filter));

        await Assert.That(after.Id).IsNotEqualTo(before.Id);
        await Assert.That(recreated.Handle).IsNotEqualTo(old.Handle);
        await Assert.That(recreated.ShapeId).IsNotEqualTo(old.ShapeId);
        await Assert.That(recreated.IsCurrent(old.Handle)).IsFalse();
    }

    [Test]
    public async Task Recreating_a_database_under_the_same_tenant_rotates_the_handle()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var tenant = await api.TenantOfAsync();
        var store = await StoreAsync(api);

        var before = ShapeHandle.Compute(store, await CollectionAsync(store), Parse(Filter));
        var incarnation = store.Incarnation;

        await Assert.That(await engine.DeleteDatabaseAsync(tenant, CancellationToken.None)).IsTrue();
        await Assert.That(await engine.CreateDatabaseAsync(tenant, CancellationToken.None)).IsTrue();
        var recreated = (await engine.OpenDatabaseAsync(tenant, CancellationToken.None))!;
        await CreateCollectionAsync(recreated, DocumentApi.Collection);

        await Assert.That(recreated.Incarnation).IsNotEqualTo(incarnation);
        var after = ShapeHandle.Compute(recreated, await CollectionAsync(recreated), Parse(Filter));
        await Assert.That(after.Handle).IsNotEqualTo(before.Handle);
        await Assert.That(after.ShapeId).IsNotEqualTo(before.ShapeId);
    }

    [Test]
    public async Task Copying_a_database_renews_its_incarnation_and_handle()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var tenant = await api.TenantOfAsync();
        var store = await StoreAsync(api);

        var collection = await CollectionAsync(store);
        var original = ShapeHandle.Compute(store, collection, Parse(Filter));
        var target = tenant with { DatabaseId = DocumentApi.NewId() };

        await Assert.That(await engine.CopyDatabaseAsync(tenant, target, CancellationToken.None)).IsTrue();
        var copy = (await engine.OpenDatabaseAsync(target, CancellationToken.None))!;
        var copiedCollection = await CollectionAsync(copy);
        var copied = ShapeHandle.Compute(copy, copiedCollection, Parse(Filter));

        await Assert.That(copy.Incarnation).IsNotEqualTo(store.Incarnation);
        await Assert.That(copiedCollection.Id).IsEqualTo(collection.Id);
        await Assert.That(copied.Handle).IsNotEqualTo(original.Handle);
        await Assert.That(copied.ShapeId).IsNotEqualTo(original.ShapeId);
        await Assert.That(copied.IsCurrent(original.Handle)).IsFalse();
    }

    [Test]
    public async Task A_restored_snapshot_under_the_same_tenant_gets_a_new_incarnation_and_handle()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-shape-restore-").FullName;
        try
        {
            await using var api = await DocumentApi.StartIsolatedAsync();
            var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
            var backup = api.Server.Services.GetRequiredService<IWalBackup>();
            var tenant = await api.TenantOfAsync();
            var store = await StoreAsync(api);

            var collection = await CollectionAsync(store);
            var original = ShapeHandle.Compute(store, collection, Parse(Filter));
            var incarnation = store.Incarnation;

            var snapshot = Path.Combine(directory, "snapshot.db");
            await using (var tap = (await backup.OpenTapAsync(tenant, CancellationToken.None))!)
            {
                _ = await tap.SnapshotAsync(Stream.Null, snapshot, CancellationToken.None);
            }

            await Assert.That(await engine.DeleteDatabaseAsync(tenant, CancellationToken.None)).IsTrue();
            await backup.ImportDatabaseAsync(tenant, snapshot, CancellationToken.None);
            var restored = (await engine.OpenDatabaseAsync(tenant, CancellationToken.None))!;
            var restoredCollection = await CollectionAsync(restored);

            await Assert.That(restored.Incarnation).IsNotEqualTo(incarnation);
            await Assert.That(restoredCollection.Id).IsEqualTo(collection.Id).Because("the copy keeps the collection's identity");
            var copy = ShapeHandle.Compute(restored, restoredCollection, Parse(Filter));
            await Assert.That(copy.Handle).IsNotEqualTo(original.Handle);
            await Assert.That(copy.ShapeId).IsNotEqualTo(original.ShapeId);
            await Assert.That(copy.IsCurrent(original.Handle)).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_clean_restart_keeps_the_incarnation_and_the_handle()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-shape-clean-").FullName;
        try
        {
            ShapeHandle before;
            TenantContext tenant;
            CollectionInfo collection;
            IDatabaseStore original;
            await using (var api = await DocumentApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                original = await StoreAsync(api);
                tenant = await api.TenantOfAsync();
                collection = await CollectionAsync(original);
                before = ShapeHandle.Compute(original, collection, Parse(Filter));
            }

            await using var restarted = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
            var reopened = await StoreAsync(restarted, tenant);

            await Assert.That(reopened.Incarnation).IsEqualTo(original.Incarnation);
            var reopenedCollection = await CollectionAsync(reopened);
            await Assert.That(reopenedCollection.Id).IsEqualTo(collection.Id);
            await Assert.That(ShapeHandle.Compute(reopened, reopenedCollection, Parse(Filter))).IsEqualTo(before);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Importing_a_snapshot_into_another_store_rotates_its_incarnation_and_handle()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-shape-import-").FullName;
        try
        {
            await using var sourceApi = await DocumentApi.StartIsolatedAsync();
            var backup = sourceApi.Server.Services.GetRequiredService<IWalBackup>();
            var tenant = await sourceApi.TenantOfAsync();
            var source = await StoreAsync(sourceApi);
            var sourceCollection = await CollectionAsync(source);
            var originalHandle = ShapeHandle.Compute(source, sourceCollection, Parse(Filter));

            var snapshot = Path.Combine(directory, "snapshot.db");
            await using (var tap = (await backup.OpenTapAsync(tenant, CancellationToken.None))!)
            {
                _ = await tap.SnapshotAsync(Stream.Null, snapshot, CancellationToken.None);
            }

            await using var targetApi = await DocumentApi.StartIsolatedAsync();
            await targetApi.Server.Services.GetRequiredService<IWalBackup>().ImportDatabaseAsync(tenant, snapshot, CancellationToken.None);
            var copy = (await targetApi.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
            var copyCollection = await CollectionAsync(copy);

            await Assert.That(copyCollection.Id).IsEqualTo(sourceCollection.Id).Because("the copy keeps the collection's generation");
            await Assert.That(copyCollection.CreatedAt).IsEqualTo(sourceCollection.CreatedAt);
            await Assert.That(copy.Incarnation).IsNotEqualTo(source.Incarnation);

            var copiedHandle = ShapeHandle.Compute(copy, copyCollection, Parse(Filter));
            await Assert.That(copiedHandle.Handle).IsNotEqualTo(originalHandle.Handle);
            await Assert.That(copiedHandle.ShapeId).IsNotEqualTo(originalHandle.ShapeId);
            await Assert.That(copiedHandle.CanonicalFilter).IsEqualTo(originalHandle.CanonicalFilter);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<IDatabaseStore> StoreAsync(DocumentApi api)
    {
        _ = await api.PutAsync(DocumentApi.NewId(), new { n = 1 });
        return await StoreAsync(api.Server, await api.TenantOfAsync());
    }

    private static async Task<IDatabaseStore> StoreAsync(TerraBaseTestServer server, TenantContext tenant) =>
        (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;

    private static async Task<CollectionInfo> CollectionAsync(IDatabaseStore store, string name = DocumentApi.Collection)
    {
        await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        return await transaction.GetCollectionAsync(name, CancellationToken.None) ?? throw new InvalidOperationException($"collection {name} is missing");
    }

    private static async Task CreateCollectionAsync(IDatabaseStore store, string name)
    {
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        _ = await transaction.CreateCollectionAsync(new CollectionInfo(name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DocumentApi.NewId()), CancellationToken.None);
        _ = await transaction.CommitAsync(CancellationToken.None);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
