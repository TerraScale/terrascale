using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class StorageBeginCancellationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancelling_a_queued_begin_returns_its_idle_session_before_a_blocked_worker_finishes(bool write)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "2";
        }, StorageTransactionTests.ConfigurePool, time: new FakeTimeProvider());
        var server = management.Server;
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        var client = tenant.Data;
        foreach (var database in new[] { "idle" })
        {
            using var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/{database}", UriKind.Relative), null, deadline.Token);
            created.EnsureSuccessStatusCode();
            await StorageFixture.CreateCollectionAsync(tenant, database, "items", deadline.Token);
        }

        // The owned clock holds periodic maintenance between ticks. Drain fixture storage before warming
        // authentication, so it cannot compete for the single Hobby admission in this deliberately tiny pool.
        await WaitUntilAsync(() => engine.Io.Admitted == 0 && engine.Io.Queued == 0 && engine.Io.Running == 0, deadline.Token);
        // Collection creation invalidates authentication. Warm it before using native counters to
        // identify the externally blocked BEGIN rather than an earlier API-key storage read.
        using (var warmed = await client.GetAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/idle/collections/items", UriKind.Relative), deadline.Token))
        {
            warmed.EnsureSuccessStatusCode();
        }

        var name = await tenant.ContextAsync(management.Server, "idle", deadline.Token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(name, deadline.Token))!;
        await WaitUntilAsync(() => engine.Io.Admitted == 0 && engine.Io.Queued == 0 && engine.Io.Running == 0, deadline.Token);
        await Assert.That(store.IdleCount).IsEqualTo(1);
        var begins = new List<Task<IStorageTransaction>>();
        var requests = new List<Task<HttpResponseMessage>>();
        Exception? primaryFailure = null;

        // Occupy the only read thread, as a long read would, and for write begins also the database's write thread,
        // which runs every call of its write transactions.
        using var release = new ManualResetEventSlim();
        var blockedRead = engine.Io.RunAsync(release.Wait, CancellationToken.None);
        var blockedWrite = write ? store.WriteLane.RunAsync(release.Wait, CancellationToken.None) : Task.CompletedTask;
        int Queued()
        {
            return write ? store.WriteLane.Queued : engine.Io.Queued;
        }

        try
        {
            Task<HttpResponseMessage> healthy;
            try
            {
                await WaitUntilAsync(() => engine.Io.Running == 1 && (!write || store.WriteLane.Running), deadline.Token);
                for (var i = 0; i < 4; i++)
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    var begin = store.BeginTransactionAsync(write, cancellation.Token).AsTask();
                    begins.Add(begin);
                    try
                    {
                        // Shared counters can describe prerequisite/background work before this BEGIN rents its session.
                        await WaitUntilAsync(() => engine.Io.Admitted == 1 && Queued() == 1
                            && store.IdleCount == (write ? 1 : 0), deadline.Token);
                        // A read takes the idle session; a write uses the database's own writer connection instead.
                        await Assert.That(store.IdleCount).IsEqualTo(write ? 1 : 0);
                        await cancellation.CancelAsync();
                        await Assert.ThrowsAsync<OperationCanceledException>(() => begin.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken));
                        await Assert.That(engine.Io.Admitted).IsEqualTo(0);
                        await Assert.That(Queued()).IsEqualTo(0);
                        await Assert.That(engine.Io.Running).IsEqualTo(1);
                        await Assert.That(store.IdleCount).IsEqualTo(1);
                    }
                    finally
                    {
                        cancellation.Cancel();
                    }
                }

                // A real HTTPS request takes the admission just released while the read thread stays busy.
                healthy = client.GetAsync(
                    new Uri($"/v1/projects/{tenant.ProjectId}/databases/idle/collections/items/documents/by-key?id={StorageFixture.Id("missing")}", UriKind.Relative), deadline.Token);
                requests.Add(healthy);
                await WaitUntilAsync(() => engine.Io.Admitted == 1 && engine.Io.Queued == 1, deadline.Token);
                await Assert.That(healthy.IsCompleted).IsFalse();
            }
            finally
            {
                release.Set();
            }

            await blockedRead.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await blockedWrite.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            using var read = await healthy.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await WaitUntilAsync(() => engine.Io.Admitted == 0 && engine.Io.Queued == 0, deadline.Token);
            await Assert.That(store.IdleCount).IsEqualTo(1);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            // The threads are released; a started BEGIN can still return a transaction.
            await StoragePoolTests.JoinCleanupAsync(begins.Select(StorageTransactionTests.DisposeBeginAsync)
                .Concat(requests.Select(StoragePoolTests.DisposeResponseAsync))
                .Append(deadline.CancelAsync()), primaryFailure);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
