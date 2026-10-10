using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text;

namespace TerraBase.Testing;

/// <summary>Reports bounded runtime samples at integration failures and session end.</summary>
public static partial class SuiteDiagnostics
{
    private const int Capacity = 600;
    private const int CgroupDepthLimit = 16;
    private static readonly Queue<Sample> Samples = new(Capacity);
    private static Timer? _timer;
    private static long _started;
    private static double _reported = -1;
    private static string? _cgroup;

    internal static TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    [Before(HookType.TestSession)]
    public static void Start()
    {
        _started = Stopwatch.GetTimestamp();
        _cgroup = CgroupDirectory(out var mountpoint);
        // Bypass TUnit's captured hook output so green runs expose the startup PID and final comparator rows.
        using (var output = new StreamWriter(Console.OpenStandardOutput()))
        {
            ReportConfiguration(output.WriteLine, mountpoint);
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
        if (context.Execution.Result?.State is not (TestState.Failed or TestState.Timeout or TestState.Cancelled))
        {
            return;
        }

        Capture(null);
        Report(context.OutputWriter.WriteLine);

        var generations = GC.GetGCMemoryInfo().GenerationInfo;
        for (var index = 0; index < generations.Length; index++)
        {
            var generation = generations[index];
            context.OutputWriter.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Suite heap: generation={index} before_bytes={generation.SizeBeforeBytes} after_bytes={generation.SizeAfterBytes} fragmented_bytes={generation.FragmentationAfterBytes}"));
        }
    }

    private static void Report(Action<string> write)
    {
        Sample[] samples;
        lock (Samples)
        {
            samples = [.. Samples.Where(sample => sample.Elapsed > _reported)];
            _reported = Samples.Last().Elapsed;
        }

        if (samples.Length == 0)
        {
            return;
        }

        // Each row holds only the latest completed collection; a gap in gc_index means unobserved collections.
        // Heap/load values describe that collection, not current live objects; allocation totals are approximate.
        // Pressure/throttling totals are cumulative microseconds; cgroup counters/bytes are -1 when unavailable.
        write(string.Create(CultureInfo.InvariantCulture,
            $"Suite runtime pid={Environment.ProcessId} cpus={Environment.ProcessorCount} server_gc={GCSettings.IsServerGC}: elapsed_s,cpu_s,gc_pause_s,gen2,threads,queued_work,monitor_contentions,gc_index,gc_gen,gc_promoted_bytes,gc_pause0_ms,gc_pause1_ms,host_cpu_some_us,cgroup_cpu_some_us,cgroup_throttled_us,load1,allocated_bytes,heap_bytes,fragmented_bytes,committed_bytes,memory_load_bytes,memory_load_threshold_bytes,working_set_bytes,cgroup_memory_current_bytes,cgroup_swap_current_bytes,cgroup_memory_low,cgroup_memory_high,cgroup_memory_max,cgroup_memory_oom,cgroup_memory_oom_kill,cgroup_memory_oom_group_kill,host_memory_some_us,host_memory_full_us,cgroup_memory_some_us,cgroup_memory_full_us,cgroup_pgmajfault"));
        foreach (var sample in samples)
        {
            write(string.Create(CultureInfo.InvariantCulture,
                $"Suite runtime: {sample.Elapsed:F3},{sample.Cpu:F3},{sample.Pause:F3},{sample.Gen2},{sample.Threads},{sample.Queued},{sample.Contentions},{sample.GcIndex},{sample.GcGeneration},{sample.GcPromoted},{sample.GcPause0:F3},{sample.GcPause1:F3},{sample.HostCpuSome},{sample.CgroupCpuSome},{sample.CgroupThrottled},{sample.Load1:F2},{sample.Allocated},{sample.Heap},{sample.Fragmented},{sample.Committed},{sample.MemoryLoad},{sample.MemoryLoadThreshold},{sample.WorkingSet},{sample.CgroupMemory},{sample.CgroupSwap},{sample.MemoryLow},{sample.MemoryHigh},{sample.MemoryMax},{sample.MemoryOom},{sample.MemoryOomKill},{sample.MemoryOomGroupKill},{sample.HostMemorySome},{sample.HostMemoryFull},{sample.CgroupMemorySome},{sample.CgroupMemoryFull},{sample.MajorFaults}"));
        }
    }

    [After(HookType.TestSession)]
    public static async Task StopAsync()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
        }

        Capture(null);
        using var output = new StreamWriter(Console.OpenStandardOutput());
        Report(output.WriteLine);
    }

    private static void ReportConfiguration(Action<string> write, string? mountpoint)
    {
        var configuration = GC.GetConfigurationVariables();
        string[] keys = ["ServerGC", "ConcurrentGC", "HeapCount", "MaxHeapCount", "GCDynamicAdaptationMode",
            "GCHeapHardLimit", "GCHeapHardLimitPercent", "GCHeapHardLimitSOH", "GCHeapHardLimitLOH", "GCHeapHardLimitPOH",
            "GCHeapHardLimitSOHPercent", "GCHeapHardLimitLOHPercent", "GCHeapHardLimitPOHPercent", "GCConserveMem",
            "NoAffinitize", "GCHeapAffinitizeMask", "GCCpuGroup", "RetainVM"];
        var settings = new StringBuilder();
        foreach (var key in keys)
        {
            var value = configuration.TryGetValue(key, out var configured) && configured is bool or long
                ? Convert.ToString(configured, CultureInfo.InvariantCulture) : "-1";
            settings.Append(CultureInfo.InvariantCulture, $" {key}={value}");
        }

        // AppContext exposes configured PGO only; absence does not establish the effective runtime value.
        var tieredPgo = AppContext.GetData("System.Runtime.TieredPGO") switch
        {
            bool enabled => $"configured:{enabled}",
            string value when bool.TryParse(value, out var enabled) => $"configured:{enabled}",
            null => "default",
            _ => "configured:unavailable"
        };
        write(string.Create(CultureInfo.InvariantCulture,
            $"Suite runtime configuration: pid={Environment.ProcessId} cpus={Environment.ProcessorCount} server_gc={GCSettings.IsServerGC} latency_mode={GCSettings.LatencyMode} total_available_memory_bytes={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes} tiered_pgo={tieredPgo}{settings}"));

        var directory = _cgroup;
        if (directory is null || mountpoint is null)
        {
            write("Suite cgroup limits: unavailable=-1");
            return;
        }

        for (var depth = 0; depth < CgroupDepthLimit; depth++)
        {
            write(string.Create(CultureInfo.InvariantCulture,
                $"Suite cgroup limits: depth={depth} path={directory} cpu_max=[{ReadSmall(directory + "/cpu.max")?.Trim() ?? "-1"}] cpuset_cpus_effective=[{ReadSmall(directory + "/cpuset.cpus.effective")?.Trim() ?? "-1"}] memory_max_bytes={ReadSmall(directory + "/memory.max")?.Trim() ?? "-1"} memory_high_bytes={ReadSmall(directory + "/memory.high")?.Trim() ?? "-1"} memory_swap_max_bytes={ReadSmall(directory + "/memory.swap.max")?.Trim() ?? "-1"}"));
            if (directory == mountpoint)
            {
                return;
            }

            directory = Path.GetDirectoryName(directory);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }
        }

        write($"Suite cgroup limits: ancestry_truncated=True depth_limit={CgroupDepthLimit}");
    }

    private static void Capture(object? state)
    {
        lock (Samples)
        {
            var gc = GC.GetGCMemoryInfo();
            var pauses = gc.PauseDurations;
            var load = ReadSmall("/proc/loadavg")?.Split(' ')[0];
            var memoryEvents = ReadCgroup("memory.events");
            var hostMemoryPressure = ReadSmall("/proc/pressure/memory");
            var cgroupMemoryPressure = ReadCgroup("memory.pressure");
            var sample = new Sample(Stopwatch.GetElapsedTime(_started).TotalSeconds, Environment.CpuUsage.TotalTime.TotalSeconds,
                GC.GetTotalPauseDuration().TotalSeconds, GC.CollectionCount(2), ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, Monitor.LockContentionCount,
                gc.Index, gc.Generation, gc.PromotedBytes, pauses[0].TotalMilliseconds, pauses[1].TotalMilliseconds,
                Field(ReadSmall("/proc/pressure/cpu"), "some ", "total="), Field(ReadCgroup("cpu.pressure"), "some ", "total="),
                Field(ReadCgroup("cpu.stat"), "throttled_usec ", ""),
                double.TryParse(load, NumberStyles.Float, CultureInfo.InvariantCulture, out var load1) ? load1 : -1,
                GC.GetTotalAllocatedBytes(), gc.HeapSizeBytes, gc.FragmentedBytes, gc.TotalCommittedBytes,
                gc.MemoryLoadBytes, gc.HighMemoryLoadThresholdBytes, Environment.WorkingSet,
                Field(ReadCgroup("memory.current"), "", ""), Field(ReadCgroup("memory.swap.current"), "", ""),
                Field(memoryEvents, "low ", ""), Field(memoryEvents, "high ", ""), Field(memoryEvents, "max ", ""),
                Field(memoryEvents, "oom ", ""), Field(memoryEvents, "oom_kill ", ""), Field(memoryEvents, "oom_group_kill ", ""),
                Field(hostMemoryPressure, "some ", "total="), Field(hostMemoryPressure, "full ", "total="),
                Field(cgroupMemoryPressure, "some ", "total="), Field(cgroupMemoryPressure, "full ", "total="),
                // memory.stat can place pgmajfault after 1 KiB; this read stays bounded at 4 KiB.
                Field(ReadCgroup("memory.stat", 4096), "pgmajfault ", ""));
            if (Samples.Count == Capacity)
            {
                Samples.Dequeue();
            }

            Samples.Enqueue(sample);
            CaptureStorage(sample.Elapsed, sample.Pause);
        }
    }

    private static string? ReadCgroup(string name, int limit = 1024) => _cgroup is null ? null : ReadSmall(_cgroup + "/" + name, limit);

    // This process's cgroup v2 directory, or null (fields report -1) when no visible cgroup2 mount contains it. Runs once.
    // /proc/self/cgroup gives "0::/path" relative to the cgroup namespace root; each cgroup2 line in /proc/self/mountinfo
    // gives the subtree root it exposes (field 4), relative to the same namespace root, at a mountpoint (field 5). Both
    // may climb above the namespace root with leading "/.." components. With equal climbs, the most specific mount whose
    // root is the membership path or a "/"-bounded prefix of it maps the remainder below its mountpoint. A mount whose
    // root is only further climbs (inherited from outside a cgroup namespace) hides the names between, so its directory
    // is searched and validated instead.
    private static string? CgroupDirectory(out string? cgroupMountpoint)
    {
        cgroupMountpoint = null;
        var membership = ReadSmall("/proc/self/cgroup", 4096)?.Split('\n').SkipLast(1)
            .FirstOrDefault(entry => entry.StartsWith("0::/", StringComparison.Ordinal))?[3..].TrimEnd('/');
        // 1 MiB covers hosts with thousands of mounts; past that the cut-off tail is ignored and fields report -1.
        var mountinfo = ReadSmall("/proc/self/mountinfo", 1 << 20);
        if (membership is null || mountinfo is null)
        {
            return null;
        }

        var (climb, names) = SplitClimb(membership);
        string? directory = null;
        var matched = -1;
        List<(int Depth, string Mountpoint)> inherited = [];
        foreach (var line in mountinfo[..(mountinfo.LastIndexOf('\n') + 1)].Split('\n'))
        {
            var fields = line.Split(' ');
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (fields.Length < 5 || separator < 0 || !line[(separator + 3)..].StartsWith("cgroup2 ", StringComparison.Ordinal))
            {
                continue;
            }

            var (rootClimb, root) = SplitClimb(Unescape(fields[3]).TrimEnd('/'));
            var mountpoint = Unescape(fields[4]).TrimEnd('/');
            if (rootClimb == climb && root.Length > matched && (names == root || names.StartsWith(root + "/", StringComparison.Ordinal)))
            {
                matched = root.Length;
                directory = mountpoint + names[root.Length..];
                cgroupMountpoint = mountpoint;
            }
            else if (rootClimb > climb && root.Length == 0)
            {
                inherited.Add((rootClimb - climb, mountpoint));
            }
        }

        if (directory is not null)
        {
            return directory;
        }

        foreach (var (depth, mountpoint) in inherited.OrderBy(mount => mount.Depth))
        {
            if (FindInherited(mountpoint, depth, names) is { } found)
            {
                cgroupMountpoint = mountpoint;
                return found;
            }
        }

        return null;
    }

    // Splits a namespace-relative cgroup path into its leading "/.." count and the remaining "/"-prefixed names.
    private static (int Climb, string Names) SplitClimb(string path)
    {
        var climb = 0;
        while (path == "/.." || path.StartsWith("/../", StringComparison.Ordinal))
        {
            climb++;
            path = path[3..];
        }

        return (climb, path);
    }

    // The directory depth levels below mountpoint, then names, whose cgroup.procs lists this process. A process belongs
    // to exactly one cgroup, so at most one candidate matches. Visits at most 4096 directories, else null.
    private static string? FindInherited(string mountpoint, int depth, string names)
    {
        List<string> level = [mountpoint];
        var visited = 0;
        for (var index = 0; index < depth; index++)
        {
            List<string> next = [];
            foreach (var parent in level)
            {
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(parent + "/"))
                    {
                        if (++visited > 4096)
                        {
                            return null;
                        }

                        next.Add(child);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // An unreadable branch is skipped; if it held this process, no candidate matches and fields report -1.
                }
            }

            level = next;
        }

        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        return level.Select(candidate => candidate + names).FirstOrDefault(candidate =>
        {
            try
            {
                return File.ReadLines(candidate + "/cgroup.procs").Contains(pid);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        });
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
        long HostCpuSome, long CgroupCpuSome, long CgroupThrottled, double Load1,
        long Allocated, long Heap, long Fragmented, long Committed, long MemoryLoad, long MemoryLoadThreshold, long WorkingSet,
        long CgroupMemory, long CgroupSwap, long MemoryLow, long MemoryHigh, long MemoryMax, long MemoryOom, long MemoryOomKill, long MemoryOomGroupKill,
        long HostMemorySome, long HostMemoryFull, long CgroupMemorySome, long CgroupMemoryFull, long MajorFaults);
}
