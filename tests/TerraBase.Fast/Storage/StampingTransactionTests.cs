using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class StampingTransactionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_batch_refused_by_the_wrapper_clears_its_result_prefix_without_writing(bool streaming)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 }, cancellationToken: token)).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(await api.TenantOfAsync(cancellationToken: token), token))!;
        var id = DocumentApi.NewId();
        (long Version, DocumentVersion? Previous)[] results = [(7, new DocumentVersion(3, false)), (8, new DocumentVersion(4, false))];
        await using (var transaction = await store.BeginTransactionAsync(write: true, token))
        {
            await using var changes = transaction.QuerySqlAsync(new SqlQuery(DocumentApi.Collection, null, [], 100), token).GetAsyncEnumerator();
            if (streaming)
            {
                await Assert.That(await changes.MoveNextAsync()).IsTrue();
            }
            else
            {
                await Assert.ThrowsAsync<DocumentValidationException>(() =>
                    transaction.PutAsync(DocumentApi.Collection, id, "[]"u8.ToArray(), token).AsTask());
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                transaction.PutBatchAsync([(DocumentApi.Collection, id, "{}"u8.ToArray())], results, token).AsTask());
            await Assert.That(results[0]).IsEqualTo((0L, (DocumentVersion?)null));
            await Assert.That(results[1]).IsEqualTo((8L, (DocumentVersion?)new DocumentVersion(4, false)));
        }

        (await api.GetAsync(id, cancellationToken: token)).Status.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancelling_a_query_stream_waiting_behind_an_operation_restores_submissions(bool raw)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-threads"] = "1", cancellationToken: deadline.Token);
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 }, cancellationToken: deadline.Token)).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var native = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var engine = raw ? native : api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(await api.TenantOfAsync(cancellationToken: deadline.Token), deadline.Token))!;
        await using var transaction = await store.BeginTransactionAsync(write: false, deadline.Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var changes = transaction.QuerySqlAsync(new SqlQuery(DocumentApi.Collection, null, [], 100), cancellation.Token).GetAsyncEnumerator();
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
        }, StorageTransactionTests.ConfigurePool, time: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var server = management.Server;
        await using var serverCleanup = management;
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        var client = tenant.Data;
        using var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/ordered", UriKind.Relative), null, deadline.Token);
        created.EnsureSuccessStatusCode();
        await StorageFixture.CreateCollectionAsync(tenant, "ordered", "items", deadline.Token);
        var database = await tenant.ContextAsync(management.Server, "ordered", deadline.Token);
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var native = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(database, deadline.Token))!;
        var nativeStore = (TursoDatabaseStore)(await native.OpenDatabaseAsync(database, deadline.Token))!;
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
            // A separately admitted call occupies the thread that runs this write transaction's calls (its database's
            // write lane). Its event makes submission order deterministic without relying on scheduler timing.
            var blocker = nativeStore.WriteLane.RunAsync(() =>
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
            // The disposal released the database's writer: the next write transaction begins on it at once.
            var next = await store.BeginTransactionAsync(write: true, deadline.Token);
            await using var nextCleanup = next;
            await transaction.DisposeAsync();
            await Assert.That(native.Io.Admitted).IsEqualTo(1);
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
