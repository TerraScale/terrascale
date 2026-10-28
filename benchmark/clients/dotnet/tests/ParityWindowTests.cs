using ApiBench;
using TUnit.Core;

namespace BenchClient.Tests;

public sealed class ParityWindowTests
{
    private static ParityWindowPlan Plan => new(1, TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    [Test]
    public async Task ReadsStartedInsideTheWindowKeepTheirFullLatencyAndDrainTime()
    {
        var clock = new ManualTime();
        var entered = Signal();
        var release = Signal();
        var run = ParityWindow.RunAsync(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); }, Plan, clock, default);
        await Wait(entered.Task);
        clock.Advance(TimeSpan.FromSeconds(6));
        release.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result.Rejection).IsNull();
        await Assert.That(result.Measured).IsEqualTo(1);
        await Assert.That(result.Latency!.P99Us).IsEqualTo(6_000_000);
        await Assert.That(result.DurationS).IsEqualTo(6);
        await Assert.That(result.Rps).IsEqualTo(1.0 / 6);
    }

    [Test]
    public async Task WarmupCrossingTheBoundaryIsExcludedButStillCountsAsAnAttempt()
    {
        var clock = new ManualTime();
        var entered = new[] { Signal(), Signal() };
        var release = new[] { Signal(), Signal() };
        var calls = 0;
        var run = ParityWindow.RunAsync(async ct =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            entered[index].SetResult();
            await release[index].Task.WaitAsync(ct);
        }, Plan with { Warmup = TimeSpan.FromSeconds(2) }, clock, default);
        await Wait(entered[0].Task);
        clock.Advance(TimeSpan.FromSeconds(3));
        release[0].SetResult();
        await Wait(entered[1].Task);
        clock.Advance(TimeSpan.FromSeconds(5));
        release[1].SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result.Starts).IsEqualTo(2);
        await Assert.That(result.Measured).IsEqualTo(1);
        await Assert.That(result.Ok).IsEqualTo(1);
        await Assert.That(result.Latency!.P50Us).IsEqualTo(5_000_000);
        await Assert.That(result.DurationS).IsEqualTo(6);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DrainTimeoutAndInterruptCancelThenJoinEveryRead(bool interrupt)
    {
        var clock = new ManualTime();
        using var cancel = new CancellationTokenSource();
        var entered = Signal();
        var active = 0;
        var run = ParityWindow.RunAsync(async ct =>
        {
            Interlocked.Increment(ref active);
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Interlocked.Decrement(ref active); }
        }, Plan, clock, cancel.Token);
        await Wait(entered.Task);
        if (interrupt) cancel.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(36));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result.Rejection).IsEqualTo(interrupt ? "interrupted" : "drain_timeout");
        await Assert.That(result.Unfinished).IsEqualTo(1);
        await Assert.That(result.Latency).IsNull();
        await Assert.That(result.Rps).IsNull();
        await Assert.That(active).IsEqualTo(0);
    }

    [Test]
    public async Task OneFailureStopsAllWorkersAndJoinsTheirCancellation()
    {
        var clock = new ManualTime();
        var entered = Signal();
        var release = Signal();
        var active = 0;
        var calls = 0;
        var run = ParityWindow.RunAsync(async ct =>
        {
            Interlocked.Increment(ref active);
            if (Interlocked.Increment(ref calls) == 16) entered.SetResult();
            try { await release.Task.WaitAsync(ct); throw new InvalidDataException("synthetic"); }
            finally { Interlocked.Decrement(ref active); }
        }, Plan with { Concurrency = 16 }, clock, default);
        await Wait(entered.Task);
        release.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result.Rejection).IsEqualTo("operation");
        await Assert.That(result.Starts).IsEqualTo(16);
        await Assert.That(result.Ok).IsEqualTo(0);
        await Assert.That(result.Errors + result.Unfinished).IsEqualTo(16);
        await Assert.That(active).IsEqualTo(0);
    }

    [Test]
    public async Task ReservoirIsBoundedWithoutDroppingMeasuredOperations()
    {
        var clock = new ManualTime();
        var result = await ParityWindow.RunAsync(_ =>
        {
            clock.Advance(TimeSpan.FromTicks(10));
            return Task.CompletedTask;
        }, Plan with { Measure = TimeSpan.FromTicks(200_000) }, clock, default).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result.Rejection).IsNull();
        await Assert.That(result.Ok).IsEqualTo(20_000);
        await Assert.That(result.Latency!.Samples).IsEqualTo(16_384);
        await Assert.That(result.Latency.MaxUs).IsEqualTo(1);
        await Assert.That(result.Latency.P50Us).IsEqualTo(1);
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Alarm> alarms = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var alarm = new Alarm(this, callback, state);
            lock (gate) alarms.Add(alarm);
            alarm.Change(dueTime, period);
            return alarm;
        }

        internal void Advance(TimeSpan elapsed)
        {
            Alarm[] due;
            lock (gate)
            {
                ticks += elapsed.Ticks;
                due = [.. alarms.Where(alarm => alarm.Due <= ticks)];
                foreach (var alarm in due) alarm.Due = long.MaxValue;
            }
            foreach (var alarm in due) alarm.Fire();
        }

        private sealed class Alarm(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due { get; set; } = long.MaxValue;
            internal void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Only one-shot delays are needed by these controls.");
                lock (owner.gate) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                return true;
            }
            public void Dispose() { lock (owner.gate) owner.alarms.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
