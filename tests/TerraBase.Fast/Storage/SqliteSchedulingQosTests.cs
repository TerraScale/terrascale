using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class SqliteSchedulingQosTests
{
    [Test]
    public async Task Hobby_saturation_preserves_a_worker_and_admission_for_paid_and_system_requests()
    {
        using var clock = new HeldHobbyClock();
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "2";
            settings["storage-queue"] = "4";
        }, services => services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock), managementCredential: true);
        using var plans = api.Service(api.User);
        var hobby = await api.CreateManagedDatabaseAsync("hobby");
        var paid = await api.CreateManagedDatabaseAsync("paid");
        using var upgraded = await plans.PutAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{paid.Organization}/plan"), new { plan = "pro" });
        upgraded.EnsureSuccessStatusCode();
        using var hobbyData = await api.CreateClientAsync(hobby.Organization, "documents:read", "documents:write");
        using var paidData = await api.CreateClientAsync(paid.Organization, "documents:read", "documents:write");
        foreach (var (data, database) in new[] { (hobbyData, hobby), (paidData, paid) })
        {
            await ManagementApi.CreateCollectionAsync(data, database, "items");
            using var warm = await data.GetAsync(ManagementApi.Uri($"{database.Data}/collections/items"));
            warm.EnsureSuccessStatusCode();
        }

        var io = api.Server.Services.GetRequiredService<SqliteStorageEngine>().Io;
        using var cancelQueued = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var pending = new List<Task<HttpResponseMessage>>();
        clock.Hold = true;
        try
        {
            var running = QueryAsync(hobbyData, hobby, TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            pending.Add(running);
            await clock.Entered.Task.WaitAsync(TUnit.Core.TestContext.Current.Execution.CancellationToken);
            var queued = QueryAsync(hobbyData, hobby, cancelQueued.Token);
            pending.Add(queued);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(io.Admitted == 2 && io.Queued == 1));
            // The two admitted Hobby operations consume their lane; a third cannot take the paid slots.
            using var refused = await QueryAsync(hobbyData, hobby, TUnit.Core.TestContext.Current.Execution.CancellationToken);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            using var paidResponse = await QueryAsync(paidData, paid, TUnit.Core.TestContext.Current.Execution.CancellationToken).WaitAsync(TUnit.Core.TestContext.Current.Execution.CancellationToken);
            await Assert.That(paidResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var healthy = await api.Server.Client.GetAsync(new Uri("/readyz", UriKind.Relative), TUnit.Core.TestContext.Current.Execution.CancellationToken);
            await Assert.That(healthy.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(running.IsCompleted).IsFalse();
            await Assert.That(queued.IsCompleted).IsFalse();

            cancelQueued.Cancel();
            try
            {
                using var unexpected = await queued;
                throw new InvalidOperationException("the queued request was not cancelled");
            }
            catch (OperationCanceledException)
            {
            }

            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(io.Queued == 0 && io.Admitted == 1));
            clock.Release();
            using var completed = await running;
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(io.Admitted == 0));
        }
        finally
        {
            clock.Release();
            cancelQueued.Cancel();
            foreach (var request in pending)
            {
                await StoragePoolTests.DisposeResponseAsync(request);
            }
        }
    }

    [Test]
    public async Task A_cold_finite_plan_write_reuses_the_batch_admission_for_quota_accounting()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-queue"] = "1");
        var id = DocumentApi.NewId();
        var created = await api.PutAsync(id, new { n = 1 });
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var read = await api.GetAsync(id);
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(api.Server.Services.GetRequiredService<SqliteStorageEngine>().Io.Admitted == 0));
    }

    private static Task<HttpResponseMessage> QueryAsync(HttpClient data, ManagedDatabaseRef database, CancellationToken cancellationToken = default) =>
        data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/collections/items/query"), new { limit = 1 }, cancellationToken);

    // Hold actual query execution on its SQLite worker, after authentication and plan resolution.
    // A synchronization barrier, rather than elapsed time, proves paid work finishes while Hobby is still executing.
    private sealed class HeldHobbyClock : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();

        public bool Hold { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long GetTimestamp()
        {
            if (Hold && QosContext.Current?.Plan == PlanTier.Hobby && Thread.CurrentThread.Name == "terrabase-sqlite")
            {
                Entered.TrySetResult();
                _release.Wait();
            }

            return TimeProvider.System.GetTimestamp();
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }
}
