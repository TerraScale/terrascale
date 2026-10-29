using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Platform;

public sealed class ClosureWorkerShutdownTests
{
    [Test]
    public async Task A_storage_failure_after_shutdown_does_not_fault_the_closure_worker()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var storage = new FailingStorage();
        await using var server = await AuthTestServer.StartAsync(runClosureWorker: true, services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(new HttpClient(storage));
            // Exercise the real closure worker and host lifecycle with controlled storage failures.
            services.RemoveAll<IHostedService>();
            services.AddHostedService(services => services.GetRequiredService<ClosureWorker>());
        });
        var worker = server.App.Services.GetRequiredService<ClosureWorker>();
        await storage.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromSeconds(2));
        await storage.InFlight.Task.WaitAsync(TimeSpan.FromSeconds(15));
        // The running worker retried the first failure; the second fails only after its stop token is cancelled.
        await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse();
        await server.App.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(storage.FailedDuringStop.Task.IsCompletedSuccessfully).IsTrue();
        await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue();
    }

    private sealed class FailingStorage : HttpMessageHandler
    {
        private int calls;

        public TaskCompletionSource FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource InFlight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FailedDuringStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref this.calls) == 1)
            {
                this.FirstFailure.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            this.InFlight.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                this.FailedDuringStop.TrySetResult();
                // Storage transport may surface an unavailable response rather than cancellation.
                throw ApiException.Unavailable();
            }

            throw new InvalidOperationException("The held storage request must end through cancellation.");
        }
    }
}
