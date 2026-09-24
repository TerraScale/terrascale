using System.Diagnostics;
using ZoneTree.Compression;

var mode = args.Length == 0 ? "cold" : args[0];
var source = new byte[4 * 1024 * 1024];
uint state = 0x9E3779B9;
for (var index = 0; index < source.Length; index++)
{
    state = unchecked(state * 1_664_525u + 1_013_904_223u);
    source[index] = (byte)(state >> 24);
}

if (mode == "warm")
{
    for (var index = 0; index < 3; index++)
    {
        var warmup = LZ4DataCompression.Compress(source, level: 3);
        GC.KeepAlive(warmup);
    }
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

var before = GC.GetAllocatedBytesForCurrentThread();
var stopwatch = Stopwatch.StartNew();
var compressed = LZ4DataCompression.Compress(source, level: 3);
stopwatch.Stop();
var after = GC.GetAllocatedBytesForCurrentThread();
GC.KeepAlive(compressed);

Console.WriteLine($"mode={mode} input={source.Length} output={compressed.Length} allocated-current-thread={after - before} elapsed-ms={stopwatch.Elapsed.TotalMilliseconds:F3}");
