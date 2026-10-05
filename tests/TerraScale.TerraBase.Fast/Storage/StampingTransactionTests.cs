using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Engine.Sqlite;
using TerraScale.TerraBase.Fast.Documents;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Storage;

public sealed class StampingTransactionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancelling_a_changes_stream_waiting_behind_an_operation_restores_submissions(bool raw)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-threads"] = "1", cancellationToken: deadline.Token);
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 }, cancellationToken: deadline.Token)).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var native = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var engine = raw ? native : api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(await api.TenantOfAsync(cancellationToken: deadline.Token), deadline.Token))!;
        await using var transaction = await store.BeginTransactionAsync(write: false, deadline.Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var changes = transaction.ChangesAsync(DocumentApi.Collection, null, 0, long.MaxValue, cancellation.Token).GetAsyncEnumerator();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new List<Task>();
        Exception? primaryFailure = null;
        native.Io.Admit();
        try
        {
            pending.Add(native.Io.RunAsync(() =>
            {
                entered.TrySetResult();
                release.Wait(deadline.Token);
            }, deadline.Token));
            await entered.Task.WaitAsync(deadline.Token);
            var read = transaction.GetOffsetAsync(deadline.Token).AsTask();
            pending.Add(read);
            var moving = changes.MoveNextAsync().AsTask();
            pending.Add(moving);
            await Assert.That(moving.IsCompleted).IsFalse();
            // Reservation precedes gate acquisition: neither call may queue behind the waiting stream.
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.GetOffsetAsync(deadline.Token).AsTask().WaitAsync(deadline.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.DisposeAsync().AsTask().WaitAsync(deadline.Token));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => moving.WaitAsync(deadline.Token));
            var later = transaction.GetOffsetAsync(deadline.Token).AsTask();
            pending.Add(later);
            release.Set();
            await Assert.That(await later.WaitAsync(deadline.Token)).IsEqualTo(await read);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            release.Set();
            cancellation.Cancel();
            deadline.Cancel();
            try
            {
                await StoragePoolTests.JoinCleanupAsync(pending, primaryFailure);
            }
            finally
            {
                native.Io.Release();
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Submitted_reads_and_disposal_wait_for_every_earlier_write(bool disposeWhileQueued)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var management = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
        }, StorageTransactionTests.ConfigurePool);
        var server = management.Server;
        await using var serverCleanup = management;
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        var client = tenant.Data;
        using var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/ordered", UriKind.Relative), null, deadline.Token);
        created.EnsureSuccessStatusCode();
        await StorageFixture.CreateCollectionAsync(tenant, "ordered", "items", deadline.Token);
        var database = await tenant.ContextAsync(management.Server, "ordered", deadline.Token);
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var native = server.Services.GetRequiredService<SqliteStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(database, deadline.Token))!;
        var nativeStore = (SqliteDatabaseStore)(await native.OpenDatabaseAsync(database, deadline.Token))!;
        var transaction = await store.BeginTransactionAsync(write: true, deadline.Token);
        await using var transactionCleanup = transaction;
        var idA = StorageFixture.Id("a");
        var idB = StorageFixture.Id("b");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new List<Task>();
        Task<StoredDocument?>? read = null;
        Task? firstDisposal = null;
        native.Io.Admit();
        var blockerAdmissionHeld = true;
        Exception? primaryFailure = null;
        try
        {
            // A separately admitted maintenance call occupies the actual engine's only worker.
            // Its event makes submission order deterministic without relying on scheduler timing.
            var blocker = native.Io.RunAsync(() =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(20), deadline.Token))
                {
                    throw new TimeoutException("the transaction-ordering test did not release its worker");
                }

                return 0;
            }, deadline.Token);
            pending.Add(blocker);
            await entered.Task.WaitAsync(deadline.Token);
            pending.Add(transaction.PutAsync("items", idA, Encoding.UTF8.GetBytes("{\"n\":1}"), deadline.Token).AsTask());
            pending.Add(transaction.PutAsync("items", idB, Encoding.UTF8.GetBytes("{\"n\":2}"), deadline.Token).AsTask());
            read = transaction.GetAsync("items", idB, deadline.Token).AsTask();
            pending.Add(read);
            pending.Add(transaction.CommitAsync(deadline.Token).AsTask());
            if (disposeWhileQueued)
            {
                firstDisposal = transaction.DisposeAsync().AsTask();
                pending.Add(firstDisposal);
                var secondDisposal = transaction.DisposeAsync().AsTask();
                pending.Add(secondDisposal);
                await Assert.That(ReferenceEquals(firstDisposal, secondDisposal)).IsTrue();
                await Assert.That(firstDisposal.IsCompleted).IsFalse();
                await Assert.That(secondDisposal.IsCompleted).IsFalse();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.GetAsync("items", idA, deadline.Token).AsTask());
                await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.PutAsync("items", idA, Encoding.UTF8.GetBytes("{}"), deadline.Token).AsTask());
            }

            await Assert.That(read.IsCompleted).IsFalse();
            await Assert.That(native.Io.Admitted).IsEqualTo(2);
            await Assert.That(nativeStore.IdleCount).IsEqualTo(0);

            release.Set();
            await Task.WhenAll(pending).WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            native.Io.Release();
            blockerAdmissionHeld = false;
            await transaction.DisposeAsync();

            var document = await read;
            await Assert.That(document?.Id).IsEqualTo(idB);
            using var body = JsonDocument.Parse(document!.Json);
            await Assert.That(body.RootElement.GetProperty("n").GetInt32()).IsEqualTo(2);
            await Assert.That(native.Io.Admitted).IsEqualTo(0);
            await Assert.That(nativeStore.IdleCount).IsEqualTo(1);
            var next = await store.BeginTransactionAsync(write: true, deadline.Token);
            await using var nextCleanup = next;
            await transaction.DisposeAsync();
            await Assert.That(native.Io.Admitted).IsEqualTo(1);
            await Assert.That(nativeStore.IdleCount).IsEqualTo(0);
            await Assert.That((await next.GetAsync("items", idA, deadline.Token))?.Id).IsEqualTo(idA);
            await Assert.That((await next.GetAsync("items", idB, deadline.Token))?.Id).IsEqualTo(idB);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            deadline.Cancel();
            release.Set();
            try
            {
                await StoragePoolTests.JoinCleanupAsync(pending, primaryFailure);
            }
            finally
            {
                if (blockerAdmissionHeld)
                {
                    native.Io.Release();
                }
            }
        }
    }
}
