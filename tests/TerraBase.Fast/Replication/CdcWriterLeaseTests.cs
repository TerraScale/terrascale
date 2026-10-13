using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
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
        var commit = JsonSerializer.Serialize(new
        {
            id = EntityIds.New().ToString(),
            ops = new object[]
            {
                new { op = "create_organization", id = database.OrganizationId, name = $"lease-{database.OrganizationId}" },
                new { op = "create_project", organization = database.OrganizationId, id = database.ProjectId, name = "lease" },
                new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "main", id = database.DatabaseId },
            },
        });
        using (var service = api.Service(api.User))
        {
            using var created = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(created.IsSuccessStatusCode).IsTrue().Because(await created.Content.ReadAsStringAsync());
        }

        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var source = api.Server.Services.GetRequiredService<IChangeGroupSource>();
        var store = await engine.OpenDatabaseAsync(database, deadline.Token) ?? throw new InvalidOperationException("the database is missing");
        _ = await source.HeadAsync(database, deadline.Token);

        // The read pool's only worker is taken until the end.
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = engine.Io.RunAsync(() =>
        {
            entered.SetResult();
            release.Wait(deadline.Token);
        }, deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        Task? ack = null;
        var writerCompleted = false;
        try
        {
            // Control: the writer commits while the read pool is busy.
            await using (var control = await store.BeginDurableTransactionAsync(deadline.Token))
            {
                _ = await control.CommitAsync(deadline.Token);
            }

            // The ack commits, then queues its tail read behind the busy worker; a second writer must still get the writer.
            ack = source.AckAsync(database, ReplicaId.Of(1, 7, 0), 0, deadline.Token).AsTask();
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
    }
}
