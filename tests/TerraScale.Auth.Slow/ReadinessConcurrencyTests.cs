using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Slow;

public sealed class ReadinessConcurrencyTests
{
    [Test]
    public async Task Concurrent_readiness_requests_share_the_background_check()
    {
        var health = new FakeStorageHealth(healthy: true, delay: TimeSpan.FromMilliseconds(300));
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));
        await Assert.That(await ReadinessWait.ReadyWithinAsync(server.Client, TimeSpan.FromSeconds(10))).IsTrue();
        var before = health.Calls;

        var responses = await Task.WhenAll(Enumerable.Range(0, 200).Select(async _ =>
        {
            using var response = await server.Client.GetAsync("/readyz");
            return response.StatusCode;
        }));

        await Assert.That(responses.All(status => status == HttpStatusCode.OK)).IsTrue();
        // The requests read the latest result; at most one pass can land during the burst.
        await Assert.That(health.Calls - before).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task A_failed_check_is_retried_on_the_next_pass()
    {
        var health = new FakeStorageHealth(healthy: false);
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        await Task.Delay(ReadinessWait.NextPass);
        using var response = await server.Client.GetAsync("/readyz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(health.Calls).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task A_stuck_storage_check_is_never_stacked_with_new_ones()
    {
        var health = new FakeStorageHealth(healthy: true, delay: TimeSpan.FromSeconds(8), ignoreCancellation: true);
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        // The pass at five seconds finds the first check still running and starts no storage work.
        await Task.Delay(ReadinessWait.NextPass);
        using var response = await server.Client.GetAsync("/readyz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(health.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_check_that_blocks_before_returning_is_still_bounded()
    {
        var health = new FakeStorageHealth(healthy: true, delay: TimeSpan.FromSeconds(8), blockBeforeReturning: true);
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        // The two-second deadline records not ready even though the check blocks its worker.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        using var response = await server.Client.GetAsync("/readyz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task Readiness_recovers_once_a_late_check_finishes()
    {
        var health = new LateThenHealthyStorage();
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        // The first pass times out while its check ignores cancellation.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        health.CompleteFirstCheck(healthy: true);
        var recovered = await ReadinessWait.ReadyWithinAsync(server.Client, TimeSpan.FromSeconds(10));

        // The late success is never recorded; the next pass runs a fresh check.
        await Assert.That(recovered).IsTrue();
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
}
