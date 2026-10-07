using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

// TS-647 step 2: client writes of one database share write transactions (group commit), each in its own savepoint, on
// the database's own write thread.
public sealed class GroupCommitTests
{
    [Test]
    public async Task Writes_queued_together_commit_in_one_transaction_and_failures_roll_back_alone()
    {
        await using var api = await DocumentApi.StartAsync();
        var existing = DocumentApi.NewId();
        var original = await api.PutAsync(existing, new { n = 0 });
        var store = await StoreAsync(api);

        // Hold the database's writer so the writes below queue up, then release them into as few batches as possible.
        var held = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        var ids = Enumerable.Range(0, 8).Select(_ => DocumentApi.NewId()).ToArray();
        var rolledBack = DocumentApi.NewId();
        Task<ApiResponse>[] sending;
        try
        {
            sending =
            [
                .. ids.Select(id => api.PutAsync(id, new { n = 1 })),
                api.PutAsync(existing, new { n = 2 }, DocumentApi.IfMatch("stale")),
                api.TransactAsync(
                    new { op = "put", collection = DocumentApi.Collection, id = rolledBack, document = new { n = 3 } },
                    new { op = "assert", collection = DocumentApi.Collection, id = existing, filter = new { eq = new object[] { "/n", 99 } } }),
            ];

            // The first write to arrive may already wait for the writer outside the queue; every other one queues.
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites == sending.Length - 1));
        }
        finally
        {
            await held.DisposeAsync();
        }

        var answers = await Task.WhenAll(sending);
        var created = answers[..ids.Length];
        await Assert.That(created.All(answer => answer.Status == HttpStatusCode.Created)).IsTrue();
        var offsets = created.GroupBy(answer => answer.Json.GetProperty("offset").GetString()).Select(group => group.Count()).ToArray();
        await Assert.That(offsets.Length).IsLessThanOrEqualTo(2);
        await Assert.That(offsets.Max()).IsGreaterThanOrEqualTo(ids.Length - 1);

        // The refused write and the failed transaction rolled back only their own savepoints.
        await Assert.That(answers[ids.Length].Status).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That(answers[ids.Length + 1].Status).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That((await api.GetAsync(existing)).ETag).IsEqualTo(original.ETag);
        await Assert.That((await api.GetAsync(rolledBack)).Status).IsEqualTo(HttpStatusCode.NotFound);
        foreach (var id in ids)
        {
            await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task A_rolled_back_write_restores_what_an_earlier_write_of_its_batch_captured()
    {
        await using var api = await DocumentApi.StartAsync();
        var existing = DocumentApi.NewId();
        await api.PutAsync(existing, new { n = 0 });
        var store = await StoreAsync(api);
        var shared = DocumentApi.NewId();
        var other = DocumentApi.NewId();

        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var held = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        Task<ApiResponse> primer;
        Task<ApiResponse> first;
        Task<ApiResponse> failing;
        Task<ApiResponse> last;
        try
        {
            // A primer takes the batch that waits for the writer; the next three then queue, in this order, for one batch.
            primer = api.PutAsync(DocumentApi.NewId(), new { n = 0 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(engine.Io.Admitted == 2 && store.QueuedWrites == 0));
            first = api.PutAsync(shared, new { n = 1 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites >= 1));
            // Its first put replaces what `first` captured in the same batch; its second put then fails, after it.
            failing = api.TransactAsync(
                new { op = "put", collection = DocumentApi.Collection, id = shared, document = new { n = 2 } },
                new { op = "put", collection = DocumentApi.Collection, id = existing, document = new { n = 5 }, if_match = "tbv1-00000000000000000000000000000000" });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites >= 2));
            last = api.PutAsync(other, new { n = 3 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites >= 3));
        }
        finally
        {
            await held.DisposeAsync();
        }

        await Assert.That((await primer).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await first).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await last).Json.GetProperty("offset").GetString()).IsEqualTo((await first).Json.GetProperty("offset").GetString());
        await Assert.That((await failing).Status).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That((await last).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.GetAsync(shared)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);

        // The change groups replicated to peers carry the first write's state, never the rolled-back one.
        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var captured = new List<DocumentChange>();
        for (var sequence = await reader.GetLastChangeSequenceAsync(CancellationToken.None); sequence > 0 && captured.Count < 3; sequence--)
        {
            captured.AddRange(await reader.ReadChangesAsync(sequence, CancellationToken.None) ?? []);
        }

        var states = captured.Where(change => change.Id == shared).Select(change => System.Text.Encoding.UTF8.GetString(change.Json!.Value.Span)).ToList();
        await Assert.That(states).IsNotEmpty();
        await Assert.That(states.All(json => json.Contains("\"n\":1", StringComparison.Ordinal))).IsTrue();
        await Assert.That(captured.Any(change => change.Id == other)).IsTrue();
    }

    [Test]
    public async Task A_storage_thread_busy_with_a_long_read_does_not_hold_up_writes()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-threads"] = "1");
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = 0 })).Status).IsEqualTo(HttpStatusCode.Created);
        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();

        // Occupy the only read thread, as a long runSql would.
        using var release = new ManualResetEventSlim();
        var busy = engine.Io.RunAsync(release.Wait, CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var written = await api.PutAsync(DocumentApi.NewId(), new { n = 1 }, cancellationToken: deadline.Token);
            await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(busy.IsCompleted).IsFalse();
        }
        finally
        {
            release.Set();
            await busy;
        }
    }

    private static async Task<SqliteDatabaseStore> StoreAsync(DocumentApi api)
    {
        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        return (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
    }
}
