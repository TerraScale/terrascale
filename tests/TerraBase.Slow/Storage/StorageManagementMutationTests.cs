using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Storage;

public sealed class StorageManagementMutationTests
{
    [Test]
    [Arguments("delete-prefix")]
    [Arguments("create-index")]
    [Arguments("drop-index")]
    public async Task Read_transactions_refuse_management_mutations(string mutation)
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var id = DocumentApi.NewId();
        await api.PutAsync(id, new { n = 1 });
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var index = new IndexDefinition("items_id", DocumentApi.Collection, [DocumentField.Id]);
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await writer.CreateIndexAsync(index, CancellationToken.None);
            await writer.CommitAsync(CancellationToken.None);
        }

        await using (var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                switch (mutation)
                {
                    case "delete-prefix":
                        await reader.DeleteCollectionsAsync(DocumentApi.Collection, CancellationToken.None);
                        break;
                    case "create-index":
                        await reader.CreateIndexAsync(index with { Name = "another" }, CancellationToken.None);
                        break;
                    case "drop-index":
                        await reader.DropIndexAsync(index.Name, CancellationToken.None);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mutation));
                }
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.CommitAsync(CancellationToken.None).AsTask());
        }

        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await using var after = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That((await after.ListIndexesAsync(CancellationToken.None)).Select(item => item.Name)).IsEquivalentTo([index.Name]);
    }

    // Prefix deletes remove system database collections without a trace (a client database refuses them: its deletions
    // are tombstones).
    [Test]
    public async Task A_failed_prefix_delete_cannot_commit_the_documents_deleted_before_its_collection_failure()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        var collection = $"prefix_{Guid.NewGuid():N}";
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await writer.CreateCollectionAsync(new CollectionInfo(collection, 0), CancellationToken.None);
            await writer.PutAsync(collection, "a", "{\"n\":1}"u8.ToArray(), CancellationToken.None);
            await writer.CommitAsync(CancellationToken.None);
        }

        // RAISE(FAIL) leaves the preceding document delete in place until this transaction rolls back.
        await StorageFixture.ExecuteWhileWriterHeldAsync(api.Server, null,
            "CREATE TRIGGER reject_collection AFTER DELETE ON collections BEGIN SELECT RAISE(FAIL, 'injected collection failure'); END;");

        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.ThrowsAsync<SqliteException>(() => writer.DeleteCollectionsAsync(collection, CancellationToken.None).AsTask());
            await Assert.That(await writer.GetAsync(collection, "a", CancellationToken.None)).IsNull();
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
        }

        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var document = await read.GetAsync(collection, "a", CancellationToken.None);
        await Assert.That(document is not null && document.Json.Span.SequenceEqual("{\"n\":1}"u8)).IsTrue();
    }
}
