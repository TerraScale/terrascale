using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class F4DiagnosticTests
{
    [Test]
    public async Task Ack_tail_read_does_not_hold_the_committed_catalog_writer()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var management = await ManagementApi.StartAsync(settings => settings["storage-threads"] = "1");
        var engine = management.Server.Services.GetRequiredService<TursoStorageEngine>();
        var source = management.Server.Services.GetRequiredService<IChangeGroupSource>();
        var store = await engine.OpenSystemDatabaseAsync(deadline.Token);
        _ = await source.HeadAsync(ChangeGroups.System, store.Incarnation, deadline.Token) ?? throw new InvalidOperationException("the copy refused its own incarnation");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = engine.Io.RunAsync(() =>
        {
            entered.SetResult();
            release.Wait(deadline.Token);
        }, deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        Task<bool>? ack = null;
        var writerCompleted = false;
        try
        {
            // Control: the dedicated writer can commit while the read pool is occupied.
            await using (var control = await store.BeginDurableTransactionAsync(deadline.Token))
            {
                _ = await control.CommitAsync(deadline.Token);
            }

            ack = source.AckAsync(ChangeGroups.System, store.Incarnation, ReplicaId.Of(1, 7, 0), 0, default, deadline.Token).AsTask();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (engine.Io.Queued == 0)
            {
                _ = await timer.WaitForNextTickAsync(deadline.Token);
            }

            using var writerBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await using var writer = await store.BeginDurableTransactionAsync(writerBudget.Token);
                _ = await writer.CommitAsync(writerBudget.Token);
                writerCompleted = true;
            }
            catch (OperationCanceledException) when (writerBudget.IsCancellationRequested)
            {
            }
        }
        finally
        {
            release.Set();
            await read.WaitAsync(deadline.Token);
            if (ack is not null)
            {
                await ack.WaitAsync(deadline.Token);
            }
        }

        await using (var recovered = await store.BeginDurableTransactionAsync(deadline.Token))
        {
            _ = await recovered.CommitAsync(deadline.Token);
        }
        await Assert.That(writerCompleted).IsTrue();
        await Assert.That(await ack!).IsTrue().Because("the ack names the copy in place");
    }
}
