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
    private static string? _cgroup;

    internal static TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    [Before(HookType.TestSession)]
    public static void Start()
    {
        _started = Stopwatch.GetTimestamp();
        _cgroup = CgroupDirectory();
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
                Field(ReadSmall("/proc/pressure/cpu"), "some ", "total="), Field(_cgroup is null ? null : ReadSmall(_cgroup + "/cpu.pressure"), "some ", "total="),
                Field(_cgroup is null ? null : ReadSmall(_cgroup + "/cpu.stat"), "throttled_usec ", ""),
                double.TryParse(load, NumberStyles.Float, CultureInfo.InvariantCulture, out var load1) ? load1 : -1);
            if (Samples.Count == Capacity)
            {
                Samples.Dequeue();
            }

            Samples.Enqueue(sample);
        }
    }

    // This process's cgroup v2 directory, or null (fields report -1) when no visible cgroup2 mount contains it. Runs once.
    // /proc/self/cgroup gives "0::/path" relative to the hierarchy (or cgroup namespace) root; each cgroup2 line in
    // /proc/self/mountinfo gives the subtree root it exposes (field 4) at a mountpoint (field 5). The most specific mount
    // whose root is the membership path or a "/"-bounded prefix of it maps the remainder below its mountpoint.
    private static string? CgroupDirectory()
    {
        var membership = ReadSmall("/proc/self/cgroup", 4096)?.Split('\n').SkipLast(1)
            .FirstOrDefault(entry => entry.StartsWith("0::/", StringComparison.Ordinal))?[3..].TrimEnd('/');
        // 1 MiB covers hosts with thousands of mounts; past that the cut-off tail is ignored and fields report -1.
        var mountinfo = ReadSmall("/proc/self/mountinfo", 1 << 20);
        if (membership is null || mountinfo is null)
        {
            return null;
        }

        string? directory = null;
        var matched = -1;
        foreach (var line in mountinfo[..(mountinfo.LastIndexOf('\n') + 1)].Split('\n'))
        {
            var fields = line.Split(' ');
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (fields.Length < 5 || separator < 0 || !line[(separator + 3)..].StartsWith("cgroup2 ", StringComparison.Ordinal))
            {
                continue;
            }

            var root = Unescape(fields[3]).TrimEnd('/');
            if (root.Length > matched && (membership == root || membership.StartsWith(root + "/", StringComparison.Ordinal)))
            {
                matched = root.Length;
                directory = Unescape(fields[4]).TrimEnd('/') + membership[root.Length..];
            }
        }

        return directory;
    }

    // mountinfo octal-escapes space, tab, newline and backslash; backslash goes last so "\134040" stays a literal "\040".
    private static string Unescape(string field) => field.Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);

    // Best effort: reads at most limit bytes (stack buffer for the 1 KiB per-second stats), null when absent or unreadable.
    private static string? ReadSmall(string path, int limit = 1024)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
            Span<byte> buffer = limit <= 1024 ? stackalloc byte[1024] : new byte[limit];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = file.Read(buffer[length..])) > 0)
            {
                length += read;
            }

            return Encoding.UTF8.GetString(buffer[..length]);
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
