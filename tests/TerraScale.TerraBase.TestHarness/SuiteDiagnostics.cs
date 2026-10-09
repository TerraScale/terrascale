using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text;

namespace TerraBase.Testing;

/// <summary>Reports bounded runtime samples at the first integration failure, before a CI job can be killed.</summary>
public static class SuiteDiagnostics
{
    private const int Capacity = 600;
    private static readonly Queue<Sample> Samples = new(Capacity);
    private static Timer? _timer;
    private static long _started;
    private static int _reported;
    private static string _cgroup = "/sys/fs/cgroup";

    internal static TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    [Before(HookType.TestSession)]
    public static void Start()
    {
        _started = Stopwatch.GetTimestamp();
        // cgroup v2 lists this process's cgroup as "0::/path"; without a cgroup namespace it is not the mount root.
        var membership = ReadSmall("/proc/self/cgroup")?.Split('\n').FirstOrDefault(entry => entry.StartsWith("0::/", StringComparison.Ordinal));
        if (membership is not null)
        {
            _cgroup += membership[3..].TrimEnd('/');
        }

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

        // Each row holds only the latest completed collection; a gap in gc_index means unobserved collections.
        // Pressure and throttling totals are cumulative microseconds, -1 where the host does not expose them.
        context.OutputWriter.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Suite runtime pid={Environment.ProcessId} cpus={Environment.ProcessorCount} server_gc={GCSettings.IsServerGC}: elapsed_s,cpu_s,gc_pause_s,gen2,threads,queued_work,monitor_contentions,gc_index,gc_gen,gc_promoted_bytes,gc_pause0_ms,gc_pause1_ms,host_cpu_some_us,cgroup_cpu_some_us,cgroup_throttled_us,load1"));
        foreach (var sample in samples)
        {
            context.OutputWriter.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Suite runtime: {sample.Elapsed:F3},{sample.Cpu:F3},{sample.Pause:F3},{sample.Gen2},{sample.Threads},{sample.Queued},{sample.Contentions},{sample.GcIndex},{sample.GcGeneration},{sample.GcPromoted},{sample.GcPause0:F3},{sample.GcPause1:F3},{sample.HostCpuSome},{sample.CgroupCpuSome},{sample.CgroupThrottled},{sample.Load1:F2}"));
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
            var gc = GC.GetGCMemoryInfo();
            var pauses = gc.PauseDurations;
            var load = ReadSmall("/proc/loadavg")?.Split(' ')[0];
            var sample = new Sample(Stopwatch.GetElapsedTime(_started).TotalSeconds, Environment.CpuUsage.TotalTime.TotalSeconds,
                GC.GetTotalPauseDuration().TotalSeconds, GC.CollectionCount(2), ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, Monitor.LockContentionCount,
                gc.Index, gc.Generation, gc.PromotedBytes, pauses[0].TotalMilliseconds, pauses[1].TotalMilliseconds,
                Field(ReadSmall("/proc/pressure/cpu"), "some ", "total="), Field(ReadSmall(_cgroup + "/cpu.pressure"), "some ", "total="),
                Field(ReadSmall(_cgroup + "/cpu.stat"), "throttled_usec ", ""),
                double.TryParse(load, NumberStyles.Float, CultureInfo.InvariantCulture, out var load1) ? load1 : -1);
            if (Samples.Count == Capacity)
            {
                Samples.Dequeue();
            }

            Samples.Enqueue(sample);
        }
    }

    // Best effort: one read of at most 1 KiB, null when the file is absent or unreadable.
    private static string? ReadSmall(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
            Span<byte> buffer = stackalloc byte[1024];
            return Encoding.ASCII.GetString(buffer[..file.Read(buffer)]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The first integer after key in the line starting with prefix ("some ... total=N", "throttled_usec N"), or -1.
    private static long Field(string? text, string prefix, string key)
    {
        var line = text?.Split('\n').FirstOrDefault(entry => entry.StartsWith(prefix, StringComparison.Ordinal));
        var part = line?[prefix.Length..].Split(' ').FirstOrDefault(entry => entry.StartsWith(key, StringComparison.Ordinal));
        return long.TryParse(part?[key.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
    }

    private readonly record struct Sample(double Elapsed, double Cpu, double Pause, int Gen2, int Threads, long Queued, long Contentions,
        long GcIndex, int GcGeneration, long GcPromoted, double GcPause0, double GcPause1,
        long HostCpuSome, long CgroupCpuSome, long CgroupThrottled, double Load1);
}
