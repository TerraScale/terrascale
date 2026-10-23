using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class StorageConstructionTests
{
    [Test]
    [Arguments(-1, 1, "threads")]
    [Arguments(0, 1, "threads")]
    [Arguments(1, -1, "maxAdmitted")]
    [Arguments(1, 0, "maxAdmitted")]
    public async Task Pool_constructor_rejects_nonpositive_limits_before_creating_workers_during_real_host_startup(int threads, int maxAdmitted, string parameter)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var directory = Directory.CreateTempSubdirectory("terrabase-construction-").FullName;
        var created = 0;
        try
        {
            var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            {
                await using var unexpected = await TerraBaseTestServer.StartAsync(settings =>
                {
                    settings["data-directory"] = directory;
                    settings["storage-threads"] = "1";
                }, services =>
                {
                    // DI owns a pool if validation regresses; construct it only in the hosted service's StartAsync.
                    services.AddSingleton(provider => new TursoIoPool(
                        threads, maxAdmitted, provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                        createWorker: _ =>
                        {
                            Interlocked.Increment(ref created);
                            throw new InvalidOperationException("Invalid pool limits must not create workers.");
                        }));
                    services.AddSingleton<IHostedService>(provider => new PoolConstructionService(
                        () => provider.GetRequiredService<TursoIoPool>()));
                });
            });
            await Assert.That(failure!.GetType()).IsEqualTo(typeof(ArgumentOutOfRangeException));
            await Assert.That(failure.ParamName).IsEqualTo(parameter);
            await Assert.That(failure.Data.Contains("CleanupFailure")).IsFalse();
            await Assert.That(created).IsEqualTo(0);
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsFalse();

            await using var reopened = await TerraBaseTestServer.StartAsync(settings =>
            {
                settings["data-directory"] = directory;
                settings["storage-threads"] = "1";
            });
            using var health = await reopened.Client.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
            await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_failed_second_worker_launch_joins_the_first_before_releasing_ownership_and_allows_immediate_reopen()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var directory = Directory.CreateTempSubdirectory("terrabase-construction-").FullName;
        var workStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Thread?[2];
        var created = 0;
        Task<Exception?>? startup = null;
        TerraBaseTestServer? unexpected = null;
        TerraBaseTestServer? reopened = null;
        Task? shutdown = null;
        Exception? primaryFailure = null;
        try
        {
            startup = Task.Run(async () =>
            {
                try
                {
                    unexpected = await TerraBaseTestServer.StartAsync(settings =>
                    {
                        settings["data-directory"] = directory;
                        settings["storage-threads"] = "2";
                    }, services => services.AddSingleton(provider =>
                    {
                        var options = provider.GetRequiredService<TerraBaseOptions>();
                        return new TursoStorageEngine(
                            options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                            provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                            provider.GetRequiredService<ILogger<TursoStorageEngine>>(), provider.GetRequiredKeyedService<IMasterKey>(EngineExtensions.DatabaseMasterKey), options.ShutdownGrace,
                            createWorker: CreateWorker);
                    }));
                    return (Exception?)null;
                }
                catch (Exception error)
                {
                    return error;
                }
            }, CancellationToken.None);

            await workStopped.Task.WaitAsync(deadline.Token);
            // Work's completion signal has fired, but the real thread is held at its exit.
            await Assert.That(workers[0]!.IsAlive).IsTrue();
            await Assert.That(startup.IsCompleted).IsFalse();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsTrue();
            release.Set();

            var failure = await startup.WaitAsync(deadline.Token);
            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.GetType()).IsEqualTo(typeof(ThreadStateException));
            await Assert.That(failure.Data.Contains("CleanupFailure")).IsFalse();
            await Assert.That(created).IsEqualTo(2);
            await Assert.That(workers[0]!.IsAlive).IsFalse();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsFalse();

            // No GC, retry delay or finalizer: a new real host takes the same external directory immediately.
            reopened = await TerraBaseTestServer.StartAsync(settings =>
            {
                settings["data-directory"] = directory;
                settings["storage-threads"] = "2";
            });
            using var health = await reopened.Client.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
            await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
            shutdown = reopened.DisposeAsync().AsTask();
            await shutdown.WaitAsync(deadline.Token);
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsFalse();
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            await CleanupAsync();
        }

        async Task CleanupAsync()
        {
            release.Set();
            deadline.Cancel();
            // Startup can publish a server after cancellation. Join it before inspecting the result.
            if (startup is not null)
            {
                await StoragePoolTests.JoinCleanupAsync([startup], primaryFailure);
            }
            var cleanup = new List<Task>();
            if (unexpected is not null)
            {
                cleanup.Add(unexpected.DisposeAsync().AsTask());
            }
            if (reopened is not null)
            {
                cleanup.Add(shutdown ?? reopened.DisposeAsync().AsTask());
            }
            try
            {
                await StoragePoolTests.JoinCleanupAsync(cleanup, primaryFailure);
            }
            catch (Exception error)
            {
                primaryFailure ??= error;
                throw;
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception error) when (primaryFailure is not null)
                {
                    primaryFailure.Data["CleanupFailure"] = primaryFailure.Data["CleanupFailure"] is Exception earlier
                        ? new AggregateException(earlier, error) : error;
                }
            }
        }

        Thread CreateWorker(ThreadStart run)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref created) - 1;
            var worker = new Thread(() =>
            {
                try
                {
                    // A broken rollback must still be able to cancel a worker waiting in Monitor.Wait.
                    using var interrupt = deadline.Token.Register(Thread.CurrentThread.Interrupt);
                    run();
                    if (index == 0)
                    {
                        workStopped.TrySetResult();
                    }

                    release.Wait(deadline.Token);
                }
                catch (Exception error) when (deadline.IsCancellationRequested && error is OperationCanceledException or ThreadInterruptedException)
                {
                }
            })
            { IsBackground = true };
            workers[index] = worker;
            if (index == 1)
            {
                // Start it here so the pool's own UnsafeStart throws a real ThreadStateException.
                worker.Start();
            }

            return worker;
        }
    }

    [Test]
    public async Task Failed_pool_construction_releases_directory_ownership_before_the_server_retries()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        ArgumentOutOfRangeException? failure = null;
        await using var management = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<TerraBaseOptions>();
                var meter = provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
                var logger = provider.GetRequiredService<ILogger<TursoStorageEngine>>();
                try
                {
                    // The pool rejects this queue after the engine takes the directory's actual file lock.
                    // If validation regresses, return the unexpected engine so DI still owns its cleanup.
                    return new TursoStorageEngine(options.DataDirectory, 1, 0, options.MemoryBudget, meter, logger, provider.GetRequiredKeyedService<IMasterKey>(EngineExtensions.DatabaseMasterKey), options.ShutdownGrace);
                }
                catch (ArgumentOutOfRangeException error)
                {
                    failure = error;
                }

                // Reacquire the same directory immediately, without collection/finalization of the failed engine.
                return new TursoStorageEngine(
                    options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                    meter, logger, provider.GetRequiredKeyedService<IMasterKey>(EngineExtensions.DatabaseMasterKey), options.ShutdownGrace);
            }));
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.ParamName).IsEqualTo("maxAdmitted");

        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        using var created = await tenant.Data.PutAsync(
            new Uri($"/v1/projects/{tenant.ProjectId}/databases/retried", UriKind.Relative), null, deadline.Token);
        created.EnsureSuccessStatusCode();
        await StorageFixture.CreateCollectionAsync(tenant, "retried", "items", deadline.Token);
        var document = new Uri(
            $"/v1/projects/{tenant.ProjectId}/databases/retried/collections/items/documents/by-key?id={StorageFixture.Id("a")}", UriKind.Relative);
        using var written = await tenant.Data.PutAsJsonAsync(document, new { n = 1 }, deadline.Token);
        written.EnsureSuccessStatusCode();
        using var read = await tenant.Data.GetAsync(document, deadline.Token);
        read.EnsureSuccessStatusCode();
    }

    private sealed class PoolConstructionService(Func<TursoIoPool> createPool) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = createPool();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
