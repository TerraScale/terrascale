using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Documents;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Storage;

public sealed class HealthyRewriteAdmissionTests
{
    private const string Items = "items";

    [Test]
    public async Task Healthy_rewrites_preserve_write_admission_and_a_real_fault_refuses_it()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var faults = engine.Ledger;
        var (database, store) = await CreateAsync(engine, token);
        var id = EntityIds.New().ToString();
        await PutAsync(store, id, token);
        var pin = store.Incarnation;
        await Assert.That(faults.Refusal(store)).IsNull();
        var first = await engine.RewriteAsync(database, 0, token);
        var second = await engine.RewriteAsync(database, 0, token);
        await Assert.That(first.SourceIncarnation).IsEqualTo(pin);
        await Assert.That(second.SourceIncarnation).IsEqualTo(first.TargetIncarnation);
        await Assert.That(faults.Refusal(store)).IsNull();
        await PutAsync(store, EntityIds.New().ToString(), token);
        await faults.RecordAsync(database, second.TargetIncarnation, "injected copy fault");
        await Assert.That(faults.Refusal(store)).IsNotNull();
        await Assert.ThrowsAsync<StorageCopyFaultedException>(async () => await PutAsync(store, EntityIds.New().ToString(), token));
    }

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
        await Assert.That(store.Incarnation).IsEqualTo(before);
        var reading = documents.GetAsync(new DocumentKey(database, Items, id), token).AsTask();
        await Assert.That(rewriting.IsCompleted).IsFalse();
        await held.DisposeAsync();

        var rewrite = await rewriting;
        var read = await reading;
        await Assert.That(rewrite.SourceIncarnation).IsEqualTo(before);
        await Assert.That(engine.Ledger.Refusal(store)).IsNull();
        await Assert.That(read is StoredDocument { Deleted: false } document && Encoding.UTF8.GetString(document.Json.Span) == """{"n":1}""").IsTrue();
    }

    [Test]
    public async Task An_uncertain_rewrite_refuses_subsequent_writes()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var faults = engine.Ledger;
        var (database, store) = await CreateAsync(engine, token);
        await PutAsync(store, EntityIds.New().ToString(), token);
        var sync = engine.DirectorySync;
        var syncs = 0;
        engine.DirectorySync = directory =>
        {
            if (Interlocked.Increment(ref syncs) == 2)
            {
                throw new IOException("injected directory sync failure");
            }

            sync(directory);
        };

        await Assert.ThrowsAsync<RewriteUncertainException>(async () => await engine.RewriteAsync(database, 0, token));
        await Assert.That(faults.Refusal(store)).IsNotNull();
        await Assert.ThrowsAsync<StorageCopyFaultedException>(async () => await PutAsync(store, EntityIds.New().ToString(), token));
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
