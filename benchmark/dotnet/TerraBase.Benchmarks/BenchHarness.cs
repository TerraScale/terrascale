using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;

namespace TerraBase.Benchmarks;

public sealed record BenchMetrics(
    string Suite,
    string Name,
    int Rounds,
    int IterationsPerRound,
    double OpsPerSec,
    double P50Us,
    double P95Us,
    double P99Us,
    double MeanUs,
    double CpuUsPerOp,
    double AllocBytesPerOp,
    double WorkingSetMiB,
    long DiskBytes,
    double IoWriteBytesPerOp,
    double IoReadBytesPerOp,
    string Note = "");

internal sealed record BenchReport(
    DateTimeOffset TimestampUtc,
    string MachineName,
    string DotNetVersion,
    string OsDescription,
    int ProcessorCount,
    string NativeSqliteVersion,
    int Rounds,
    double IterationsScale,
    IReadOnlyList<BenchMetrics> Metrics);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchReport))]
[JsonSerializable(typeof(BenchMetrics))]
internal sealed partial class BenchJsonContext : JsonSerializerContext;

public readonly record struct ProcIoSnapshot(long ReadChars, long WriteChars, long ReadBytes, long WriteBytes);

public sealed class BenchRunnerContext : IDisposable
{
    private const double BytesPerMiB = 1024.0 * 1024.0;
    private const string ProcIoPath = "/proc/self/io";
    private const string ProcStatusPath = "/proc/self/status";
    private const string ProcFdPath = "/proc/self/fd";

    private readonly HashSet<string> _selectedSuites;
    private readonly bool _runAll;
    private readonly string _rootDirectory;
    private readonly List<string> _runDirectories = [];
    private readonly List<BenchMetrics> _results = [];
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly CancellationToken _cancellationToken;

    public BenchRunnerContext(IEnumerable<string> suites, int rounds, double iterationsScale = 1.0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suites);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rounds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterationsScale);

        Rounds = rounds;
        IterationsScale = iterationsScale;
        _cancellationToken = cancellationToken;
        _selectedSuites = new HashSet<string>(suites, StringComparer.OrdinalIgnoreCase);
        _runAll = _selectedSuites.Count == 0 || _selectedSuites.Contains("all");
        _rootDirectory = Path.Combine(Path.GetTempPath(), string.Concat("terrabase-bench-", Ulid.NewUlid().ToString()));
        _ = Directory.CreateDirectory(_rootDirectory);
    }

    public int Rounds { get; }

    public double IterationsScale { get; }

    public IReadOnlyList<BenchMetrics> Results => _results;

    public bool ShouldRun(string suite) => _runAll || _selectedSuites.Contains(suite);

    public int ScaleIterations(int iterations) => Math.Max(1, (int)Math.Round(iterations * IterationsScale));

    public string RunDir(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var sanitized = SanitizeName(name);
        var path = Path.Combine(_rootDirectory, string.Concat(sanitized, "-", Ulid.NewUlid().ToString()));
        _ = Directory.CreateDirectory(path);
        _runDirectories.Add(path);
        return path;
    }

    public void AddResult(BenchMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _results.Add(metrics);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[{metrics.Suite}] {metrics.Name,-34} {metrics.OpsPerSec,10:F1} ops/s | p50={metrics.P50Us,8:F1}us p95={metrics.P95Us,8:F1}us p99={metrics.P99Us,8:F1}us | cpu={metrics.CpuUsPerOp,7:F1}us alloc={metrics.AllocBytesPerOp,8:F0}B rss={metrics.WorkingSetMiB,6:F1}MiB disk={metrics.DiskBytes,8}B{(metrics.Note.Length > 0 ? string.Concat(" | ", metrics.Note) : string.Empty)}"));
    }

    public BenchMetrics RecordSnapshotMetric(
        string suite,
        string name,
        double opsPerSec = 0,
        double p50Us = 0,
        double p95Us = 0,
        double p99Us = 0,
        double meanUs = 0,
        double cpuUsPerOp = 0,
        double allocBytesPerOp = 0,
        double? workingSetMiB = null,
        long diskBytes = 0,
        double ioWriteBytesPerOp = 0,
        double ioReadBytesPerOp = 0,
        string note = "")
    {
        var rssMiB = workingSetMiB ?? (ReadWorkingSetBytes() / BytesPerMiB);
        var metrics = new BenchMetrics(
            suite,
            name,
            1,
            1,
            opsPerSec,
            p50Us,
            p95Us,
            p99Us,
            meanUs,
            cpuUsPerOp,
            allocBytesPerOp,
            rssMiB,
            diskBytes,
            ioWriteBytesPerOp,
            ioReadBytesPerOp,
            note);
        AddResult(metrics);
        return metrics;
    }

    public async Task<BenchMetrics> MeasureAsync(
        string suite,
        string name,
        int warmup,
        int iterations,
        Func<int, ValueTask> body,
        Func<long>? diskBytes = null,
        string note = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var scaledIters = ScaleIterations(iterations);
        var opIndex = 0;

        for (var w = 0; w < warmup; w++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            await body(opIndex++);
        }

        var totalOps = Rounds * scaledIters;
        var latenciesUs = new double[totalOps];
        var totalElapsedSeconds = 0.0;
        var totalCpuUs = 0.0;
        var totalAllocBytes = 0L;
        var totalReadChars = 0L;
        var totalWriteChars = 0L;
        var totalReadBytes = 0L;
        var totalWriteBytes = 0L;
        var peakWorkingSetBytes = 0L;
        var sampleCursor = 0;

        for (var round = 0; round < Rounds; round++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            CollectMemory();

            _process.Refresh();
            var cpuBefore = _process.TotalProcessorTime;
            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var ioBefore = ReadProcIo();
            var roundStart = Stopwatch.GetTimestamp();

            for (var i = 0; i < scaledIters; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                var opStart = Stopwatch.GetTimestamp();
                await body(opIndex++);
                latenciesUs[sampleCursor++] = Stopwatch.GetElapsedTime(opStart).TotalMicroseconds;
            }

            var roundElapsed = Stopwatch.GetElapsedTime(roundStart).TotalSeconds;
            var ioAfter = ReadProcIo();
            var allocAfter = GC.GetTotalAllocatedBytes(precise: true);
            _process.Refresh();
            var cpuAfter = _process.TotalProcessorTime;

            totalElapsedSeconds += roundElapsed;
            totalCpuUs += (cpuAfter - cpuBefore).TotalMicroseconds;
            totalAllocBytes += Math.Max(0, allocAfter - allocBefore);
            totalReadChars += Math.Max(0, ioAfter.ReadChars - ioBefore.ReadChars);
            totalWriteChars += Math.Max(0, ioAfter.WriteChars - ioBefore.WriteChars);
            totalReadBytes += Math.Max(0, ioAfter.ReadBytes - ioBefore.ReadBytes);
            totalWriteBytes += Math.Max(0, ioAfter.WriteBytes - ioBefore.WriteBytes);
            peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, _process.WorkingSet64);
        }

        return FinalizeMetrics(
            suite,
            name,
            scaledIters,
            latenciesUs,
            totalElapsedSeconds,
            totalCpuUs,
            totalAllocBytes,
            peakWorkingSetBytes,
            diskBytes is not null ? diskBytes() : 0L,
            totalReadChars,
            totalWriteChars,
            totalReadBytes,
            totalWriteBytes,
            note);
    }

    public BenchMetrics MeasureSync(
        string suite,
        string name,
        int warmup,
        int iterations,
        Action<int> body,
        Func<long>? diskBytes = null,
        string note = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var scaledIters = ScaleIterations(iterations);
        var opIndex = 0;

        for (var w = 0; w < warmup; w++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            body(opIndex++);
        }

        var totalOps = Rounds * scaledIters;
        var latenciesUs = new double[totalOps];
        var totalElapsedSeconds = 0.0;
        var totalCpuUs = 0.0;
        var totalAllocBytes = 0L;
        var totalReadChars = 0L;
        var totalWriteChars = 0L;
        var totalReadBytes = 0L;
        var totalWriteBytes = 0L;
        var peakWorkingSetBytes = 0L;
        var sampleCursor = 0;

        for (var round = 0; round < Rounds; round++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            CollectMemory();

            _process.Refresh();
            var cpuBefore = _process.TotalProcessorTime;
            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var ioBefore = ReadProcIo();
            var roundStart = Stopwatch.GetTimestamp();

            for (var i = 0; i < scaledIters; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                var opStart = Stopwatch.GetTimestamp();
                body(opIndex++);
                latenciesUs[sampleCursor++] = Stopwatch.GetElapsedTime(opStart).TotalMicroseconds;
            }

            var roundElapsed = Stopwatch.GetElapsedTime(roundStart).TotalSeconds;
            var ioAfter = ReadProcIo();
            var allocAfter = GC.GetTotalAllocatedBytes(precise: true);
            _process.Refresh();
            var cpuAfter = _process.TotalProcessorTime;

            totalElapsedSeconds += roundElapsed;
            totalCpuUs += (cpuAfter - cpuBefore).TotalMicroseconds;
            totalAllocBytes += Math.Max(0, allocAfter - allocBefore);
            totalReadChars += Math.Max(0, ioAfter.ReadChars - ioBefore.ReadChars);
            totalWriteChars += Math.Max(0, ioAfter.WriteChars - ioBefore.WriteChars);
            totalReadBytes += Math.Max(0, ioAfter.ReadBytes - ioBefore.ReadBytes);
            totalWriteBytes += Math.Max(0, ioAfter.WriteBytes - ioBefore.WriteBytes);
            peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, _process.WorkingSet64);
        }

        return FinalizeMetrics(
            suite,
            name,
            scaledIters,
            latenciesUs,
            totalElapsedSeconds,
            totalCpuUs,
            totalAllocBytes,
            peakWorkingSetBytes,
            diskBytes is not null ? diskBytes() : 0L,
            totalReadChars,
            totalWriteChars,
            totalReadBytes,
            totalWriteBytes,
            note);
    }

    public long ReadWorkingSetBytes()
    {
        _process.Refresh();
        return _process.WorkingSet64;
    }

    public static long ReadRssBytes()
    {
        if (File.Exists(ProcStatusPath))
        {
            foreach (var line in File.ReadLines(ProcStatusPath))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kib))
                {
                    return kib * 1024L;
                }
            }
        }

        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    public static int ReadOpenFdCount() =>
        Directory.Exists(ProcFdPath) ? Directory.GetFileSystemEntries(ProcFdPath).Length : 0;

    public static ProcIoSnapshot ReadProcIo()
    {
        if (!File.Exists(ProcIoPath))
        {
            return default;
        }

        var rchar = 0L;
        var wchar = 0L;
        var readBytes = 0L;
        var writeBytes = 0L;
        foreach (var line in File.ReadLines(ProcIoPath))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line.AsSpan(0, colon);
            var rawValue = line.AsSpan(colon + 1).Trim();
            if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                continue;
            }

            if (key.SequenceEqual("rchar"))
            {
                rchar = parsed;
            }
            else if (key.SequenceEqual("wchar"))
            {
                wchar = parsed;
            }
            else if (key.SequenceEqual("read_bytes"))
            {
                readBytes = parsed;
            }
            else if (key.SequenceEqual("write_bytes"))
            {
                writeBytes = parsed;
            }
        }

        return new ProcIoSnapshot(rchar, wchar, readBytes, writeBytes);
    }

    public static long DirectorySizeBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var total = 0L;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.Exists)
            {
                total += info.Length;
            }
        }

        return total;
    }

    public static void AssertCondition(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static T Require<T>(T? value, string? error, string fallback) where T : class =>
        value ?? throw new InvalidOperationException(error ?? fallback);

    public static void PrintSummaryTable(IReadOnlyList<BenchMetrics> results)
    {
        Console.WriteLine();
        Console.WriteLine("| Suite | Benchmark | Ops/s | P50 (us) | P95 (us) | P99 (us) | Mean (us) | CPU (us/op) | Alloc (B/op) | RSS (MiB) | Disk (B) | IO W/R (B/op) | Note |");
        Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var item in results)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {item.Suite} | {item.Name} | {item.OpsPerSec:F1} | {item.P50Us:F1} | {item.P95Us:F1} | {item.P99Us:F1} | {item.MeanUs:F1} | {item.CpuUsPerOp:F1} | {item.AllocBytesPerOp:F0} | {item.WorkingSetMiB:F1} | {item.DiskBytes} | {item.IoWriteBytesPerOp:F0}/{item.IoReadBytesPerOp:F0} | {item.Note} |"));
        }
    }

    public void Dispose()
    {
        _process.Dispose();
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private BenchMetrics FinalizeMetrics(
        string suite,
        string name,
        int iterationsPerRound,
        double[] latenciesUs,
        double totalElapsedSeconds,
        double totalCpuUs,
        long totalAllocBytes,
        long peakWorkingSetBytes,
        long diskBytes,
        long totalRchar,
        long totalWchar,
        long totalReadBytes,
        long totalWriteBytes,
        string note)
    {
        Array.Sort(latenciesUs);
        var totalOps = latenciesUs.Length;
        var sumUs = 0.0;
        for (var i = 0; i < totalOps; i++)
        {
            sumUs += latenciesUs[i];
        }

        var meanUs = sumUs / totalOps;
        var p50Us = Percentile(latenciesUs, 0.50);
        var p95Us = Percentile(latenciesUs, 0.95);
        var p99Us = Percentile(latenciesUs, 0.99);
        var opsPerSec = totalElapsedSeconds > 0 ? totalOps / totalElapsedSeconds : 0;
        var cpuUsPerOp = totalCpuUs / totalOps;
        var allocBytesPerOp = (double)totalAllocBytes / totalOps;
        var workingSetMiB = peakWorkingSetBytes / BytesPerMiB;
        var ioWritePerOp = (double)(totalWriteBytes > 0 ? totalWriteBytes : totalWchar) / totalOps;
        var ioReadPerOp = (double)(totalReadBytes > 0 ? totalReadBytes : totalRchar) / totalOps;

        var ioDetail = string.Create(
            CultureInfo.InvariantCulture,
            $"io(rchar={totalRchar / (double)totalOps:F0},wchar={totalWchar / (double)totalOps:F0},read_b={totalReadBytes / (double)totalOps:F0},write_b={totalWriteBytes / (double)totalOps:F0})");
        var combinedNote = string.IsNullOrWhiteSpace(note) ? ioDetail : string.Concat(note, "; ", ioDetail);

        var metrics = new BenchMetrics(
            suite,
            name,
            Rounds,
            iterationsPerRound,
            opsPerSec,
            p50Us,
            p95Us,
            p99Us,
            meanUs,
            cpuUsPerOp,
            allocBytesPerOp,
            workingSetMiB,
            diskBytes,
            ioWritePerOp,
            ioReadPerOp,
            combinedNote);
        AddResult(metrics);
        return metrics;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var position = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    private static void CollectMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string SanitizeName(string name)
    {
        Span<char> buffer = stackalloc char[Math.Min(name.Length, 64)];
        for (var i = 0; i < buffer.Length; i++)
        {
            var ch = name[i];
            buffer[i] = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_';
        }

        return new string(buffer);
    }
}
