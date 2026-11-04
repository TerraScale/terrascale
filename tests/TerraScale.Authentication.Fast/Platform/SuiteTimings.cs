using System.Diagnostics;
using System.Globalization;
using System.Runtime;

namespace TerraScale.Authentication.Fast.Platform;

/// <summary>Reports runtime costs independently of test assertions and runner startup.</summary>
public static class SuiteTimings
{
    private static long started;
    private static TimeSpan cpu;
    private static TimeSpan paused;

    [Before(HookType.TestSession)]
    public static void Start()
    {
        started = Stopwatch.GetTimestamp();
        cpu = Environment.CpuUsage.TotalTime;
        paused = GC.GetTotalPauseDuration();
    }

    [After(HookType.TestSession)]
    public static void Report()
    {
        using var output = new StreamWriter(Console.OpenStandardOutput());
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Auth runtime: wall={Stopwatch.GetElapsedTime(started).TotalSeconds:F3}s cpu={(Environment.CpuUsage.TotalTime - cpu).TotalSeconds:F3}s gc_pause={(GC.GetTotalPauseDuration() - paused).TotalSeconds:F3}s server_gc={GCSettings.IsServerGC} gen2={GC.CollectionCount(2)} allocated={GC.GetTotalAllocatedBytes()}"));
    }
}
