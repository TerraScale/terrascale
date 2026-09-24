namespace TerraBase.Benchmarks;

internal sealed record BenchmarkCgroupSnapshot(
    string? Path,
    string? MemoryMax,
    string? MemoryPeak,
    string? SwapMax,
    string? MemoryEvents,
    string? MemoryCurrent,
    string? SwapCurrent,
    string? IoStat)
{
    internal static BenchmarkCgroupSnapshot Capture()
    {
        if (!OperatingSystem.IsLinux()) return Empty;
        try
        {
            var entry = File.ReadLines("/proc/self/cgroup").FirstOrDefault(static line => line.StartsWith("0::", StringComparison.Ordinal));
            if (entry is null) return Empty;
            var path = System.IO.Path.Combine("/sys/fs/cgroup", entry[3..].TrimStart('/'));
            return new(
                path,
                Read(path, "memory.max"),
                Read(path, "memory.peak"),
                Read(path, "memory.swap.max"),
                Read(path, "memory.events"),
                Read(path, "memory.current"),
                Read(path, "memory.swap.current"),
                Read(path, "io.stat"));
        }
        catch (IOException) { return Empty; }
        catch (UnauthorizedAccessException) { return Empty; }
    }

    private static BenchmarkCgroupSnapshot Empty { get; } = new(null, null, null, null, null, null, null, null);

    private static string? Read(string root, string name)
    {
        var path = System.IO.Path.Combine(root, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}
