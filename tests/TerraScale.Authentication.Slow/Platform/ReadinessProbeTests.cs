using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Authentication.Features.Observability;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Platform;

public sealed class ReadinessProbeTests
{
    [Test]
    public async Task Latest_is_not_ready_before_the_first_evaluation()
    {
        var health = new FakeStorageHealth(healthy: true);
        var probe = new ReadinessProbe(health, TimeProvider.System, NullLogger<ReadinessProbe>.Instance);

        await Assert.That(probe.Latest().Ready).IsFalse();
        await Assert.That(health.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Latest_serves_the_last_evaluation_without_running_checks()
    {
        var health = new FakeStorageHealth(healthy: true, region: "gru");
        var probe = new ReadinessProbe(health, TimeProvider.System, NullLogger<ReadinessProbe>.Instance);
        await probe.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => probe.Latest().Ready);
            var calls = health.Calls;

            var observations = Enumerable.Range(0, 100).Select(_ => probe.Latest()).ToArray();

            await Assert.That(observations.All(observation => observation.Ready && observation.Region == "gru")).IsTrue();
            await Assert.That(health.Calls).IsEqualTo(calls);
        }
        finally
        {
            await probe.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task A_latest_evaluation_older_than_fifteen_seconds_answers_not_ready()
    {
        var clock = new ManualClock();
        var probe = new ReadinessProbe(new FakeStorageHealth(healthy: true), clock, NullLogger<ReadinessProbe>.Instance);
        await probe.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => probe.Latest().Ready);
        }
        finally
        {
            // Stops the loop so only the clock decides what is stale.
            await probe.StopAsync(CancellationToken.None);
        }

        clock.Advance(TimeSpan.FromSeconds(14));
        await Assert.That(probe.Latest().Ready).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(probe.Latest().Ready).IsFalse();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>A clock the test moves by hand. Timers still fire in real time.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => Stopwatch.Frequency;

        public override long GetTimestamp() => Volatile.Read(ref this.timestamp);

        public void Advance(TimeSpan by) => Interlocked.Add(ref this.timestamp, (long)(by.TotalSeconds * Stopwatch.Frequency));
    }
}
