using System.Diagnostics;
using System.Globalization;

namespace TerraBase.Testing;

/// <summary>Reports bounded runtime samples at the first integration failure, before a CI job can be killed.</summary>
public static class SuiteDiagnostics
{
    private const int Capacity = 600;
    private static readonly Queue<Sample> Samples = new(Capacity);
    private static Timer? _timer;
    private static long _started;
    private static int _reported;

    [Before(HookType.TestSession)]
    public static void Start()
    {
        _started = Stopwatch.GetTimestamp();
        Capture(null);
        using (ExecutionContext.SuppressFlow())
        {
            _timer = new Timer(Capture, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    [AfterEvery(HookType.Test)]
    public static void ReportFailure(TestContext context)
    {
        if (context.Execution.Result?.State is not (TestState.Failed or TestState.Timeout or TestState.Cancelled)
            || Interlocked.Exchange(ref _reported, 1) != 0)
        {
            return;
        }

        Capture(null);
        Sample[] samples;
        lock (Samples)
        {
            samples = [.. Samples];
        }

        context.OutputWriter.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Suite runtime pid={Environment.ProcessId} cpus={Environment.ProcessorCount}: elapsed_s,cpu_s,gc_pause_s,gen2,threads,queued_work,monitor_contentions"));
        foreach (var sample in samples)
        {
            context.OutputWriter.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Suite runtime: {sample.Elapsed:F3},{sample.Cpu:F3},{sample.Pause:F3},{sample.Gen2},{sample.Threads},{sample.Queued},{sample.Contentions}"));
        }
    }

    [After(HookType.TestSession)]
    public static async Task StopAsync()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
        }
    }

    private static void Capture(object? state)
    {
        lock (Samples)
        {
            if (Samples.Count == Capacity)
            {
                Samples.Dequeue();
            }

            Samples.Enqueue(new(Stopwatch.GetElapsedTime(_started).TotalSeconds, Environment.CpuUsage.TotalTime.TotalSeconds,
                GC.GetTotalPauseDuration().TotalSeconds, GC.CollectionCount(2), ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, Monitor.LockContentionCount));
        }
    }

    private readonly record struct Sample(double Elapsed, double Cpu, double Pause, int Gen2, int Threads, long Queued, long Contentions);
}
