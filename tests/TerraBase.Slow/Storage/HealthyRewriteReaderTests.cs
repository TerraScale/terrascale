using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

/// <summary>
/// TS-982: a history erasure's rewrite of SYSTEM must never stall the catalog writers (ts-auth's) behind a reader it waits for,
/// and the continuous backup's tap, the handle that kept SYSTEM from draining on prod, must neither hold it nor reopen meanwhile.
/// </summary>
public sealed class HealthyRewriteReaderTests
{
    private const string Probe = "rewrite_reader_probe";

    // Below the 2 s a write queued behind the writer-held drain waited (on prod 30 s), and above what a loaded 4-core CI
    // runner adds to one SYSTEM write while the whole suite runs (658 ms seen; locally the slowest is a few ms).
    private static readonly TimeSpan WriteBound = TimeSpan.FromSeconds(1);

    [Test]
    public async Task A_reader_held_through_a_system_rewrite_refuses_it_without_delaying_a_write()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        await PutAsync(store, "before", token);
        var before = store.Incarnation;

        var slowest = TimeSpan.Zero;
        var writes = 0;
        await using (var reader = await store.BeginTransactionAsync(write: false, token))
        {
            _ = await reader.GetAsync(Probe, StorageFixture.Id("before"), token);
            var rewriting = engine.RewriteAsync(null, 0, token).AsTask();
            // Writes go on through the whole attempt, until the rewrite gives up on the reader.
            while (!rewriting.IsCompleted)
            {
                var started = Stopwatch.GetTimestamp();
                await PutAsync(store, $"during {writes}", token).WaitAsync(TimeSpan.FromSeconds(30), token);
                var took = Stopwatch.GetElapsedTime(started);
                slowest = took > slowest ? took : slowest;
                writes++;
            }

            await Assert.That(async () => await rewriting).Throws<RewriteRefusedException>();
        }

        await Assert.That(writes).IsGreaterThan(0);
        await Assert.That(slowest).IsLessThan(WriteBound);
        await Assert.That(store.Incarnation).IsEqualTo(before);

        // With the reader gone the next attempt goes through and keeps every write.
        var rewrite = await engine.RewriteAsync(null, 0, token);
        await Assert.That(rewrite.SourceIncarnation).IsEqualTo(before);
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync(Probe, StorageFixture.Id($"during {writes - 1}"), token)).IsNotNull();
    }

    [Test]
    public async Task A_system_backup_tap_ends_as_a_rewrite_starts_and_reopens_only_after_it()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        await PutAsync(store, "before", token);
        var tap = await engine.OpenTapAsync(null, token) ?? throw new InvalidOperationException("tap is absent");
        await Assert.That(tap.Ended).IsFalse();

        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.RewriteWaiting = () => waiting.TrySetResult();
        var rewriting = engine.RewriteAsync(null, 0, token).AsTask();
        await waiting.Task.WaitAsync(token);
        await Assert.That(tap.Ended).IsTrue();
        // A tap opened now would hold the old file the rewrite waits for: it is refused at once instead of joining the wait.
        await Assert.That(async () => await engine.OpenTapAsync(null, token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token))
            .Throws<InvalidOperationException>();
        await Assert.That(rewriting.IsCompleted).IsFalse();

        await tap.DisposeAsync();
        var rewrite = await rewriting.WaitAsync(TimeSpan.FromSeconds(30), token);
        await Assert.That(rewrite.TargetIncarnation).IsNotEqualTo(rewrite.SourceIncarnation);
        await using var reopened = await engine.OpenTapAsync(null, token) ?? throw new InvalidOperationException("tap is absent");
        await Assert.That(reopened.Ended).IsFalse();
    }

    private static async Task PutAsync(TursoDatabaseStore store, string id, CancellationToken token)
    {
        await using var write = await store.BeginTransactionAsync(write: true, token);
        _ = await write.PutAsync(Probe, StorageFixture.Id(id), Encoding.UTF8.GetBytes($$"""{"id":"{{EntityIds.New()}}"}"""), token);
        await write.CommitAsync(token);
    }
}
