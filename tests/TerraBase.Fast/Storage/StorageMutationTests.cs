using System.Net;
using System.Text;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Storage;

public sealed class StorageMutationTests
{

    [Test]
    public async Task Dropping_a_collection_rolls_back_atomically_and_recreation_starts_empty()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        await api.PutAsync(id, new { n = 1 });
        await api.PutAsync(id, new { n = 2 });
        var store = await StoreAsync(api);

        await using (var rolledBack = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.That(await rolledBack.DropCollectionAsync(DocumentApi.Collection, CancellationToken.None)).IsTrue();
        }

        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(2);

        var collections = api.Collections;
        await Assert.That((await api.SendAsync(HttpMethod.Delete, $"{collections}/{DocumentApi.Collection}")).Status)
            .IsEqualTo(HttpStatusCode.OK);
        await api.SendAsync(HttpMethod.Post, collections, new { name = DocumentApi.Collection });
        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);
        await api.PutAsync(id, new { n = 3 });
        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task Stored_documents_and_tombstones_keep_the_transaction_incarnation()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        await api.PutAsync(id, new { n = 1 });
        var store = await StoreAsync(api);
        await using (var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            var document = (await reader.GetAsync(DocumentApi.Collection, id, CancellationToken.None))!;
            await Assert.That(document.Scope).IsEqualTo(reader.Scope);
            var query = new DocumentQuery(DocumentApi.Collection, null, [], null, 10, 1 << 20);
            var page = await reader.QueryAsync(query, CancellationToken.None);
            await Assert.That(page.Rows[0].Document.Scope).IsEqualTo(reader.Scope);
        }

        await api.SendAsync(HttpMethod.Delete, api.Document(id));
        await using var deleted = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        // GetAsync reads live documents only; the tombstone's version comes from its metadata.
        var tombstone = (await deleted.GetVersionAsync(DocumentApi.Collection, id, CancellationToken.None))!.Value;
        await Assert.That(tombstone.Deleted).IsTrue();
    }

    private static async Task<IDatabaseStore> StoreAsync(DocumentApi api)
    {
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        return (await engine.OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
    }
}
