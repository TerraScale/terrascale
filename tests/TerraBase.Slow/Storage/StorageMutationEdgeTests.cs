using System.Net;
using System.Text;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Storage;

public sealed class StorageMutationEdgeTests
{
    [Test]
    [Arguments(false, "queued", "put")]
    [Arguments(true, "queued", "put")]
    [Arguments(true, "queued", "delete")]
    [Arguments(true, "queued", "tombstone")]
    [Arguments(true, "queued", "create")]
    [Arguments(true, "queued", "drop")]
    [Arguments(false, "gate", "put")]
    [Arguments(true, "gate", "put")]
    [Arguments(false, "cancelled", "put")]
    [Arguments(true, "cancelled", "put")]
    [Arguments(false, "queued", "read")]
    [Arguments(true, "queued", "read")]
    public async Task Cancelled_mutations_prevent_commit_but_cancelled_reads_do_not(bool stamped, string phase, string mutation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-threads"] = "1", cancellationToken: deadline.Token);
        var id = DocumentApi.NewId();
        await api.PutAsync(id, new { n = 1 }, cancellationToken: deadline.Token);
        var native = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        IStorageEngine engine = stamped ? api.Server.Services.GetRequiredService<IStorageEngine>() : native;
        var database = await api.TenantOfAsync(cancellationToken: deadline.Token);
        var store = (await engine.OpenDatabaseAsync(database, deadline.Token))!;
        var sequence = store.CommitSequence;
        // Write transactions run their calls on the database's write thread: blocking it queues the calls below.
        var lane = ((TursoDatabaseStore)(await native.OpenDatabaseAsync(database, deadline.Token))!).WriteLane;
        using var release = new ManualResetEventSlim();
        var firstId = DocumentApi.NewId();
        var secondId = DocumentApi.NewId();
        Task? blocked = null;
        Task? earlier = null;
        Task? cancelled = null;
        Task? commit = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using (var writer = await store.BeginTransactionAsync(write: true, deadline.Token))
        {
            await writer.PutAsync(DocumentApi.Collection, firstId, Encoding.UTF8.GetBytes("{\"n\":2}"), deadline.Token);
            try
            {
                try
                {
                    blocked = lane.RunAsync(release.Wait, CancellationToken.None);
                    await UntilAsync(() => lane.Running);
                    if (phase == "gate")
                    {
                        earlier = writer.GetAsync(DocumentApi.Collection, id, deadline.Token).AsTask();
                        await UntilAsync(() => lane.Queued == 1);
                    }
                    if (phase == "cancelled")
                    {
                        cancellation.Cancel();
                    }

                    cancelled = mutation switch
                    {
                        "put" => writer.PutAsync(DocumentApi.Collection, secondId, Encoding.UTF8.GetBytes("{}"), cancellation.Token).AsTask(),
                        "delete" => writer.DeleteAsync(DocumentApi.Collection, id, cancellation.Token).AsTask(),
                        "tombstone" => writer.TombstoneAsync(DocumentApi.Collection, id, cancellation.Token).AsTask(),
                        "create" => writer.CreateCollectionAsync(new CollectionInfo("extra", 0), cancellation.Token).AsTask(),
                        "drop" => writer.DropCollectionAsync(DocumentApi.Collection, cancellation.Token).AsTask(),
                        _ => writer.GetAsync(DocumentApi.Collection, id, cancellation.Token).AsTask(),
                    };
                    if (phase == "queued")
                    {
                        await UntilAsync(() => lane.Queued == 1);
                    }
                    cancellation.Cancel();
                    if (phase != "gate")
                    {
                        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.WaitAsync(deadline.Token));
                    }
                    // Accepted before the worker resumes: failure marking must precede this commit.
                    commit = writer.CommitAsync(deadline.Token).AsTask();
                }
                finally
                {
                    release.Set();
                }

                await blocked.WaitAsync(deadline.Token);
                if (earlier is not null)
                {
                    await earlier.WaitAsync(deadline.Token);
                }
                await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.WaitAsync(deadline.Token));
                if (mutation == "read")
                {
                    await commit.WaitAsync(deadline.Token);
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => commit.WaitAsync(deadline.Token));
                }
            }
            finally
            {
                cancellation.Cancel();
            }
        }

        await Assert.That((await api.GetAsync(firstId, cancellationToken: deadline.Token)).Status)
            .IsEqualTo(mutation == "read" ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        await Assert.That((await api.GetAsync(secondId, cancellationToken: deadline.Token)).Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(store.CommitSequence).IsEqualTo(sequence + (mutation == "read" ? 1 : 0));

        async Task UntilAsync(Func<bool> condition)
        {
            while (!condition())
            {
                await Task.Delay(10, deadline.Token);
            }
        }
    }

    [Test]
    [Arguments("put")]
    [Arguments("tombstone")]
    [Arguments("create")]
    [Arguments("drop")]
    public async Task Read_transactions_refuse_every_mutation(string mutation)
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { n = 1 });
        var store = await StoreAsync(api);
        var sequence = store.CommitSequence;
        await using (var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => MutateAsync(reader, mutation, id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.CommitAsync(CancellationToken.None).AsTask());
            var document = await reader.GetAsync(DocumentApi.Collection, id, CancellationToken.None);
            await Assert.That(document!.Scope).IsEqualTo(reader.Scope);
        }

        var read = await api.GetAsync(id);
        await Assert.That(read.Version).IsEqualTo(original.Version);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await Assert.That(store.CommitSequence).IsEqualTo(sequence);
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/extra")).Status)
            .IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("put", "offset")]
    [Arguments("create", "offset")]
    [Arguments("drop", "offset")]
    [Arguments("create", "collection")]
    [Arguments("drop", "collection")]
    [Arguments("tombstone", "document")]
    public async Task A_failed_collection_or_offset_statement_prevents_committing_partial_changes(string mutation, string failure)
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { n = 1 });
        var store = await StoreAsync(api);
        var sequence = store.CommitSequence;
        var trigger = failure == "offset"
            ? "CREATE TRIGGER reject_offset BEFORE UPDATE ON commit_offset BEGIN SELECT RAISE(FAIL, 'injected offset failure'); END;"
            // The row is already gone when an AFTER DELETE trigger fails: the transaction must still refuse to commit.
            : failure == "document"
                ? "CREATE TRIGGER reject_document AFTER DELETE ON \"items\" BEGIN SELECT RAISE(FAIL, 'injected document failure'); END;"
            : mutation == "create"
                ? "CREATE TRIGGER reject_collection AFTER INSERT ON collections BEGIN SELECT RAISE(FAIL, 'injected collection failure'); END;"
                : "CREATE TRIGGER reject_collection BEFORE DELETE ON collections BEGIN SELECT RAISE(FAIL, 'injected collection failure'); END;";
        await StorageFixture.ExecuteWhileWriterHeldAsync(api.Server, await api.TenantOfAsync(), trigger);

        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            // RAISE(FAIL) preserves earlier statements (and an AFTER INSERT's new row) in this transaction.
            await Assert.ThrowsAsync<SqliteException>(() => MutateAsync(writer, mutation, id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
        }

        var read = await api.GetAsync(id);
        await Assert.That(read.Version).IsEqualTo(original.Version);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await Assert.That(store.CommitSequence).IsEqualTo(sequence);
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/extra")).Status)
            .IsEqualTo(HttpStatusCode.NotFound);
    }

    private static async Task MutateAsync(IStorageTransaction transaction, string mutation, string id)
    {
        switch (mutation)
        {
            case "put":
                await transaction.PutAsync(DocumentApi.Collection, id, Encoding.UTF8.GetBytes("{\"n\":2}"), CancellationToken.None);
                break;
            case "tombstone":
                await transaction.TombstoneAsync(DocumentApi.Collection, id, CancellationToken.None);
                break;
            case "create":
                await transaction.CreateCollectionAsync(new CollectionInfo("extra", 0), CancellationToken.None);
                break;
            case "drop":
                await transaction.DropCollectionAsync(DocumentApi.Collection, CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static async Task<IDatabaseStore> StoreAsync(DocumentApi api)
    {
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        return (await engine.OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
    }
}
