using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
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
        }, StorageTransactionTests.ConfigurePool);
        var server = management.Server;
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        var client = tenant.Data;
        foreach (var database in new[] { "blocked", "idle" })
        {
            using var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/{database}", UriKind.Relative), null, deadline.Token);
            created.EnsureSuccessStatusCode();
            await StorageFixture.CreateCollectionAsync(tenant, database, "items", deadline.Token);
        }

        // Collection creation invalidates authentication. Warm it before using native counters to
        // identify the externally blocked BEGIN rather than an earlier API-key storage read.
        using (var warmed = await client.GetAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/idle/collections/items", UriKind.Relative), deadline.Token))
        {
            warmed.EnsureSuccessStatusCode();
        }

        var engine = server.Services.GetRequiredService<SqliteStorageEngine>();
        var name = await tenant.ContextAsync(management.Server, "idle", deadline.Token);
        var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(name, deadline.Token))!;
        await Assert.That(store.IdleCount).IsEqualTo(1);
        var blockedName = await tenant.ContextAsync(management.Server, "blocked", deadline.Token);
        var blockedStore = (await engine.OpenDatabaseAsync(blockedName, deadline.Token))!;
        var blockedFile = await tenant.FileAsync(server, "blocked", deadline.Token);
        var begins = new List<Task<IStorageTransaction>>();
        var requests = new List<Task<HttpResponseMessage>>();
        Exception? primaryFailure = null;
        try
        {
            Task<IStorageTransaction> blocked;
            Task<HttpResponseMessage> healthy;
            // Assertion failures release the external lock before joining any work or disposing the host.
            await using (await StorageFixture.LockAsync(blockedFile))
            {
                // A tenant HTTP write first reads the catalog. Start the known native BEGIN directly,
                // so Running == 1 cannot accidentally identify that earlier catalog lookup.
                blocked = blockedStore.BeginTransactionAsync(write: true, deadline.Token).AsTask();
                begins.Add(blocked);
                await WaitUntilAsync(() => engine.Io.Admitted == 1 && engine.Io.Running == 1, deadline.Token);
                for (var i = 0; i < 4; i++)
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    var begin = store.BeginTransactionAsync(write, cancellation.Token).AsTask();
                    begins.Add(begin);
                    try
                    {
                        await WaitUntilAsync(() => engine.Io.Admitted == 2 && engine.Io.Queued == 1, deadline.Token);
                        await Assert.That(store.IdleCount).IsEqualTo(0);
                        await cancellation.CancelAsync();
                        await Assert.ThrowsAsync<OperationCanceledException>(() => begin.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken));
                        await Assert.That(engine.Io.Admitted).IsEqualTo(1);
                        await Assert.That(engine.Io.Queued).IsEqualTo(0);
                        await Assert.That(engine.Io.Running).IsEqualTo(1);
                        await Assert.That(store.IdleCount).IsEqualTo(1);
                    }
                    finally
                    {
                        cancellation.Cancel();
                    }
                }

                // A real HTTPS request takes the admission just released while the other database stays locked.
                healthy = client.GetAsync(
                    new Uri($"/v1/projects/{tenant.ProjectId}/databases/idle/collections/items/documents/{StorageFixture.Id("missing")}", UriKind.Relative), deadline.Token);
                requests.Add(healthy);
                await WaitUntilAsync(() => engine.Io.Admitted == 2 && engine.Io.Queued == 1, deadline.Token);
                await Assert.That(healthy.IsCompleted).IsFalse();
            }

            await using (await blocked.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken))
            {
            }
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
            // The external lock is already released; a started BEGIN can still return a transaction.
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
