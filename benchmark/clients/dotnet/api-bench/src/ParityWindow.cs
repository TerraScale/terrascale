namespace ApiBench;

internal sealed record ParityWindowPlan(int Concurrency, TimeSpan Warmup, TimeSpan Measure, TimeSpan Drain)
{
    internal static ParityWindowPlan Live(int concurrency) => new(concurrency, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
}

internal sealed record ParityWindowResult(long Starts, long Measured, long Ok, long Errors, long Unfinished,
    double DurationS, double? Rps, ParityWindowLatency? Latency, string? Rejection);
internal sealed record ParityWindowLatency(double P50Us, double P99Us, double MaxUs, int Samples);

internal static class ParityWindow
{
    private const int SampleLimit = 16_384;

    // Operations must honor cancellation. After a drain timeout we cancel, then join every worker before returning.
    internal static async Task<ParityWindowResult> RunAsync(Func<CancellationToken, Task> operation,
        ParityWindowPlan plan, TimeProvider clock, CancellationToken interrupt)
    {
        if (plan.Concurrency <= 0 || plan.Warmup < TimeSpan.Zero || plan.Measure <= TimeSpan.Zero || plan.Drain <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(plan));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(interrupt);
        var begin = clock.GetTimestamp();
        var first = begin + (long)(plan.Warmup.TotalSeconds * clock.TimestampFrequency);
        var end = first + (long)(plan.Measure.TotalSeconds * clock.TimestampFrequency);
        var deadline = Task.Delay(plan.Warmup + plan.Measure + plan.Drain, clock, stop.Token);
        var gate = new object();
        long starts = 0, measured = 0, ok = 0, errors = 0, unfinished = 0, last = 0;
        var samples = new List<double>(SampleLimit);
        double maximum = 0;
        string? rejection = null;

        void Reject(string reason)
        {
            lock (gate) rejection ??= reason;
            stop.Cancel();
        }

        async Task WorkerAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                var started = clock.GetTimestamp();
                if (started >= end) return;
                var inside = started >= first;
                lock (gate)
                {
                    starts++;
                    if (inside) measured++;
                }
                var success = false;
                var canceled = false;
                try
                {
                    await operation(stop.Token);
                    success = true;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    canceled = true;
                }
                catch (Exception)
                {
                    Reject("operation");
                }
                var finished = clock.GetTimestamp();
                if (!inside) continue;
                lock (gate)
                {
                    if (canceled) { unfinished++; continue; }
                    last = Math.Max(last, finished);
                    if (!success) { errors++; continue; }
                    ok++;
                    var us = clock.GetElapsedTime(started, finished).TotalMicroseconds;
                    maximum = Math.Max(maximum, us);
                    if (samples.Count < SampleLimit) samples.Add(us);
                    else
                    {
                        var index = Random.Shared.NextInt64(ok);
                        if (index < SampleLimit) samples[(int)index] = us;
                    }
                }
            }
        }

        var workers = Task.WhenAll(Enumerable.Range(0, plan.Concurrency).Select(_ => Task.Run(WorkerAsync)));
        if (await Task.WhenAny(workers, deadline) != workers)
            Reject(interrupt.IsCancellationRequested ? "interrupted" : "drain_timeout");
        await workers;
        stop.Cancel();
        if (interrupt.IsCancellationRequested) rejection ??= "interrupted";
        if (measured == 0) rejection ??= "empty";
        var duration = last == 0 ? 0 : clock.GetElapsedTime(first, Math.Max(end, last)).TotalSeconds;
        samples.Sort();
        ParityWindowLatency? latency = samples.Count == 0 ? null : new(
            samples[(int)Math.Ceiling(samples.Count * .50) - 1], samples[(int)Math.Ceiling(samples.Count * .99) - 1], maximum, samples.Count);
        return new(starts, measured, ok, errors, unfinished, duration, ok == 0 || duration == 0 ? null : ok / duration, latency, rejection);
    }
}
