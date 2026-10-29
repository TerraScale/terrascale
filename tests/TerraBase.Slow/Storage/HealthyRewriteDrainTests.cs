using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Storage;

/// <summary>
/// TS-982: a history erasure's rewrite of SYSTEM waited up to 30 s for a reader to end while holding the writer, so every
/// catalog write (ts-auth's included) stalled that long, again on each retry. The rewrite now first waits for readers without
/// the writer: writes keep committing while it waits, and it completes once the reader ends.
/// </summary>
public sealed class HealthyRewriteDrainTests
{
    private const string Probe = "rewrite_drain_probe";

    [Test]
    public async Task Catalog_writes_commit_while_a_system_rewrite_waits_for_a_reader()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        await PutAsync(store, "before", token);

        var reader = await store.BeginTransactionAsync(write: false, token);
        Task<Engine.HealthyRewrite> rewrite;
        try
        {
            _ = await reader.GetAsync(Probe, StorageFixture.Id("before"), token);
            rewrite = engine.RewriteAsync(null, 0, token).AsTask();
            // The rewrite waits for this reader (up to 10 s) without the writer.
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);

            for (var write = 0; write < 3; write++)
            {
                var started = Stopwatch.GetTimestamp();
                await PutAsync(store, $"during {write}", token).WaitAsync(TimeSpan.FromSeconds(5), token);
                await Assert.That(Stopwatch.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(2));
            }

            await Assert.That(rewrite.IsCompleted).IsFalse();
        }
        finally
        {
            await reader.DisposeAsync();
        }

        var done = await rewrite.WaitAsync(TimeSpan.FromSeconds(30), token);
        await Assert.That(done.TargetIncarnation).IsNotEqualTo(done.SourceIncarnation);
        await using var read = await store.BeginTransactionAsync(write: false, token);
        for (var write = 0; write < 3; write++)
        {
            await Assert.That(await read.GetAsync(Probe, StorageFixture.Id($"during {write}"), token)).IsNotNull();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Readers_arriving_after_the_off_writer_drain_wait_for_rewrite_completion(bool refuse)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        await PutAsync(store, "before", token);
        var before = store.Incarnation;
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.RewriteBeforeWriter = () =>
        {
            ready.TrySetResult();
            release.Wait(token);
            if (refuse)
            {
                throw new IOException("injected refusal before taking the writer");
            }
        };
        store.RewriteSessionWaiting = () => waiting.TrySetResult();
        var rewriting = engine.RewriteAsync(null, 0, token).AsTask();
        Task? reading = null;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            reading = ReadAsync();
            // Without closing reader admission, this read completes while the rewrite is still before its writer.
            var first = await Task.WhenAny(waiting.Task, reading).WaitAsync(TimeSpan.FromSeconds(10), token);
            await Assert.That(ReferenceEquals(first, waiting.Task)).IsTrue();
            await Assert.That(reading.IsCompleted).IsFalse();
            // Stopping new readers must not stop the writer during the off-writer drain.
            await PutAsync(store, "during", token).WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        finally
        {
            release.Set();
            try
            {
                if (refuse)
                {
                    _ = await Assert.ThrowsAsync<RewriteRefusedException>(async () => await rewriting);
                }
                else
                {
                    _ = await rewriting;
                }
            }
            finally
            {
                if (reading is not null)
                {
                    await reading.WaitAsync(TimeSpan.FromSeconds(30), token);
                }
            }
        }
        await Assert.That(store.Incarnation == before).IsEqualTo(refuse);
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync(Probe, StorageFixture.Id("during"), token)).IsNotNull();

        async Task ReadAsync()
        {
            await using var reader = await store.BeginTransactionAsync(write: false, token);
            await Assert.That(await reader.GetAsync(Probe, StorageFixture.Id("before"), token)).IsNotNull();
        }
    }

    private static async Task PutAsync(TursoDatabaseStore store, string id, CancellationToken token)
    {
        await using var write = await store.BeginTransactionAsync(write: true, token);
        _ = await write.PutAsync(Probe, StorageFixture.Id(id), Encoding.UTF8.GetBytes($$"""{"id":"{{EntityIds.New()}}"}"""), token);
        await write.CommitAsync(token);
    }
}
