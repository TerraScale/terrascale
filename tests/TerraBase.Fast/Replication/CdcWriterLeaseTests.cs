using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// F4 (diagnosis on #173): a peer's ack commits on its database's writer, then refreshes the tail-lag gauge with a read on
/// the read pool. That read must not keep the writer: with the only read worker busy, another durable writer still commits
/// while the ack's read waits.
/// </summary>
public sealed class CdcWriterLeaseTests
{
    [Test]
    public async Task An_acks_tail_read_does_not_hold_its_databases_writer()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var api = await ManagementApi.StartAsync(settings => settings["storage-threads"] = "1");
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await CatalogFixture.RegisterAsync(api, api.User, database, "main", []);

        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var source = api.Server.Services.GetRequiredService<IChangeGroupSource>();
        var store = await engine.OpenDatabaseAsync(database, deadline.Token) ?? throw new InvalidOperationException("the database is missing");
        _ = await source.HeadAsync(database, store.Incarnation, deadline.Token) ?? throw new InvalidOperationException("the copy refused its own incarnation");

        // The read pool's only worker is taken until the end.
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
            // Control: the writer commits while the read pool is busy.
            await using (var control = await store.BeginDurableTransactionAsync(deadline.Token))
            {
                _ = await control.CommitAsync(deadline.Token);
            }

            // The ack commits, then queues its tail read behind the busy worker; a second writer must still get the writer.
            ack = source.AckAsync(database, store.Incarnation, ReplicaId.Of(1, 7, 0), 0, default, deadline.Token).AsTask();
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

        await Assert.That(writerCompleted).IsTrue();
        await Assert.That(await ack!).IsTrue().Because("the ack names the copy in place");
    }
}
