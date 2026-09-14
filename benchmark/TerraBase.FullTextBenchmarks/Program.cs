using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ZoneTree;
using ZoneTree.FullTextSearch.SearchEngines;
using ZoneTree.Options;

var options = BenchmarkOptions.Parse(args);
var benchmarkAssembly = Assembly.GetExecutingAssembly();
var zoneTreeAssembly = typeof(HashedSearchEngine<>).Assembly;
var zoneTreeAssemblyName = zoneTreeAssembly.GetName().Name;
if (!string.Equals(zoneTreeAssemblyName, "TerraBase.ZoneTree", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        $"The FullTextSearch benchmark must load TerraBase.ZoneTree, but loaded {zoneTreeAssemblyName ?? "<unknown>"}.");
}

var environment = new
{
    Runtime = Environment.Version.ToString(),
    Framework = RuntimeInformation.FrameworkDescription,
    OS = RuntimeInformation.OSDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    Processors = Environment.ProcessorCount,
    AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
    ServerGc = GCSettings.IsServerGC,
    GcLatencyMode = GCSettings.LatencyMode.ToString(),
    BenchmarkAssembly = benchmarkAssembly.Location,
    ArtifactSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(benchmarkAssembly.Location))).ToLowerInvariant(),
    TerraBaseZoneTreeAssembly = zoneTreeAssembly.Location,
    TerraBaseZoneTreeAssemblyName = zoneTreeAssemblyName,
    TerraBaseZoneTreeAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zoneTreeAssembly.Location))).ToLowerInvariant(),
};

Console.WriteLine(JsonSerializer.Serialize(environment));
Console.WriteLine($"configuration records={options.Records} tokens={options.Tokens} samples={options.Samples} warmup={options.Warmup} operations={options.Operations} seed={options.Seed}");

foreach (var compression in options.Compressions)
{
    var root = Path.Combine(Path.GetTempPath(), $"terrabase-fulltext-bench-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        using var engine = CreateEngine(Path.Combine(root, "index"), compression);
        var texts = CreateTexts(options);
        for (var i = 0; i < options.Records; i++)
        {
            engine.AddRecord((ulong)i + 1, texts[i]);
        }

        var add = MeasureAdd(engine, texts, options);
        Console.WriteLine($"compression={compression} operation=AddRecord {add}");

        engine.Index.WaitForBackgroundThreads();
        var flush = MeasureFlush(engine, root);
        Console.WriteLine($"compression={compression} flush_ms={flush.Elapsed.TotalMilliseconds:F3} files_bytes={flush.Bytes:N0}");

        var queryResult = engine.SimpleSearch("token1 token2", respectTokenOrder: false);
        Console.WriteLine($"compression={compression} query_result_count={queryResult.Length}");
        var search = MeasureSearch(engine, options);
        Console.WriteLine($"compression={compression} operation=SimpleSearch {search}");
    }
    finally
    {
        TryDelete(root);
    }
}

static HashedSearchEngine<ulong> CreateEngine(string path, CompressionMethod compression)
{
    var advanced = new AdvancedZoneTreeOptions<ulong, ulong>
    {
        FactoryConfigurator1 = factory => factory.ConfigureDiskSegmentOptions(options =>
        {
            options.CompressionMethod = compression;
            options.CompressionLevel = compression == CompressionMethod.Zstd ? CompressionLevels.Zstd0 : 0;
        }),
        FactoryConfigurator2 = factory => factory.ConfigureDiskSegmentOptions(options =>
        {
            options.CompressionMethod = compression;
            options.CompressionLevel = compression == CompressionMethod.Zstd ? CompressionLevels.Zstd0 : 0;
        }),
    };
    return new HashedSearchEngine<ulong>(path, useSecondaryIndex: true, advancedOptions: advanced);
}

static string[] CreateTexts(BenchmarkOptions options)
{
    var random = new Random(options.Seed);
    var texts = new string[options.Records + options.Operations];
    for (var i = 0; i < texts.Length; i++)
    {
        var tokens = new string[options.Tokens];
        for (var token = 0; token < tokens.Length; token++)
        {
            tokens[token] = $"token{random.Next(0, options.Records / 4 + 1)}";
        }

        texts[i] = string.Join(' ', tokens);
    }

    return texts;
}

static Measurement MeasureAdd(HashedSearchEngine<ulong> engine, string[] texts, BenchmarkOptions options)
{
    var nextRecord = (ulong)options.Records + 1;
    for (var i = 0; i < options.Warmup; i++)
    {
        engine.AddRecord(nextRecord++, texts[options.Records + (i % options.Operations)]);
    }

    return Measure(options.Samples, options.Operations, () =>
    {
        engine.AddRecord(nextRecord++, texts[options.Records + (int)(nextRecord % (ulong)options.Operations)]);
    });
}

static Measurement MeasureSearch(HashedSearchEngine<ulong> engine, BenchmarkOptions options)
{
    for (var i = 0; i < options.Warmup; i++)
    {
        var result = engine.SimpleSearch("token1 token2", respectTokenOrder: false);
        GC.KeepAlive(result);
    }

    return Measure(options.Samples, options.Operations, () =>
    {
        var result = engine.SimpleSearch("token1 token2", respectTokenOrder: false);
        GC.KeepAlive(result);
    });
}

static Measurement Measure(int samples, int operations, Action action)
{
    var measurements = new List<Sample>(samples);
    for (var sample = 0; sample < samples; sample++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timings = new double[operations];
        var stopwatch = Stopwatch.StartNew();
        for (var operation = 0; operation < operations; operation++)
        {
            var started = Stopwatch.GetTimestamp();
            action();
            timings[operation] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }

        stopwatch.Stop();
        measurements.Add(new Sample(
            stopwatch.Elapsed.TotalMilliseconds,
            Percentile(timings, 0.50),
            Percentile(timings, 0.95),
            Percentile(timings, 0.99),
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
    }

    return new Measurement(
        Median(measurements.Select(static sample => sample.ElapsedMilliseconds)),
        Median(measurements.Select(static sample => sample.P50Microseconds)),
        Median(measurements.Select(static sample => sample.P95Microseconds)),
        Median(measurements.Select(static sample => sample.P99Microseconds)),
        Median(measurements.Select(sample => sample.AllocatedBytes / (double)operations)));
}

static FlushMeasurement MeasureFlush(HashedSearchEngine<ulong> engine, string root)
{
    var started = Stopwatch.StartNew();
    engine.Index.EvictToDisk();
    engine.Index.WaitForBackgroundThreads();
    started.Stop();
    var bytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length);
    return new FlushMeasurement(started.Elapsed, bytes);
}

static double Percentile(double[] values, double percentile)
{
    Array.Sort(values);
    var position = (values.Length - 1) * percentile;
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);
    return values[lower] + (values[upper] - values[lower]) * (position - lower);
}

static double Median(IEnumerable<double> values)
{
    var ordered = values.Order().ToArray();
    return Percentile(ordered, 0.5);
}

static void TryDelete(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException)
    {
    }
}

file sealed record Sample(
    double ElapsedMilliseconds,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    long AllocatedBytes);

file sealed record Measurement(
    double ElapsedMilliseconds,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double AllocatedBytesPerOperation)
{
    public override string ToString() =>
        $"samples=5 elapsed_ms={ElapsedMilliseconds:F3} p50_us={P50Microseconds:F3} p95_us={P95Microseconds:F3} p99_us={P99Microseconds:F3} allocated_bytes_op={AllocatedBytesPerOperation:F1}";
}

file sealed record FlushMeasurement(TimeSpan Elapsed, long Bytes);

file sealed record BenchmarkOptions(
    int Records,
    int Tokens,
    int Samples,
    int Warmup,
    int Operations,
    int Seed,
    IReadOnlyList<CompressionMethod> Compressions)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var options = new BenchmarkOptions(2_000, 12, 5, 100, 200, 1337, [CompressionMethod.Zstd, CompressionMethod.None]);
        for (var index = 0; index < args.Length; index++)
        {
            var (name, value) = ReadOption(args, ref index);
            var parsed = name.Equals("compression", StringComparison.OrdinalIgnoreCase)
                ? 0
                : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            options = name switch
            {
                "records" => options with { Records = parsed },
                "tokens" => options with { Tokens = parsed },
                "samples" => options with { Samples = parsed },
                "warmup" => options with { Warmup = parsed },
                "operations" => options with { Operations = parsed },
                "seed" => options with { Seed = parsed },
                "compression" => options with { Compressions = ParseCompressions(value) },
                _ => throw new ArgumentException($"Unknown option '--{name}'."),
            };
        }

        if (options.Records < 1 || options.Tokens < 1 || options.Samples < 5 || options.Warmup < 0 || options.Operations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "records/tokens/operations must be positive, samples must be at least five, and warmup cannot be negative.");
        }

        return options;
    }

    private static IReadOnlyList<CompressionMethod> ParseCompressions(string value) =>
        value.Equals("zstd", StringComparison.OrdinalIgnoreCase)
            ? [CompressionMethod.Zstd]
            : value.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? [CompressionMethod.None]
                : throw new ArgumentException("--compression must be zstd or none.");

    private static (string Name, string Value) ReadOption(string[] args, ref int index)
    {
        var argument = args[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Expected an option, got '{argument}'.");
        }

        var separator = argument.IndexOf('=');
        if (separator > 2)
        {
            return (argument[2..separator], argument[(separator + 1)..]);
        }

        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Option '{argument}' requires a value.");
        }

        return (argument[2..], args[++index]);
    }
}
