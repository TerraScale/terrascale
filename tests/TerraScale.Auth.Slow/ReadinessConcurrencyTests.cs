using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Slow;

public sealed class ReadinessConcurrencyTests
{
    [Test]
    public async Task Concurrent_readiness_probes_share_one_storage_round_trip()
    {
        var health = new FakeStorageHealth(healthy: true, delay: TimeSpan.FromMilliseconds(300));
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        var responses = await Task.WhenAll(Enumerable.Range(0, 200).Select(async _ =>
        {
            using var response = await server.Client.GetAsync("/readyz");
            return response.StatusCode;
        }));

        await Assert.That(responses.All(status => status == HttpStatusCode.OK)).IsTrue();
        // The first probe answers everyone in flight; later requests hit the one-second cache.
        await Assert.That(health.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_probe_is_retried_after_the_cache_window()
    {
        var health = new FakeStorageHealth(healthy: false);
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        using var first = await server.Client.GetAsync("/readyz");
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        using var second = await server.Client.GetAsync("/readyz");

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(health.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task A_stuck_storage_check_is_never_stacked_with_new_ones()
    {
        var health = new FakeStorageHealth(healthy: true, delay: TimeSpan.FromSeconds(8), ignoreCancellation: true);
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        using var timedOut = await server.Client.GetAsync("/readyz");
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var started = DateTimeOffset.UtcNow;
        using var whileStuck = await server.Client.GetAsync("/readyz");

        await Assert.That(timedOut.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(whileStuck.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(DateTimeOffset.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(1));
        await Assert.That(health.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_check_that_blocks_before_returning_is_still_bounded()
    {
        var health = new FakeStorageHealth(healthy: true, delay: TimeSpan.FromSeconds(8), blockBeforeReturning: true);
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        var started = DateTimeOffset.UtcNow;
        using var response = await server.Client.GetAsync("/readyz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(DateTimeOffset.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Readiness_recovers_once_a_late_check_finishes()
    {
        var health = new LateThenHealthyStorage();
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        using var timedOut = await server.Client.GetAsync("/readyz");
        health.CompleteFirstCheck(healthy: true);
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        using var recovered = await server.Client.GetAsync("/readyz");

        // The late success is never cached; a fresh check decides readiness.
        await Assert.That(timedOut.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(health.Calls).IsEqualTo(2);
    }

    /// <summary>The first check ignores cancellation until released; later checks answer at once.</summary>
    private sealed class LateThenHealthyStorage : IStorageHealth
    {
        private readonly TaskCompletionSource<StorageHealthResult> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        public void CompleteFirstCheck(bool healthy) => this.first.SetResult(new StorageHealthResult(healthy, "gru"));

        public Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken) =>
            Interlocked.Increment(ref this.calls) == 1 ? this.first.Task : Task.FromResult(new StorageHealthResult(true, "gru"));
    }

    [Test]
    public async Task A_disconnected_waiter_never_cancels_the_shared_probe()
    {
        var health = new GatedStorage();
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        using var cancelled = new CancellationTokenSource();
        var leaving = server.Client.GetAsync("/readyz", cancelled.Token);
        var staying = server.NewClient().GetAsync("/readyz");
        await health.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.CancelAsync();
        health.Release();

        await Assert.ThrowsAsync<TaskCanceledException>(() => leaving);
        using var response = await staying;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(health.Calls).IsEqualTo(1);
    }

    /// <summary>A healthy check that waits until the test releases it.</summary>
    private sealed class GatedStorage : IStorageHealth
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref this.calls);

        public void Release() => this.gate.SetResult();

        public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.calls);
            this.Started.TrySetResult();
            await this.gate.Task;
            return new StorageHealthResult(true, "gru");
        }
    }
}
