using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class CatalogShutdownTests
{
    [Test]
    public async Task Follower_disposal_joins_caught_up_observers_before_disposing_dependencies_after_host_grace_expires()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var peers = new PeerTls();
        await using var primary = await TerraBaseTestServer.StartAsync(peers.Apply);
        var checkpoint = new ShutdownCheckpoint(token);
        var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = primary.PeerAddress!.ToString();
        }, services =>
        {
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.Zero);
            services.AddSingleton(_ => new ObserverDependency());
            services.AddSingleton(provider => new HeldCatalogObserver(provider.GetRequiredService<ObserverDependency>(), token));
            services.AddSingleton<ICatalogApplyObserver>(provider => provider.GetRequiredService<HeldCatalogObserver>());
            services.AddSingleton<IHostedService>(_ => checkpoint);
        });
        var observer = follower.Services.GetRequiredService<HeldCatalogObserver>();
        var dependency = follower.Services.GetRequiredService<ObserverDependency>();
        Task? disposal = null;
        try
        {
            await observer.Started.Task.WaitAsync(token);
            using var ready = await follower.Client.GetAsync(new Uri("/readyz", UriKind.Relative), token);
            await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);

            disposal = follower.DisposeAsync().AsTask();
            await observer.Stopping.Task.WaitAsync(token);
            await checkpoint.Stopped.Task.WaitAsync(token);
            await Assert.That(disposal.IsCompleted).IsFalse();
            await Assert.That(dependency.Disposed).IsFalse();
            await Assert.That(Directory.Exists(follower.DataDirectory)).IsTrue();
        }
        finally
        {
            observer.Release.TrySetResult();
            await (disposal ?? follower.DisposeAsync().AsTask()).WaitAsync(token);
        }

        await Assert.That(observer.DependencyDisposedAtCompletion).IsFalse();
        await Assert.That(dependency.Disposed).IsTrue();
        await Assert.That(Directory.Exists(follower.DataDirectory)).IsFalse();
    }

    private sealed class HeldCatalogObserver(ObserverDependency dependency, CancellationToken testCancellation) : ICatalogApplyObserver
    {
        public string Collection => "shutdown_test";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DependencyDisposedAtCompletion { get; private set; }

        public ValueTask AppliedAsync(IReadOnlyList<DocumentChange> documents, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask CaughtUpAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            using var stopping = cancellationToken.Register(() => Stopping.TrySetResult());
            await Stopping.Task.WaitAsync(testCancellation);
            await Release.Task.WaitAsync(testCancellation);
            DependencyDisposedAtCompletion = dependency.Disposed;
        }
    }

    private sealed class ObserverDependency : IDisposable
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class ShutdownCheckpoint(CancellationToken testCancellation) : IHostedLifecycleService
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StoppingAsync(CancellationToken cancellationToken)
        {
            // Expire the real host grace before any StopAsync; no timer-based assertion or longer timeout.
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = cancellationToken.Register(() => expired.TrySetResult());
            await expired.Task.WaitAsync(testCancellation);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken)
        {
            // All StopAsync calls finished; Program joins ExecuteTask before disposing DI next.
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
