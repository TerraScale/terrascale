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
    string Note = "",
    double P999Us = 0,
    double MaxUs = 0,
    double StdDevUs = 0,
    double JitterRatio = 0,
    double RoundOpsCvPct = 0,
    double KernelCpuUsPerOp = 0,
    double OpsPerCpuSec = 0,
    double CpuUtilizationPct = 0,
    double Gen0Per1kOps = 0,
    double Gen1Per1kOps = 0,
    double Gen2Per1kOps = 0,
    double GcPausePct = 0,
    double ManagedHeapMiB = 0,
    double NativeRssMiB = 0,
    int OpenFdCount = 0,
    int OpenFdDelta = 0,
    double IoSyscallReadBytesPerOp = 0,
    double IoSyscallWriteBytesPerOp = 0,
    double IoPhysicalReadBytesPerOp = 0,
    double IoPhysicalWriteBytesPerOp = 0,
    int UnitsPerOp = 1,
    string UnitLabel = "");

internal sealed record BenchReport(
    DateTimeOffset TimestampUtc,
    string MachineName,
    string DotNetVersion,
    string OsDescription,
    int ProcessorCount,
    string NativeSqliteVersion,
    int Rounds,
    double IterationsScale,
    IReadOnlyList<BenchMetrics> Metrics,
    int SchemaVersion = 1,
    string GitSha = "",
    string Label = "",
    string CpuModel = "",
    bool GcServerMode = false,
    double TotalWallSeconds = 0);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchReport))]
[JsonSerializable(typeof(BenchMetrics))]
[JsonSerializable(typeof(BenchComparisonReport))]
[JsonSerializable(typeof(BenchCompareColumn))]
[JsonSerializable(typeof(BenchCompareRow))]
[JsonSerializable(typeof(BenchCohortSummary))]
internal sealed partial class BenchJsonContext : JsonSerializerContext;

public readonly record struct ProcIoSnapshot(long ReadChars, long WriteChars, long ReadBytes, long WriteBytes);

public sealed class BenchRunnerContext : IDisposable
{
    private const double BytesPerMiB = 1024.0 * 1024.0;

    private readonly HashSet<string> _selectedSuites;
    private readonly string[] _benchmarkFilters;
    private readonly bool _runAll;
    private readonly string _rootDirectory;
    private readonly List<string> _runDirectories = [];
    private readonly List<BenchMetrics> _results = [];
    private readonly ResourceSampler _sampler = new();
    private readonly CancellationToken _cancellationToken;

    public BenchRunnerContext(
        IEnumerable<string> suites,
        int rounds,
        double iterationsScale = 1.0,
        IEnumerable<string>? benchmarkFilters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suites);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rounds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterationsScale);

        Rounds = rounds;
        IterationsScale = iterationsScale;
        _cancellationToken = cancellationToken;
        _selectedSuites = new HashSet<string>(suites, StringComparer.OrdinalIgnoreCase);
        _runAll = _selectedSuites.Count == 0 || _selectedSuites.Contains("all");
        _benchmarkFilters = benchmarkFilters is null
            ? []
            : [.. benchmarkFilters.Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item.Trim())];
        _rootDirectory = Path.Combine(Path.GetTempPath(), string.Concat("terrabase-bench-", Ulid.NewUlid().ToString()));
        _ = Directory.CreateDirectory(_rootDirectory);
    }

    public int Rounds { get; }

    public double IterationsScale { get; }

    public IReadOnlyList<BenchMetrics> Results => _results;

    public bool ShouldRun(string suite) => _runAll || _selectedSuites.Contains(suite);

    public bool ShouldRunBenchmark(string suite, string name)
    {
        if (!ShouldRun(suite))
        {
            return false;
        }

        if (_benchmarkFilters.Length == 0)
        {
            return true;
        }

        for (var i = 0; i < _benchmarkFilters.Length; i++)
        {
            if (MatchesFilter(_benchmarkFilters[i], suite, name))
            {
                return true;
            }
        }

        return false;
    }

    public bool HasMatchingBenchmark(string suite, params ReadOnlySpan<string> names)
    {
        if (!ShouldRun(suite))
        {
            return false;
        }

        if (_benchmarkFilters.Length == 0)
        {
            return true;
        }

        for (var i = 0; i < names.Length; i++)
        {
            if (ShouldRunBenchmark(suite, names[i]))
            {
                return true;
            }
        }

        return false;
    }

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

    public ResourceSnapshot CaptureSnapshot() => _sampler.CaptureSnapshot();

    public void AddResult(BenchMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _results.Add(metrics);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[{metrics.Suite}] {metrics.Name,-38} {metrics.OpsPerSec,10:F1} ops/s ({metrics.OpsPerCpuSec,10:F1} /cpu-s) | p50={metrics.P50Us,8:F1}us p99={metrics.P99Us,8:F1}us p999={metrics.P999Us,8:F1}us | alloc={metrics.AllocBytesPerOp,8:F0}B gc={metrics.Gen0Per1kOps:F1}/{metrics.Gen1Per1kOps:F1}/{metrics.Gen2Per1kOps:F1} | heap/nat={metrics.ManagedHeapMiB:F1}/{metrics.NativeRssMiB:F1}MiB fds={metrics.OpenFdCount}({metrics.OpenFdDelta:+#;-#;0}) disk={metrics.DiskBytes,8}B{(metrics.Note.Length > 0 ? string.Concat(" | ", metrics.Note) : string.Empty)}"));
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
        string note = "",
        double managedHeapMiB = 0,
        double nativeRssMiB = 0,
        int openFdCount = 0,
        int openFdDelta = 0,
        int unitsPerOp = 1,
        string unitLabel = "")
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
            note,
            ManagedHeapMiB: managedHeapMiB,
            NativeRssMiB: nativeRssMiB,
            OpenFdCount: openFdCount,
            OpenFdDelta: openFdDelta,
            UnitsPerOp: unitsPerOp,
            UnitLabel: unitLabel);
        AddResult(metrics);
        return metrics;
    }

    public Task<BenchMetrics> MeasureAsync(
        string suite,
        string name,
        int warmup,
        int iterations,
        Func<int, ValueTask> body,
        Func<long>? diskBytes = null,
        string note = "",
        CancellationToken cancellationToken = default) =>
        MeasureAsync(suite, name, warmup, iterations, body, unitsPerOp: 1, unitLabel: string.Empty, diskBytes, note, cancellationToken);

    public async Task<BenchMetrics> MeasureAsync(
        string suite,
        string name,
        int warmup,
        int iterations,
        Func<int, ValueTask> body,
        int unitsPerOp,
        string unitLabel,
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

        var accumulator = new RoundAccumulator(Rounds, scaledIters);

        for (var round = 0; round < Rounds; round++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            var (before, roundStart) = accumulator.BeginRound(_sampler, round);

            for (var i = 0; i < scaledIters; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                var opStart = Stopwatch.GetTimestamp();
                await body(opIndex++);
                accumulator.RecordLatency(Stopwatch.GetElapsedTime(opStart).TotalMicroseconds);
            }

            accumulator.EndRound(_sampler, before, roundStart, round, scaledIters);
        }

        var metrics = accumulator.FinalizeMetrics(
            suite,
            name,
            Rounds,
            scaledIters,
            diskBytes is not null ? diskBytes() : 0L,
            note,
            unitsPerOp,
            unitLabel);
        AddResult(metrics);
        return metrics;
    }

    public BenchMetrics MeasureSync(
        string suite,
        string name,
        int warmup,
        int iterations,
        Action<int> body,
        Func<long>? diskBytes = null,
        string note = "",
        CancellationToken cancellationToken = default) =>
        MeasureSync(suite, name, warmup, iterations, body, unitsPerOp: 1, unitLabel: string.Empty, diskBytes, note, cancellationToken);

    public BenchMetrics MeasureSync(
        string suite,
        string name,
        int warmup,
        int iterations,
        Action<int> body,
        int unitsPerOp,
        string unitLabel,
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

        var accumulator = new RoundAccumulator(Rounds, scaledIters);

        for (var round = 0; round < Rounds; round++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            var (before, roundStart) = accumulator.BeginRound(_sampler, round);

            for (var i = 0; i < scaledIters; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                var opStart = Stopwatch.GetTimestamp();
                body(opIndex++);
                accumulator.RecordLatency(Stopwatch.GetElapsedTime(opStart).TotalMicroseconds);
            }

            accumulator.EndRound(_sampler, before, roundStart, round, scaledIters);
        }

        var metrics = accumulator.FinalizeMetrics(
            suite,
            name,
            Rounds,
            scaledIters,
            diskBytes is not null ? diskBytes() : 0L,
            note,
            unitsPerOp,
            unitLabel);
        AddResult(metrics);
        return metrics;
    }

    public long ReadWorkingSetBytes() => _sampler.ReadWorkingSetBytes();

    public static long ReadRssBytes() => ResourceSampler.ReadRssBytesStatic();

    public static int ReadOpenFdCount() => ResourceSampler.ReadOpenFdCount();

    public static ProcIoSnapshot ReadProcIo() => ResourceSampler.ReadProcIoStatic();

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
        Console.WriteLine("| Suite | Benchmark | Ops/s | Ops/CPU-s | P50 (us) | P95 (us) | P99 (us) | P999 (us) | CPU (us/op) | Alloc (B/op) | GC (0/1/2) | Heap/Nat (MiB) | FDs (delta) | Disk (B) | IO W/R (B/op) | Note |");
        Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var item in results)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {item.Suite} | {item.Name} | {item.OpsPerSec:F1} | {item.OpsPerCpuSec:F1} | {item.P50Us:F1} | {item.P95Us:F1} | {item.P99Us:F1} | {item.P999Us:F1} | {item.CpuUsPerOp:F1} | {item.AllocBytesPerOp:F0} | {item.Gen0Per1kOps:F1}/{item.Gen1Per1kOps:F1}/{item.Gen2Per1kOps:F1} | {item.ManagedHeapMiB:F1}/{item.NativeRssMiB:F1} | {item.OpenFdCount}({item.OpenFdDelta:+#;-#;0}) | {item.DiskBytes} | {item.IoWriteBytesPerOp:F0}/{item.IoReadBytesPerOp:F0} | {item.Note} |"));
        }
    }

    public void Dispose()
    {
        _sampler.Dispose();
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private static bool MatchesFilter(string filter, string suite, string name)
    {
        if (filter.Contains('*', StringComparison.Ordinal))
        {
            var qualified = filter.Contains('/', StringComparison.Ordinal)
                ? string.Concat(suite, "/", name)
                : name;
            return WildcardMatches(filter, qualified);
        }

        if (filter.Contains('/', StringComparison.Ordinal))
        {
            return string.Equals(filter, string.Concat(suite, "/", name), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(filter, name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WildcardMatches(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        var p = 0;
        var t = 0;
        var starPattern = -1;
        var starText = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starText = t;
            }
            else if (starPattern >= 0)
            {
                p = starPattern + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
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

    private sealed class RoundAccumulator(int rounds, int iterationsPerRound)
    {
        private readonly double[] _latenciesUs = new double[rounds * iterationsPerRound];
        private readonly double[] _roundOpsPerSec = new double[rounds];
        private double _totalElapsedSeconds;
        private double _totalCpuUs;
        private double _totalKernelCpuUs;
        private long _totalAllocBytes;
        private long _totalGen0;
        private long _totalGen1;
        private long _totalGen2;
        private double _totalGcPauseSeconds;
        private long _peakRssBytes;
        private long _peakManagedHeapBytes;
        private long _peakNativeRssBytes;
        private int _firstOpenFdCount;
        private int _lastOpenFdCount;
        private long _totalReadChars;
        private long _totalWriteChars;
        private long _totalReadBytes;
        private long _totalWriteBytes;
        private int _sampleCursor;

        public (ResourceSnapshot Before, long StartTimestamp) BeginRound(ResourceSampler sampler, int roundIndex)
        {
            CollectMemory();
            var before = sampler.CaptureBeforeRound(out var startTimestamp);
            if (roundIndex == 0)
            {
                _firstOpenFdCount = before.OpenFdCount;
            }

            return (before, startTimestamp);
        }

        public void RecordLatency(double elapsedUs) => _latenciesUs[_sampleCursor++] = elapsedUs;

        public void EndRound(ResourceSampler sampler, ResourceSnapshot before, long roundStartTimestamp, int roundIndex, int iterationsInRound)
        {
            var after = sampler.CaptureAfterRound(out var endTimestamp);
            var roundElapsed = Stopwatch.GetElapsedTime(roundStartTimestamp, endTimestamp).TotalSeconds;

            _totalElapsedSeconds += roundElapsed;
            _roundOpsPerSec[roundIndex] = roundElapsed > 0 ? iterationsInRound / roundElapsed : 0;
            _totalCpuUs += Math.Max(0, (after.TotalCpu - before.TotalCpu).TotalMicroseconds);
            _totalKernelCpuUs += Math.Max(0, (after.KernelCpu - before.KernelCpu).TotalMicroseconds);
            _totalAllocBytes += Math.Max(0, after.TotalAllocatedBytes - before.TotalAllocatedBytes);
            _totalGen0 += Math.Max(0, after.Gen0Count - before.Gen0Count);
            _totalGen1 += Math.Max(0, after.Gen1Count - before.Gen1Count);
            _totalGen2 += Math.Max(0, after.Gen2Count - before.Gen2Count);
            _totalGcPauseSeconds += Math.Max(0, (after.GcPauseDuration - before.GcPauseDuration).TotalSeconds);
            _peakRssBytes = Math.Max(_peakRssBytes, after.RssBytes);
            _peakManagedHeapBytes = Math.Max(_peakManagedHeapBytes, after.ManagedHeapBytes);
            _peakNativeRssBytes = Math.Max(_peakNativeRssBytes, Math.Max(0L, after.RssBytes - after.ManagedHeapBytes));
            _lastOpenFdCount = after.OpenFdCount;
            _totalReadChars += Math.Max(0, after.Io.ReadChars - before.Io.ReadChars);
            _totalWriteChars += Math.Max(0, after.Io.WriteChars - before.Io.WriteChars);
            _totalReadBytes += Math.Max(0, after.Io.ReadBytes - before.Io.ReadBytes);
            _totalWriteBytes += Math.Max(0, after.Io.WriteBytes - before.Io.WriteBytes);
        }

        public BenchMetrics FinalizeMetrics(
            string suite,
            string name,
            int totalRounds,
            int scaledIters,
            long diskBytes,
            string note,
            int unitsPerOp,
            string unitLabel)
        {
            Array.Sort(_latenciesUs);
            var totalOps = _latenciesUs.Length;
            var sumUs = 0.0;
            for (var i = 0; i < totalOps; i++)
            {
                sumUs += _latenciesUs[i];
            }

            var meanUs = sumUs / totalOps;
            var sumSqDiff = 0.0;
            for (var i = 0; i < totalOps; i++)
            {
                var diff = _latenciesUs[i] - meanUs;
                sumSqDiff += diff * diff;
            }

            var stdDevUs = totalOps > 1 ? Math.Sqrt(sumSqDiff / (totalOps - 1)) : 0.0;
            var p50Us = Percentile(_latenciesUs, 0.50);
            var p95Us = Percentile(_latenciesUs, 0.95);
            var p99Us = Percentile(_latenciesUs, 0.99);
            var p999Us = Percentile(_latenciesUs, 0.999);
            var maxUs = totalOps > 0 ? _latenciesUs[^1] : 0.0;
            var jitterRatio = p50Us > 0 ? p99Us / p50Us : 0.0;
            var roundOpsCvPct = ComputeCvPct(_roundOpsPerSec);

            var opsPerSec = _totalElapsedSeconds > 0 ? totalOps / _totalElapsedSeconds : 0.0;
            var cpuUsPerOp = _totalCpuUs / totalOps;
            var kernelCpuUsPerOp = _totalKernelCpuUs / totalOps;
            var opsPerCpuSec = _totalCpuUs > 0 ? totalOps / (_totalCpuUs / 1_000_000.0) : 0.0;
            var cpuUtilizationPct = _totalElapsedSeconds > 0 ? (_totalCpuUs / (_totalElapsedSeconds * 1_000_000.0)) * 100.0 : 0.0;
            var allocBytesPerOp = (double)_totalAllocBytes / totalOps;
            var gen0Per1k = _totalGen0 * 1000.0 / totalOps;
            var gen1Per1k = _totalGen1 * 1000.0 / totalOps;
            var gen2Per1k = _totalGen2 * 1000.0 / totalOps;
            var gcPausePct = _totalElapsedSeconds > 0 ? (_totalGcPauseSeconds / _totalElapsedSeconds) * 100.0 : 0.0;

            var workingSetMiB = _peakRssBytes / BytesPerMiB;
            var managedHeapMiB = _peakManagedHeapBytes / BytesPerMiB;
            var nativeRssMiB = _peakNativeRssBytes / BytesPerMiB;
            var openFdDelta = _lastOpenFdCount - _firstOpenFdCount;

            var ioSyscallReadPerOp = (double)_totalReadChars / totalOps;
            var ioSyscallWritePerOp = (double)_totalWriteChars / totalOps;
            var ioPhysicalReadPerOp = (double)_totalReadBytes / totalOps;
            var ioPhysicalWritePerOp = (double)_totalWriteBytes / totalOps;
            var ioWritePerOp = (double)(_totalWriteBytes > 0 ? _totalWriteBytes : _totalWriteChars) / totalOps;
            var ioReadPerOp = (double)(_totalReadBytes > 0 ? _totalReadBytes : _totalReadChars) / totalOps;

            var ioDetail = string.Create(
                CultureInfo.InvariantCulture,
                $"io(rchar={ioSyscallReadPerOp:F0},wchar={ioSyscallWritePerOp:F0},read_b={ioPhysicalReadPerOp:F0},write_b={ioPhysicalWritePerOp:F0})");
            var combinedNote = string.IsNullOrWhiteSpace(note) ? ioDetail : string.Concat(note, "; ", ioDetail);

            return new BenchMetrics(
                suite,
                name,
                totalRounds,
                scaledIters,
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
                combinedNote,
                p999Us,
                maxUs,
                stdDevUs,
                jitterRatio,
                roundOpsCvPct,
                kernelCpuUsPerOp,
                opsPerCpuSec,
                cpuUtilizationPct,
                gen0Per1k,
                gen1Per1k,
                gen2Per1k,
                gcPausePct,
                managedHeapMiB,
                nativeRssMiB,
                _lastOpenFdCount,
                openFdDelta,
                ioSyscallReadPerOp,
                ioSyscallWritePerOp,
                ioPhysicalReadPerOp,
                ioPhysicalWritePerOp,
                unitsPerOp,
                unitLabel);
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

        private static double ComputeCvPct(double[] values)
        {
            if (values.Length <= 1)
            {
                return 0.0;
            }

            var sum = 0.0;
            for (var i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }

            var mean = sum / values.Length;
            if (mean <= 0)
            {
                return 0.0;
            }

            var sumSq = 0.0;
            for (var i = 0; i < values.Length; i++)
            {
                var diff = values[i] - mean;
                sumSq += diff * diff;
            }

            return (Math.Sqrt(sumSq / (values.Length - 1)) / mean) * 100.0;
        }

        private static void CollectMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
