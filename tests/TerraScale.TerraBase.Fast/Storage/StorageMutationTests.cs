using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Engine.Sqlite;
using TerraScale.TerraBase.Fast.Documents;

namespace TerraScale.TerraBase.Fast.Storage;

public sealed class StorageMutationTests
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
        var native = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        IStorageEngine engine = stamped ? api.Server.Services.GetRequiredService<IStorageEngine>() : native;
        var database = await api.TenantOfAsync(cancellationToken: deadline.Token);
        var store = (await engine.OpenDatabaseAsync(database, deadline.Token))!;
        var sequence = store.CommitSequence;
        var history = await HistoryRowsAsync(api);
        var other = database with { DatabaseId = Guid.NewGuid().ToString() };
        await native.CreateDatabaseAsync(other, deadline.Token);
        var blockedStore = (await native.OpenDatabaseAsync(other, deadline.Token))!;
        var firstId = DocumentApi.NewId();
        var secondId = DocumentApi.NewId();
        Task? blocked = null;
        Task? earlier = null;
        Task? cancelled = null;
        Task<long>? commit = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using (var writer = await store.BeginTransactionAsync(write: true, deadline.Token))
        {
            await writer.PutAsync(DocumentApi.Collection, firstId, Encoding.UTF8.GetBytes("{\"n\":2}"), deadline.Token);
            try
            {
                await using (await StorageFixture.LockAsync(Path.Combine(api.Server.DataDirectory,
                    other.OrganizationId, other.ProjectId!, other.DatabaseId + ".db")))
                {
                    blocked = BlockAsync();
                    await UntilAsync(() => native.Io.Running == 1);
                    if (phase == "gate")
                    {
                        earlier = writer.GetAsync(DocumentApi.Collection, id, deadline.Token).AsTask();
                        await UntilAsync(() => native.Io.Queued == 1);
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
                        await UntilAsync(() => native.Io.Queued == 1);
                    }
                    cancellation.Cancel();
                    if (phase != "gate")
                    {
                        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.WaitAsync(deadline.Token));
                    }
                    // Accepted before the worker resumes: failure marking must precede this commit.
                    commit = writer.CommitAsync(deadline.Token).AsTask();
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
        await Assert.That(await HistoryRowsAsync(api)).IsEqualTo(history + (mutation == "read" ? 1 : 0));

        async Task BlockAsync()
        {
            await using var transaction = await blockedStore.BeginTransactionAsync(write: true, deadline.Token);
        }

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
    [Arguments("delete")]
    [Arguments("create")]
    [Arguments("drop")]
    public async Task Read_transactions_refuse_every_mutation(string mutation)
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { n = 1 });
        var store = await StoreAsync(api);
        var sequence = store.CommitSequence;
        var rows = await HistoryRowsAsync(api);
        await using (var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => MutateAsync(reader, mutation, id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.CommitAsync(CancellationToken.None).AsTask());
            var document = await reader.GetAsync(DocumentApi.Collection, id, CancellationToken.None);
            await Assert.That(document!.Incarnation).IsEqualTo(reader.Incarnation);
        }

        var read = await api.GetAsync(id);
        await Assert.That(read.Version).IsEqualTo(original.Version);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await Assert.That(store.CommitSequence).IsEqualTo(sequence);
        await Assert.That(await HistoryRowsAsync(api)).IsEqualTo(rows);
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/extra")).Status)
            .IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("put", "offset")]
    [Arguments("create", "offset")]
    [Arguments("drop", "offset")]
    [Arguments("create", "collection")]
    [Arguments("drop", "collection")]
    [Arguments("delete", "document")]
    public async Task A_failed_collection_or_offset_statement_prevents_committing_partial_changes(string mutation, string failure)
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { n = 1 });
        var store = await StoreAsync(api);
        var sequence = store.CommitSequence;
        var rows = await HistoryRowsAsync(api);
        var (_, snapshotUrl, snapshotText) = await SnapshotAsync(api, shapes);
        var trigger = failure == "offset"
            ? "CREATE TRIGGER reject_offset BEFORE UPDATE ON commit_offset BEGIN SELECT RAISE(FAIL, 'injected offset failure'); END;"
            // The row is already deleted when an AFTER DELETE trigger fails: the transaction must still refuse to commit.
            : failure == "document"
                ? "CREATE TRIGGER reject_document AFTER DELETE ON documents BEGIN SELECT RAISE(FAIL, 'injected document failure'); END;"
            : mutation == "create"
                ? "CREATE TRIGGER reject_collection AFTER INSERT ON collections BEGIN SELECT RAISE(FAIL, 'injected collection failure'); END;"
                : "CREATE TRIGGER reject_collection BEFORE DELETE ON collections BEGIN SELECT RAISE(FAIL, 'injected collection failure'); END;";
        await SqlAsync(api, trigger);

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
        await Assert.That(await HistoryRowsAsync(api)).IsEqualTo(rows);
        using var replay = await shapes.GetAsync(snapshotUrl);
        replay.EnsureSuccessStatusCode();
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo(snapshotText);
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/extra")).Status)
            .IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Dropping_a_collection_removes_its_history_atomically_and_recreation_starts_a_fresh_shape()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var id = DocumentApi.NewId();
        await api.PutAsync(id, new { n = 1 });
        await api.PutAsync(id, new { n = 2 });
        var store = await StoreAsync(api);
        var rows = await HistoryRowsAsync(api);
        await Assert.That(rows).IsEqualTo(2);
        var (oldHandle, oldSnapshotUrl, oldSnapshotText) = await SnapshotAsync(api, shapes);

        await using (var rolledBack = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.That(await rolledBack.DropCollectionAsync(DocumentApi.Collection, CancellationToken.None)).IsTrue();
        }

        await Assert.That(await HistoryRowsAsync(api)).IsEqualTo(rows);
        using var replay = await shapes.GetAsync(oldSnapshotUrl);
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo(oldSnapshotText);
        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(2);

        var collections = api.Collections;
        await Assert.That((await api.SendAsync(HttpMethod.Delete, $"{collections}/{DocumentApi.Collection}")).Status)
            .IsEqualTo(HttpStatusCode.OK);
        // The drop keeps the dropped generation's history as tombstones only: no body outlives it.
        await Assert.That(await HistoryRowsAsync(api)).IsEqualTo(rows);
        await Assert.That((long)(await SqlAsync(api, "SELECT count(*) FROM change_log WHERE collection = $collection AND body IS NOT NULL;"))!)
            .IsEqualTo(0);
        await api.SendAsync(HttpMethod.Post, collections, new { name = DocumentApi.Collection });
        await api.PutAsync(id, new { n = 3 });
        using var stale = await shapes.GetAsync(oldSnapshotUrl);
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var (newHandle, _, newSnapshotText) = await SnapshotAsync(api, shapes);
        await Assert.That(newHandle).IsNotEqualTo(oldHandle);
        using var fresh = JsonDocument.Parse(newSnapshotText);
        var operations = fresh.RootElement.GetProperty("changes")[0].GetProperty("ops");
        await Assert.That(operations.GetArrayLength()).IsEqualTo(1);
        await Assert.That(operations[0].GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(3);
        await Assert.That(await HistoryRowsAsync(api)).IsEqualTo(rows + 1);
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
            await Assert.That(document.Incarnation).IsEqualTo(reader.Incarnation);
            var query = new DocumentQuery(DocumentApi.Collection, null, [], null, 10, 1 << 20);
            var page = await reader.QueryAsync(query, CancellationToken.None);
            await Assert.That(page.Rows[0].Document.Incarnation).IsEqualTo(reader.Incarnation);
            var snapshot = await reader.SnapshotAsync(query, document.Version, CancellationToken.None);
            await Assert.That(snapshot.Rows[0].Document.Incarnation).IsEqualTo(reader.Incarnation);
            await foreach (var change in reader.ChangesAsync(DocumentApi.Collection, null, 0, document.Version, CancellationToken.None))
            {
                await Assert.That(change.Incarnation).IsEqualTo(reader.Incarnation);
            }
        }

        await api.SendAsync(HttpMethod.Delete, api.Document(id));
        await using var deleted = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        // GetAsync reads live documents only; the tombstone's version comes from its metadata.
        var tombstone = (await deleted.GetVersionAsync(DocumentApi.Collection, id, CancellationToken.None))!.Value;
        await Assert.That(tombstone.Deleted).IsTrue();
        await foreach (var change in deleted.ChangesAsync(DocumentApi.Collection, null, tombstone.Version - 1, tombstone.Version, CancellationToken.None))
        {
            await Assert.That(change.Deleted).IsTrue();
            await Assert.That(change.Incarnation).IsEqualTo(deleted.Incarnation);
        }
    }

    private static async Task MutateAsync(IStorageTransaction transaction, string mutation, string id)
    {
        switch (mutation)
        {
            case "put":
                await transaction.PutAsync(DocumentApi.Collection, id, Encoding.UTF8.GetBytes("{\"n\":2}"), CancellationToken.None);
                break;
            case "delete":
                await transaction.DeleteAsync(DocumentApi.Collection, id, CancellationToken.None);
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

    private static async Task<long> HistoryRowsAsync(DocumentApi api) =>
        (long)(await SqlAsync(api, "SELECT count(*) FROM change_log WHERE collection = $collection;"))!;

    private static async Task<object?> SqlAsync(DocumentApi api, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = await api.DatabaseFileAsync(),
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$collection", DocumentApi.Collection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<(string Handle, Uri SnapshotUrl, string Text)> SnapshotAsync(DocumentApi api, HttpClient shapes)
    {
        using var start = await shapes.GetAsync(new Uri(api.Shape + "?offset=-1", UriKind.Relative));
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        using var snapshot = await shapes.GetAsync(start.Headers.Location!);
        snapshot.EnsureSuccessStatusCode();
        return (start.Headers.GetValues("TB-Shape-Handle").Single(), start.Headers.Location!, await snapshot.Content.ReadAsStringAsync());
    }
}
