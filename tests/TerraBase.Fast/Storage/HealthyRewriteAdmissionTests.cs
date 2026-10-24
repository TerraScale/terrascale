using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Documents;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

/// <summary>
/// TS-873: a request that pinned a database's incarnation before a healthy rewrite is still admitted after it (the same
/// logical state is in place), while a recorded copy fault or a rewrite whose rename may not be durable still refuses it.
/// Before, every pin a rewrite replaced answered 503 copy_faulted ("awaiting certified recovery").
/// </summary>
public sealed class HealthyRewriteAdmissionTests
{
    private const string Items = "items";

    [Test]
    public async Task A_pin_a_healthy_rewrite_replaced_is_admitted_and_a_real_fault_still_refuses_it()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var faults = server.Services.GetRequiredService<IReplicaCopyFaults>();
        var (database, store) = await CreateAsync(engine, token);
        var id = EntityIds.New().ToString();
        await PutAsync(store, id, token);

        // The exact admission sequence of a request: pin, check, (the rewrite lands), work, check again.
        var pin = store.Incarnation;
        await Assert.That(await faults.RefusedAsync(database, pin, token)).IsFalse();
        var first = await engine.RewriteAsync(database, 0, token);
        var second = await engine.RewriteAsync(database, 0, token);
        await Assert.That(first.SourceIncarnation).IsEqualTo(pin);
        await Assert.That(await faults.RefusedAsync(database, pin, token)).IsFalse().Because("two healthy rewrites chain back to the pin");
        await Assert.That(await faults.RefusedAsync(database, first.TargetIncarnation, token)).IsFalse();
        await Assert.That(await faults.RefusedAsync(database, EntityIds.New(), token)).IsTrue().Because("an incarnation no healthy rewrite replaced");

        // A real fault of the file in place refuses every pin, old ones included.
        await faults.RecordAsync(database, second.TargetIncarnation, "injected copy fault", token);
        await Assert.That(await faults.RefusedAsync(database, second.TargetIncarnation, token)).IsTrue();
        await Assert.That(await faults.RefusedAsync(database, pin, token)).IsTrue();
    }

    // A document read overlapping the rewrite: it pins the old file, waits behind the quiesced store, then reads the new file.
    [Test]
    public async Task A_document_read_overlapping_a_healthy_rewrite_answers_from_the_new_file()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var documents = server.Services.GetRequiredService<IDocumentService>();
        var (database, store) = await CreateAsync(engine, token);
        var id = EntityIds.New().ToString();
        await PutAsync(store, id, token);
        var before = store.Incarnation;

        var held = await store.BeginTransactionAsync(write: false, token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.RewriteWaiting = () => waiting.TrySetResult();
        var rewriting = engine.RewriteAsync(database, 0, token).AsTask();
        await waiting.Task.WaitAsync(token);
        // The rewrite has not published: the read pins the old incarnation, then waits for a session.
        await Assert.That(store.Incarnation).IsEqualTo(before);
        var reading = documents.GetAsync(new DocumentKey(database, Items, id), token).AsTask();
        await Assert.That(rewriting.IsCompleted).IsFalse();
        await held.DisposeAsync();

        var rewrite = await rewriting;
        var read = await reading;
        await Assert.That(rewrite.SourceIncarnation).IsEqualTo(before);
        // The incarnation the read pinned (the one in place when it started) is the one the rewrite replaced, and it is admitted.
        await Assert.That(await server.Services.GetRequiredService<IReplicaCopyFaults>().RefusedAsync(database, before, token)).IsFalse();
        await Assert.That(read is StoredDocument { Deleted: false } document && Encoding.UTF8.GetString(document.Json.Span) == """{"n":1}""").IsTrue();
    }

    // A rewrite whose rename may not be durable fails storage closed: no pin, old or new, is admitted.
    [Test]
    public async Task An_uncertain_rewrite_still_fails_every_pin_closed()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var faults = server.Services.GetRequiredService<IReplicaCopyFaults>();
        var (database, store) = await CreateAsync(engine, token);
        await PutAsync(store, EntityIds.New().ToString(), token);
        var pin = store.Incarnation;
        var sync = engine.DirectorySync;
        var syncs = 0;
        engine.DirectorySync = directory =>
        {
            // The second sync follows the rename.
            if (Interlocked.Increment(ref syncs) == 2)
            {
                throw new IOException("injected directory sync failure");
            }

            sync(directory);
        };

        await Assert.ThrowsAsync<RewriteUncertainException>(async () => await engine.RewriteAsync(database, 0, token));
        await Assert.That(await faults.RefusedAsync(database, pin, token)).IsTrue();
        await Assert.That(await faults.RefusedAsync(database, store.Incarnation, token)).IsTrue();
    }

    private static async Task<(TenantContext Database, TursoDatabaseStore Store)> CreateAsync(TursoStorageEngine engine, CancellationToken token)
    {
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, token);
        return (database, (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!);
    }

    private static async Task PutAsync(TursoDatabaseStore store, string id, CancellationToken token)
    {
        await using var write = await store.BeginTransactionAsync(write: true, token);
        _ = await write.CreateCollectionAsync(new CollectionInfo(Items, 1), token);
        _ = await write.PutAsync(Items, id, Encoding.UTF8.GetBytes("""{"n":1}"""), token);
        await write.CommitAsync(token);
    }
}
