using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class A6StartupRecoveryTests
{
    [Test]
    public async Task A_persisted_rebuild_resumes_after_application_started_without_a_timer_tick()
    {
        await using var directory = new TempDirectory();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var frozen = time.GetUtcNow();
        var token = TestContext.Current!.Execution.CancellationToken;
        TenantContext database;
        RebuildRecord pending;
        var first = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory.Root,
            time: time, failedStartCleanup: directory.RetainCleanup);
        try
        {
            using var tenant = await first.CreateTenantAsync("documents:read", "documents:write");
            using var owner = first.As(tenant.User);
            _ = await first.CreateDatabaseAsync(tenant.OrganizationId, tenant.ProjectId, "main", owner);
            var services = first.Server.Services;
            database = await services.GetRequiredService<ITenantDirectory>()
                .FindDatabaseAsync(tenant.OrganizationId, tenant.ProjectId, "main", token)
                is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
            var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token)
                ?? throw new InvalidOperationException("the test database is missing");
            var storage = services.GetRequiredService<IRebuildStorage>();
            pending = new RebuildRecord(database, store.Incarnation, storage.SidePath(database, store.Incarnation), RebuildPhase.Pending, null);
            // SaveAsync persists the row without emitting the runtime fault wake.
            var ledger = services.GetRequiredService<IRebuildLedger>();
            await ledger.SaveAsync(pending, token);
            await Assert.That(await ledger.GetAsync(database, token)).IsEqualTo(pending);
            await Assert.That(storage.SideExists(pending.SidePath)).IsFalse();
        }
        finally
        {
            await StopAsync(first);
        }

        var firstScan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory.Root,
            services => services.AddSingleton<IRebuildRecovery>(provider => new ObservedStartupRecovery(
                provider.GetRequiredService<SqliteStorageEngine>().Ledger,
                provider.GetRequiredService<IHostApplicationLifetime>(), firstScan)),
            time: time, failedStartCleanup: directory.RetainCleanup);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            await Assert.That(await firstScan.Task.WaitAsync(deadline.Token)).IsTrue()
                .Because("the production worker must not scan SYSTEM before ApplicationStarted");
            var services = restarted.Server.Services;
            var ledger = services.GetRequiredService<IRebuildLedger>();
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
            {
                using var polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                return await ledger.GetAsync(database, polling.Token) is { Phase: RebuildPhase.Swapped, NewIncarnation: not null };
            });
            var record = await ledger.GetAsync(database, deadline.Token)
                ?? throw new InvalidOperationException("the persisted rebuild record is missing");
            var rebuilt = record.NewIncarnation!.Value;
            await Assert.That(record.OldIncarnation).IsEqualTo(pending.OldIncarnation);
            await Assert.That(rebuilt).IsNotEqualTo(pending.OldIncarnation);
            await Assert.That(record.SidePath).IsEqualTo(pending.SidePath);
            var storage = services.GetRequiredService<IRebuildStorage>();
            await Assert.That(storage.SideExists(record.SidePath)).IsTrue();
            var certification = services.GetRequiredService<IRebuildCertification>();
            await Assert.That(await certification.RefillAsync(database, rebuilt, deadline.Token)).IsFalse();
            await Assert.That(await certification.CompleteAsync(database, pending.OldIncarnation, rebuilt, deadline.Token)).IsFalse();
            var copy = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token)
                ?? throw new InvalidOperationException("the replacement is missing");
            await Assert.That(copy.Incarnation).IsEqualTo(rebuilt);
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            {
                await using var refused = await copy.BeginTransactionAsync(write: true, deadline.Token);
            });
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            {
                await using var refused = await copy.BeginDurableTransactionAsync(deadline.Token);
            });
            await Assert.That((await ledger.GetAsync(database, deadline.Token))?.Phase).IsEqualTo(RebuildPhase.Swapped);
            await Assert.That(storage.SideExists(record.SidePath)).IsTrue();
            await Assert.That(time.GetUtcNow()).IsEqualTo(frozen).Because("no retry tick or clock advance may resume this rebuild");
        }
        finally
        {
            await StopAsync(restarted);
        }
    }

    private static async Task StopAsync(ManagementApi api)
    {
        try
        {
            await api.DisposeAsync();
        }
        finally
        {
            await api.CleanupCompletion;
        }
    }

    private sealed class ObservedStartupRecovery(
        IRebuildRecovery inner, IHostApplicationLifetime lifetime, TaskCompletionSource<bool> firstScan) : IRebuildRecovery
    {
        public async IAsyncEnumerable<RebuildRecord> PendingAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = firstScan.TrySetResult(lifetime.ApplicationStarted.IsCancellationRequested);
            await foreach (var record in inner.PendingAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }

        public Task WaitAsync(CancellationToken cancellationToken) => inner.WaitAsync(cancellationToken);
    }

    private sealed class TempDirectory : IAsyncDisposable
    {
        private readonly List<Task> _cleanup = [];

        public string Root { get; } = Directory.CreateTempSubdirectory("terrabase-a6-startup-").FullName;

        public void RetainCleanup(Task cleanup) => _cleanup.Add(cleanup);

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(_cleanup);
            Directory.Delete(Root, recursive: true);
        }
    }
}
