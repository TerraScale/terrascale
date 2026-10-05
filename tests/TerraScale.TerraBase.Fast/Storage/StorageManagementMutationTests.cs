using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Fast.Documents;

namespace TerraScale.TerraBase.Fast.Storage;

public sealed class StorageManagementMutationTests
{
    [Test]
    [Arguments("delete-prefix")]
    [Arguments("create-index")]
    [Arguments("drop-index")]
    public async Task Read_transactions_refuse_management_mutations(string mutation)
    {
        await using var api = await DocumentApi.StartAsync();
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

    [Test]
    public async Task A_failed_prefix_delete_cannot_commit_the_documents_deleted_before_its_collection_failure()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { n = 1 });
        await Assert.That(original.Status).IsEqualTo(HttpStatusCode.Created);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = await api.DatabaseFileAsync(),
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // RAISE(FAIL) leaves the preceding document delete in place until this transaction rolls back.
            command.CommandText = "CREATE TRIGGER reject_collection AFTER DELETE ON collections BEGIN SELECT RAISE(FAIL, 'injected collection failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.ThrowsAsync<SqliteException>(() => writer.DeleteCollectionsAsync(DocumentApi.Collection, CancellationToken.None).AsTask());
            await Assert.That(await writer.GetAsync(DocumentApi.Collection, id, CancellationToken.None)).IsNull();
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
        }

        var read = await api.GetAsync(id);
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.Version).IsEqualTo(original.Version);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
    }
}
