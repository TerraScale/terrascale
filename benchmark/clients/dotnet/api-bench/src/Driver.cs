using System.Diagnostics;
using HdrHistogram;

namespace ApiBench;

/// <summary>One operation. It returns its status key, or throws; it may restart <see cref="OpTimer.Start"/> to exclude untimed work.</summary>
internal delegate Task<string> Op(CancellationToken cancellation, OpTimer timer);

internal sealed class OpTimer
{
    /// <summary>Stopwatch timestamp that latency is measured from.</summary>
    public long Start { get; set; }
}

/// <summary>Runs one step: warmup (unrecorded), then the recorded window. Only ops that start and finish inside it count.</summary>
internal sealed class Driver
{
    private const long MaxMicros = 60_000_000;
    private const int MinAbortOps = 20;

    private readonly Step step;
    private readonly Op op;
    private readonly CountingHandler counter;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly LongHistogram latency = new(1, MaxMicros, 3);
    private readonly LongHistogram second = new(1, MaxMicros, 3);
    private readonly Dictionary<string, long> statuses = [];
    private long windowStart;
    private long windowEnd;
    private long ops;
    private long ok;
    private long secondOps;
    private string stopped = "";

    internal Driver(Step step, Op op, CountingHandler counter)
    {
        if (step.Mode == "open" && step.Rate <= 0) throw new ArgumentOutOfRangeException(nameof(step), "open mode needs a positive rate.");
        this.step = step;
        this.op = op;
        this.counter = counter;
    }

    internal async Task<StepResult> RunAsync(CancellationToken interrupt)
    {
        var utc = DateTimeOffset.UtcNow;
        var begin = Stopwatch.GetTimestamp();
        windowStart = begin + Ticks(step.WarmupS);
        windowEnd = windowStart + Ticks(step.StepS);
        using var cut = interrupt.Register(() => Stop(""));
        var load = step.Mode == "open" ? OpenAsync(begin, stop.Token) : ClosedAsync(stop.Token);
        var check = CheckAsync(stop.Token);
        await PauseUntil(windowStart, stop.Token);
        var first = Sample();
        await PauseUntil(Volatile.Read(ref windowEnd), stop.Token);
        var last = Sample();
        stop.Cancel();
        await Task.WhenAll(load, check);
        return Result(utc.AddSeconds(step.WarmupS), first, last);
    }

    private async Task ClosedAsync(CancellationToken ct) =>
        await Task.WhenAll(Enumerable.Range(0, step.Concurrency).Select(_ => WorkerAsync(ct)));

    private async Task WorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Stopwatch.GetTimestamp() < Volatile.Read(ref windowEnd))
        {
            var timer = new OpTimer { Start = Stopwatch.GetTimestamp() };
            var status = await Invoke(timer, ct);
            Record(timer.Start, Stopwatch.GetTimestamp(), status);
        }
    }

    /// <summary>Arrival i is due at begin + i/rate. Late arrivals are still issued; an arrival that finds the cap full is dropped.</summary>
    private async Task OpenAsync(long begin, CancellationToken ct)
    {
        var slots = new SemaphoreSlim(step.Concurrency);
        for (var i = 0L; ; i++)
        {
            var due = begin + Ticks(i / step.Rate);
            if (due >= Volatile.Read(ref windowEnd) || ct.IsCancellationRequested) break;
            await PauseUntil(due, ct);
            if (!slots.Wait(0))
            {
                Record(due, Stopwatch.GetTimestamp(), "dropped");
                continue;
            }

            _ = FireAsync(due, slots, ct);
        }

        for (var slot = 0; slot < step.Concurrency; slot++) await slots.WaitAsync();
    }

    private async Task FireAsync(long due, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            var timer = new OpTimer { Start = due };
            var status = await Invoke(timer, ct);
            Record(timer.Start, Stopwatch.GetTimestamp(), status);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>Checks once a second. Error rate uses every recorded op so far; p99 uses the last second.</summary>
    private async Task CheckAsync(CancellationToken ct)
    {
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await tick.WaitForNextTickAsync(ct))
            {
                var reason = Judge();
                if (reason.Length > 0)
                {
                    Stop(reason);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string Judge()
    {
        lock (gate)
        {
            var reason = ops >= MinAbortOps && ops - ok > ops * step.AbortErrorRate ? "error rate"
                : step.AbortP99Us > 0 && secondOps >= MinAbortOps && second.GetValueAtPercentile(99) > step.AbortP99Us ? "p99"
                : "";
            second.Reset();
            secondOps = 0;
            return reason;
        }
    }

    /// <summary>Ends the window now. Ops still running finish outside it and are not counted.</summary>
    private void Stop(string reason)
    {
        lock (gate)
        {
            if (stopped.Length == 0) stopped = reason;
            windowEnd = Math.Min(windowEnd, Stopwatch.GetTimestamp());
        }

        stop.Cancel();
    }

    private async Task<string> Invoke(OpTimer timer, CancellationToken ct)
    {
        try
        {
            return await op(ct, timer);
        }
        catch (Exception error)
        {
            return Http.StatusOf(error);
        }
    }

    private void Record(long started, long finished, string status)
    {
        lock (gate)
        {
            if (started < windowStart || finished > windowEnd) return;
            ops++;
            secondOps++;
            statuses[status] = statuses.GetValueOrDefault(status) + 1;
            if (status != "ok") return;
            ok++;
            var micros = Math.Clamp((long)Stopwatch.GetElapsedTime(started, finished).TotalMicroseconds, 1, MaxMicros);
            latency.RecordValue(micros);
            second.RecordValue(micros);
        }
    }

    private StepResult Result(DateTimeOffset start, Meter first, Meter last)
    {
        var duration = Math.Max(0, Stopwatch.GetElapsedTime(windowStart, Volatile.Read(ref windowEnd)).TotalSeconds);
        lock (gate)
        {
            return new StepResult(
                "result", step.Scenario, step.Transport, step.PayloadBytes, step.Mode, step.Concurrency, step.Rate, Protocol.Stamp(start),
                duration, ops, ok, ops - ok, ops == 0 ? 0 : (double)(ops - ok) / ops, duration > 0 ? ok / duration : 0,
                new(statuses),
                new(latency.GetValueAtPercentile(50), latency.GetValueAtPercentile(90), latency.GetValueAtPercentile(99),
                    latency.GetValueAtPercentile(99.9), latency.GetMaxValue(), (long)latency.GetMean()),
                last.Out - first.Out, last.In - first.In, last.Cpu - first.Cpu, Protocol.RssMb(), stopped);
        }
    }

    private readonly record struct Meter(double Cpu, long Out, long In);

    private Meter Sample()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.TotalProcessorTime.TotalSeconds, counter.BytesOut, counter.BytesIn);
    }

    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static async Task PauseUntil(long time, CancellationToken ct)
    {
        var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), time);
        if (wait <= TimeSpan.Zero) return;
        try
        {
            await Task.Delay(wait, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
