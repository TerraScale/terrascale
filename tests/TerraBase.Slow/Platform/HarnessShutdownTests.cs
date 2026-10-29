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
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

public sealed class HarnessShutdownTests
{
    [Test]
    public async Task The_entry_point_stops_hosted_services_once_before_disposing_them()
    {
        var probe = new ShutdownProbe();
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<IHostedService>(_ => probe));
        using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await server.DisposeAsync();

        await Assert.That(probe.Stops).IsEqualTo(1);
        await Assert.That(probe.StopsAtDisposal).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Disposal_joins_native_cleanup_before_deleting_the_directory_after_grace_expires(bool replaceEngine)
    {
        var server = await TerraBaseTestServer.StartAsync(settings => settings["shutdown-grace-seconds"] = "0", services =>
        {
            if (replaceEngine)
            {
                services.AddSingleton(provider =>
                {
                    var options = provider.GetRequiredService<TerraBaseOptions>();
                    return new TursoStorageEngine(options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                        provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                        provider.GetRequiredService<ILogger<TursoStorageEngine>>(), provider.GetRequiredKeyedService<IMasterKey>(EngineExtensions.DatabaseMasterKey), options.ShutdownGrace, readinessCacheTtl: options.ReadinessCacheTtl);
                });
            }
        });
        var directory = server.DataDirectory;
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var lifetime = server.Services.GetRequiredService<IHostApplicationLifetime>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        var token = TestContext.Current!.Execution.CancellationToken;
        await engine.CreateDatabaseAsync(database, token);
        var store = (await engine.OpenDatabaseAsync(database, token))!;
        await using var transaction = await store.BeginTransactionAsync(write: true, token);
        Task? shutdown = null;
        try
        {
            shutdown = server.DisposeAsync().AsTask();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(lifetime.ApplicationStopped.IsCancellationRequested));
            await Assert.That(shutdown.IsCompleted).IsFalse();
            await Assert.That(Directory.Exists(directory)).IsTrue();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsTrue();
        }
        finally
        {
            await transaction.DisposeAsync();
            await (shutdown ?? server.DisposeAsync().AsTask()).WaitAsync(token);
        }

        await Assert.That(engine.CleanupCompletion.IsCompletedSuccessfully).IsTrue();
        await Assert.That(Directory.Exists(directory)).IsFalse();
    }

    [Test]
    public async Task Background_tasks_join_before_DI_disposal_even_when_the_host_grace_expires()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var worker = new HeldBackgroundTask(token);
        var server = await TerraBaseTestServer.StartAsync(services: services =>
        {
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.Zero);
            services.AddSingleton<IHostedService>(_ => worker);
        });
        Task? shutdown = null;
        try
        {
            await worker.Started.Task.WaitAsync(token);
            shutdown = server.DisposeAsync().AsTask();
            await worker.Stopping.Task.WaitAsync(token);
            await Assert.That(worker.Disposed).IsFalse();
            await Assert.That(shutdown.IsCompleted).IsFalse();
            await Assert.That(Directory.Exists(server.DataDirectory)).IsTrue();
        }
        finally
        {
            worker.Release.TrySetResult();
            await (shutdown ?? server.DisposeAsync().AsTask()).WaitAsync(token);
        }

        await Assert.That(worker.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
        await Assert.That(worker.Disposed).IsTrue();
    }

    [Test]
    public async Task Canceling_the_execution_wait_retains_dependencies_until_background_cleanup_finishes()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        var dependency = new WorkerDependency();
        var worker = new HeldBackgroundTask(token, dependency);
        var server = await TerraBaseTestServer.StartAsync(services: services =>
        {
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.Zero);
            services.AddSingleton(_ => dependency);
            services.AddSingleton<IHostedService>(provider =>
            {
                _ = provider.GetRequiredService<WorkerDependency>();
                return worker;
            });
        }, cancellationToken: execution.Token);
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        try
        {
            using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await worker.Started.Task.WaitAsync(token);
            await execution.CancelAsync();
            await worker.Stopping.Task.WaitAsync(token);
            await Assert.That(async () => await server.DisposeAsync()).Throws<OperationCanceledException>();
            await Assert.That(worker.Disposed).IsFalse();
            await Assert.That(dependency.Disposed).IsFalse();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsTrue();
        }
        finally
        {
            worker.Release.TrySetResult();
            // Join the retained owner with this regression's own, still-live timeout.
            await server.CleanupCompletion.WaitAsync(token);
        }

        await Assert.That(worker.DependencyDisposedAtCompletion).IsFalse();
        await Assert.That(dependency.Disposed).IsTrue();
        await Assert.That(engine.CleanupCompletion.IsCompletedSuccessfully).IsTrue();
        await Assert.That(Directory.Exists(server.DataDirectory)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Failure_diagnostics_delete_fixture_directories_only_after_actual_host_and_native_cleanup(bool bodyFails)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        var directory = Directory.CreateTempSubdirectory("terrabase-diagnostic-cleanup-").FullName;
        var worker = new HeldBackgroundTask(token);
        var otherProbe = new ShutdownProbe();
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyFailure = bodyFails ? new IOException("original test body failed") : null;
        var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            settings["data-directory"] = Path.Combine(directory, "first");
            settings["shutdown-grace-seconds"] = "0";
        }, services => services.AddSingleton<IHostedService>(_ => worker), cancellationToken: execution.Token);
        TerraBaseTestServer? other = null;
        IStorageTransaction? transaction = null;
        try
        {
            other = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = Path.Combine(directory, "second"),
                services => services.AddSingleton<IHostedService>(_ => otherProbe));
            using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var engine = server.Services.GetRequiredService<TursoStorageEngine>();
            var lifetime = server.Services.GetRequiredService<IHostApplicationLifetime>();
            var store = await engine.OpenSystemDatabaseAsync(token);
            transaction = await store.BeginTransactionAsync(write: false, token);
            await execution.CancelAsync();
            await worker.Stopping.Task.WaitAsync(token);

            var failure = await Assert.That(async () => await SuiteDiagnostics.CleanupAfterBodyAsync(bodyFailure, () =>
            {
                Directory.Delete(directory, recursive: true);
                deleted.TrySetResult();
            }, (server, server.CleanupCompletion), (other, other.CleanupCompletion)).WaitAsync(token)).ThrowsException();
            await Assert.That(bodyFails ? ReferenceEquals(failure, bodyFailure) : failure is OperationCanceledException).IsTrue();
            await Assert.That(otherProbe.Stops).IsEqualTo(1);
            await Assert.That(deleted.Task.IsCompleted).IsFalse();
            await Assert.That(Directory.Exists(directory)).IsTrue();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsTrue();

            worker.Release.TrySetResult();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(lifetime.ApplicationStopped.IsCancellationRequested));
            await Assert.That(engine.CleanupCompletion.IsCompleted).IsFalse();
            await Assert.That(deleted.Task.IsCompleted).IsFalse();
            await Assert.That(Directory.Exists(directory)).IsTrue();
            await transaction.DisposeAsync();
            transaction = null;
            await server.CleanupCompletion.WaitAsync(token);
            await deleted.Task.WaitAsync(token);
            await Assert.That(Directory.Exists(directory)).IsFalse();
        }
        finally
        {
            worker.Release.TrySetResult();
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            await Task.WhenAll(server.CleanupCompletion, other?.CleanupCompletion ?? Task.CompletedTask).WaitAsync(token);
            server.Client.Dispose();
            other?.Client.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Canceled_management_startup_retains_assets_and_fixture_directory_until_actual_cleanup(bool observeCleanup)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        var directory = Directory.CreateTempSubdirectory("terrabase-management-startup-").FullName;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TursoStorageEngine? engine = null;
        Task? completion = null;
        Task? cleanup = null;
        var startup = ManagementApi.StartAsync(settings => settings["data-directory"] = directory, services => services.AddSingleton<IHostedService>(provider =>
        {
            engine = provider.GetRequiredService<TursoStorageEngine>();
            completion = provider.GetRequiredService<ApplicationRunCompletion>().Completion.Task;
            return new HeldStarting(entered, release, token);
        }), cancellationToken: execution.Token,
            failedStartCleanup: observeCleanup ? task => cleanup = task : null);
        try
        {
            await entered.Task.WaitAsync(token);
            await execution.CancelAsync();
            var startupError = await Assert.That(async () => await startup.WaitAsync(token)).ThrowsException();
            await Assert.That(completion!.IsCompleted).IsFalse();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsTrue();
            if (observeCleanup)
            {
                await Assert.That(cleanup is not null).IsTrue();
                var error = await Assert.That(async () => await SuiteDiagnostics.CleanupAfterBodyAsync(startupError, () =>
                {
                    Directory.Delete(directory, recursive: true);
                    deleted.TrySetResult();
                }, (null, cleanup!)).WaitAsync(token)).ThrowsException();
                await Assert.That(ReferenceEquals(error, startupError)).IsTrue();
                await Assert.That(deleted.Task.IsCompleted).IsFalse();
                await Assert.That(Directory.Exists(directory)).IsTrue();
            }

            release.TrySetResult();
            await Assert.That(async () => await completion.WaitAsync(token)).ThrowsException();
            await engine!.CleanupCompletion.WaitAsync(token);
            if (observeCleanup)
            {
                await cleanup!.WaitAsync(token);
                await deleted.Task.WaitAsync(token);
                await Assert.That(Directory.Exists(directory)).IsFalse();
            }
        }
        finally
        {
            await execution.CancelAsync();
            release.TrySetResult();
            if (completion is not null)
            {
                try
                {
                    await completion.WaitAsync(token);
                }
                catch (Exception) when (completion.IsFaulted || completion.IsCanceled)
                {
                    // Canceled startup's run failure was already returned to its caller.
                }
            }

            if (engine is not null)
            {
                await engine.CleanupCompletion.WaitAsync(token);
            }

            if (cleanup is not null)
            {
                await cleanup.WaitAsync(token);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public async Task Canceling_startup_retains_cleanup_ownership_for_the_host_still_being_started()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TursoStorageEngine? engine = null;
        Task? completion = null;
        Task? cleanup = null;
        string? directory = null;
        var startup = TerraBaseTestServer.StartAsync(settings => directory = settings["data-directory"], services =>
            services.AddSingleton<IHostedService>(provider =>
            {
                engine = provider.GetRequiredService<TursoStorageEngine>();
                completion = provider.GetRequiredService<ApplicationRunCompletion>().Completion.Task;
                return new HeldStarting(entered, release, token);
            }), cancellationToken: execution.Token, failedStartCleanup: task => cleanup = task);
        try
        {
            await entered.Task.WaitAsync(token);
            await execution.CancelAsync();
            var error = await Assert.That(async () => await startup.WaitAsync(token)).ThrowsException();
            await Assert.That(error is OperationCanceledException
                || error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.All(cause => cause is OperationCanceledException)).IsTrue();
            await Assert.That(completion!.IsCompleted).IsFalse();
            await Assert.That(cleanup is not null).IsTrue();
            await Assert.That(cleanup!.IsCompleted).IsFalse();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory!)).IsTrue();
        }
        finally
        {
            release.TrySetResult();
            await Assert.That(async () => await completion!.WaitAsync(token)).ThrowsException();
            await engine!.CleanupCompletion.WaitAsync(token);
            await cleanup!.WaitAsync(token);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(!Directory.Exists(directory)));
        }
    }

    [Test]
    public async Task Disposal_preserves_a_hosted_StopAsync_failure()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var stopError = new InvalidOperationException("hosted stop failed");
        var probe = new ShutdownProbe(stopError);
        var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<IHostedService>(_ => probe));
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        try
        {
            using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            var error = await Assert.That(async () => await server.DisposeAsync().AsTask().WaitAsync(token)).ThrowsException();
            await Assert.That(engine.CleanupCompletion.IsCompletedSuccessfully).IsTrue();
            // Cleanup faults retain the directory; the test owns it after native cleanup completes.
            Directory.Delete(server.DataDirectory, recursive: true);
            await Assert.That(ContainsException(error!, stopError)).IsTrue();
            await Assert.That(probe.Stops).IsEqualTo(1);
            await Assert.That(probe.StopsAtDisposal).IsEqualTo(1);
        }
    }

    [Test]
    public async Task A_late_hosted_StartAsync_failure_stops_and_joins_workers_before_disposing_dependencies()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var startError = new InvalidOperationException("late hosted start failed");
        var dependency = new WorkerDependency();
        var worker = new HeldBackgroundTask(token, dependency);
        string? directory = null;
        var startup = TerraBaseTestServer.StartAsync(settings => directory = settings["data-directory"], services =>
        {
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.Zero);
            services.AddSingleton(_ => dependency);
            services.AddSingleton<IHostedService>(provider =>
            {
                // Resolve through DI so it owns the dependency's disposal.
                _ = provider.GetRequiredService<WorkerDependency>();
                return worker;
            });
            services.AddSingleton<IHostedService>(_ => new FailingStartup(worker, startError, token));
        });
        Exception? error = null;
        try
        {
            await worker.Started.Task.WaitAsync(token);
            await worker.Stopping.Task.WaitAsync(token);
            await Assert.That(startup.IsCompleted).IsFalse();
            await Assert.That(worker.Disposed).IsFalse();
            await Assert.That(dependency.Disposed).IsFalse();
        }
        finally
        {
            worker.Release.TrySetResult();
            error = await Assert.That(async () => await startup.WaitAsync(token)).ThrowsException();
            await Assert.That(Directory.Exists(directory)).IsFalse();
        }

        await Assert.That(ContainsException(error!, startError)).IsTrue();
        await Assert.That(worker.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
        await Assert.That(worker.DependencyDisposedAtCompletion).IsFalse();
        await Assert.That(worker.Disposed).IsTrue();
        await Assert.That(dependency.Disposed).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_early_hosted_StartingAsync_failure_joins_native_cleanup_before_releasing_the_directory(bool throwCleanupCallback)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var startError = new InvalidOperationException("early hosted starting failed");
        var callbackError = new InvalidOperationException("cleanup callback failed");
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<IStorageTransaction>(TaskCreationOptions.RunContinuationsAsynchronously);
        TursoStorageEngine? engine = null;
        Task? runCompletion = null;
        EarlyFailingStartup? probe = null;
        string? directory = null;
        var startup = TerraBaseTestServer.StartAsync(settings =>
        {
            directory = settings["data-directory"];
            settings["shutdown-grace-seconds"] = "0";
        }, services => services.AddSingleton<IHostedService>(provider =>
        {
            engine = provider.GetRequiredService<TursoStorageEngine>();
            runCompletion = provider.GetRequiredService<ApplicationRunCompletion>().Completion.Task;
            return probe = new EarlyFailingStartup(engine, entered, startError);
        }), failedStartCleanup: _ =>
        {
            callbackEntered.TrySetResult();
            if (throwCleanupCallback)
            {
                throw callbackError;
            }
        });
        Exception? error = null;
        try
        {
            await entered.Task.WaitAsync(token);
            // Program has finished disposal; the harness must still join native cleanup with the session held.
            await Assert.That(async () => await runCompletion!.WaitAsync(token)).ThrowsException();
            await callbackEntered.Task.WaitAsync(token);
            await Assert.That(probe!.Started).IsFalse();
            await Assert.That(engine!.CleanupCompletion.IsCompleted).IsFalse();
            await Assert.That(startup.IsCompleted).IsFalse();
            await Assert.That(Directory.Exists(directory)).IsTrue();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory!)).IsTrue();
        }
        finally
        {
            if (entered.Task.IsCompletedSuccessfully)
            {
                await (await entered.Task).DisposeAsync();
            }

            error = await Assert.That(async () => await startup.WaitAsync(token)).ThrowsException();
            await Assert.That(engine!.CleanupCompletion.IsCompletedSuccessfully).IsTrue();
            if (Directory.Exists(directory))
            {
                // Failed-start cleanup may retain the directory; only delete after native ownership is released.
                await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory!)).IsFalse();
                Directory.Delete(directory!, recursive: true);
            }
        }

        await Assert.That(ContainsException(error!, startError)).IsTrue();
        await Assert.That(ContainsException(error!, callbackError)).IsEqualTo(throwCleanupCallback);
    }

    [Test]
    public async Task Disposal_preserves_both_hosted_StopAsync_and_DI_disposal_failures()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var stopError = new InvalidOperationException("hosted stop failed");
        var disposalError = new IOException("DI disposal failed");
        var probe = new ShutdownProbe(stopError, disposalError);
        var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<IHostedService>(_ => probe));
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        try
        {
            using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            var error = await Assert.That(async () => await server.DisposeAsync().AsTask().WaitAsync(token)).ThrowsException();
            await Assert.That(engine.CleanupCompletion.IsCompletedSuccessfully).IsTrue();
            Directory.Delete(server.DataDirectory, recursive: true);
            await Assert.That(ContainsException(error!, stopError)).IsTrue();
            await Assert.That(ContainsException(error!, disposalError)).IsTrue();
            await Assert.That(probe.Stops).IsEqualTo(1);
            await Assert.That(probe.StopsAtDisposal).IsEqualTo(1);
        }
    }

    private static bool ContainsException(Exception error, Exception expected) =>
        ReferenceEquals(error, expected)
        || (error is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(cause => ContainsException(cause, expected))
            : error.InnerException is { } inner && ContainsException(inner, expected));

    private sealed class FailingStartup(HeldBackgroundTask worker, Exception error, CancellationToken testCancellation) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await worker.Started.Task.WaitAsync(testCancellation);
            throw error;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HeldStarting(TaskCompletionSource entered, TaskCompletionSource release, CancellationToken testCancellation) : IHostedLifecycleService
    {
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(testCancellation);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EarlyFailingStartup(
        TursoStorageEngine engine, TaskCompletionSource<IStorageTransaction> entered, Exception error) : IHostedLifecycleService
    {
        public bool Started { get; private set; }

        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            var store = await engine.OpenSystemDatabaseAsync(cancellationToken);
            var transaction = await store.BeginTransactionAsync(write: false, cancellationToken);
            // Transfer ownership to the test so the failure cannot release the native session.
            entered.SetResult(transaction);
            throw error;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class WorkerDependency : IDisposable
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class HeldBackgroundTask(CancellationToken testCancellation, WorkerDependency? dependency = null) : BackgroundService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public bool DependencyDisposedAtCompletion { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Stopping.TrySetResult();
                await Release.Task.WaitAsync(testCancellation);
                DependencyDisposedAtCompletion = dependency?.Disposed ?? false;
            }
        }

        public override void Dispose()
        {
            Disposed = true;
            base.Dispose();
        }
    }

    private sealed class ShutdownProbe(Exception? stopError = null, Exception? disposalError = null) : IHostedService, IDisposable
    {
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);

        public int StopsAtDisposal { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stops);
            await Task.Yield();
            if (stopError is not null)
            {
                throw stopError;
            }
        }

        public void Dispose()
        {
            StopsAtDisposal = Stops;
            if (disposalError is not null)
            {
                throw disposalError;
            }
        }
    }
}
