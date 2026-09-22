namespace TerraBase.Benchmarks;

internal sealed record BenchmarkCgroupSnapshot(
    string? Path, string? MemoryMax, string? MemoryPeak, string? SwapMax, string? MemoryEvents)
{
    internal static BenchmarkCgroupSnapshot Capture()
    {
        if (!OperatingSystem.IsLinux()) return new(null, null, null, null, null);
        try
        {
            var entry = File.ReadLines("/proc/self/cgroup").FirstOrDefault(static line => line.StartsWith("0::", StringComparison.Ordinal));
            if (entry is null) return new(null, null, null, null, null);
            var path = System.IO.Path.Combine("/sys/fs/cgroup", entry[3..].TrimStart('/'));
            return new(path, Read(path, "memory.max"), Read(path, "memory.peak"), Read(path, "memory.swap.max"), Read(path, "memory.events"));
        }
        catch (IOException) { return new(null, null, null, null, null); }
        catch (UnauthorizedAccessException) { return new(null, null, null, null, null); }
    }

    private static string? Read(string root, string name)
    {
        var path = System.IO.Path.Combine(root, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}
