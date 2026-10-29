using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Slow.Storage;

public sealed class WorkerExecutionContextTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Database_creation_workers_do_not_inherit_the_starting_callers_execution_context(bool write)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var ambient = new AsyncLocal<object?>();
        var marker = new object();
        var observed = new TaskCompletionSource<(object? Ambient, Thread Worker)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startingFlowSuppressed = true;
        TursoStorageEngine? engine = null;
        Task? probe = null;
        await using var management = await ManagementApi.StartAsync(
            settings => settings["storage-threads"] = "1",
            services => services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<TerraBaseOptions>();
                var previous = ambient.Value;
                try
                {
                    if (!write)
                    {
                        // Mark the actual I/O pool constructor, even if the host isolated its startup context.
                        ambient.Value = marker;
                        startingFlowSuppressed = ExecutionContext.IsFlowSuppressed();
                    }

                    engine = new TursoStorageEngine(
                        options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                        provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                        provider.GetRequiredService<ILogger<TursoStorageEngine>>(), provider.GetRequiredKeyedService<IMasterKey>(EngineExtensions.DatabaseMasterKey), options.ShutdownGrace,
                        readinessCacheTtl: options.ReadinessCacheTtl,
                        checkpoint: provider.GetRequiredService<DatabaseLifecycleCheckpoint>(),
                        afterBootstrap: ObserveWorker, queryBudget: options.QueryBudget);
                    return engine;
                }
                finally
                {
                    ambient.Value = previous;
                }
            }));
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        Task<HttpResponseMessage>? creation = null;
        Exception? primaryFailure = null;
        try
        {
            creation = tenant.Data.PutAsync(tenant.Database("context"), null, deadline.Token);
            var (capturedAmbient, worker) = await observed.Task.WaitAsync(deadline.Token);
            // The held callback keeps the real worker alive without waiting for the writer's idle timeout.
            await Assert.That(startingFlowSuppressed).IsFalse();
            await Assert.That(worker.IsAlive).IsTrue();
            await Assert.That(worker.IsThreadPoolThread).IsFalse();
            await Assert.That(worker.Name).IsEqualTo(write ? "terrabase-turso-writer" : "terrabase-turso");
            await Assert.That(capturedAmbient).IsNull();
            using var health = await management.Server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
            await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);

            release.Set();
            using var created = await creation.WaitAsync(deadline.Token);
            created.EnsureSuccessStatusCode();
            await probe!.WaitAsync(deadline.Token);
            await StorageFixture.CreateCollectionAsync(tenant, "context", "items", deadline.Token);
            var document = tenant.Database("context", $"/collections/items/documents/by-key?id={StorageFixture.Id("a")}");
            using var written = await tenant.Data.PutAsJsonAsync(document, new { n = 1 }, deadline.Token);
            written.EnsureSuccessStatusCode();
            using var read = await tenant.Data.GetAsync(document, deadline.Token);
            read.EnsureSuccessStatusCode();
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            release.Set();
            await deadline.CancelAsync();
            if (creation is not null)
            {
                await StoragePoolHelpers.JoinCleanupAsync([StoragePoolHelpers.DisposeResponseAsync(creation)], primaryFailure);
            }
            if (probe is not null)
            {
                await StoragePoolHelpers.JoinCleanupAsync([probe], primaryFailure);
            }
        }

        void ObserveWorker(TursoDatabaseStore store)
        {
            if (store.Tenant is null || probe is not null)
            {
                return;
            }

            var previous = ambient.Value;
            try
            {
                if (write)
                {
                    // Bootstrap uses the read pool; this store's write lane has never started yet.
                    ambient.Value = marker;
                    startingFlowSuppressed = ExecutionContext.IsFlowSuppressed();
                }

                TursoRunner runner = write ? store.WriteLane : engine!.Io;
                // Initialization already owns admission; use its actual runner before baseline seeding.
                probe = runner.RunAsync(() =>
                {
                    observed.TrySetResult((ambient.Value, Thread.CurrentThread));
                    release.Wait(deadline.Token);
                }, deadline.Token);
            }
            finally
            {
                ambient.Value = previous;
            }
        }
    }
}
