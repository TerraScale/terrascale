using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZoneTree.Compression;
using ZoneTree.Options;

/// <summary>
/// Measures the primitive compression boundary used by ZoneTree.
/// </summary>
/// <remarks>
/// This is deliberately separate from the provider workloads in <c>Program</c>.
/// It measures DataCompression.Compress and DataCompression.Decompress directly;
/// it does not claim to measure WAL, segment, or end-to-end database throughput.
/// </remarks>
internal static class CompressionWorkload
{
    private const int SmallDocumentBytes = 4 * 1024;
    private const int LargeDocumentBytes = 256 * 1024;
    private const int IncompressibleBlockBytes = 4 * 1024 * 1024;
    private const string DefaultDurability = "in-memory primitive; no WAL or segment I/O";
    private static long blackHole;

    public static bool IsRequested(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--workload=", StringComparison.OrdinalIgnoreCase))
            {
                return argument["--workload=".Length..].Equals("compression", StringComparison.OrdinalIgnoreCase);
            }

            if (argument.Equals("--workload", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                return args[index + 1].Equals("compression", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var options = CompressionOptions.Parse(args);
        if (options.ShowHelp)
        {
            CompressionOptions.PrintHelp();
            return 0;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancelHandler = null;
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var environment = CompressionEnvironment.Create();
            var receipt = await RunBenchmarkAsync(
                options,
                outputDirectory,
                environment,
                cancellation.Token).ConfigureAwait(false);
            var receiptPath = Path.Combine(outputDirectory, "compression-receipt.json");
            await File.WriteAllTextAsync(
                receiptPath,
                JsonSerializer.Serialize(receipt, JsonOptions),
                CancellationToken.None).ConfigureAwait(false);

            var markdownPath = Path.Combine(outputDirectory, "compression-summary.md");
            await File.WriteAllTextAsync(
                markdownPath,
                CompressionMarkdown.Create(receipt, receiptPath),
                CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"Compression receipt: {receiptPath}");
            Console.WriteLine($"Compression summary: {markdownPath}");
            return receipt.Status == "complete" ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<CompressionReceipt> RunBenchmarkAsync(
        CompressionOptions options,
        string outputDirectory,
        CompressionEnvironment environment,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var rawPath = Path.Combine(outputDirectory, "compression-raw-samples.jsonl");
        var cases = new List<CompressionCaseResult>();
        await using var raw = new StreamWriter(
            rawPath,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            foreach (var profile in PayloadProfile.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = CreatePayload(profile, options.Seed);
                Console.WriteLine($"Running compression {profile.Name} ({profile.SizeBytes:N0} bytes)...");

                foreach (var method in new[] { CompressionMethod.LZ4, CompressionMethod.None })
                {
                    var result = await RunCaseAsync(
                        options,
                        profile,
                        method,
                        payload,
                        raw,
                        cancellationToken).ConfigureAwait(false);
                    cases.Add(result);
                    Console.WriteLine(
                        $"  {method}: {result.Status}; compressed {result.CompressedBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "—"} bytes");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cases.Add(new CompressionCaseResult
            {
                Profile = "benchmark",
                Method = "LZ4 vs None",
                Status = "failed",
                Error = "Cancelled by user.",
            });
        }
        catch (Exception exception)
        {
            cases.Add(new CompressionCaseResult
            {
                Profile = "benchmark",
                Method = "LZ4 vs None",
                Status = "failed",
                Error = exception.ToString(),
            });
        }

        return new CompressionReceipt
        {
            Status = cases.Count != 0 && cases.All(static result => result.Status == "complete")
                ? "complete"
                : "failed",
            StartedAtUtc = startedAt,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            Configuration = options,
            Environment = environment,
            Cases = cases,
            Command = Environment.CommandLine,
            ReceiptDirectory = outputDirectory,
            RawSamplesPath = rawPath,
        };
    }

    private static async Task<CompressionCaseResult> RunCaseAsync(
        CompressionOptions options,
        PayloadProfile profile,
        CompressionMethod method,
        Memory<byte> payload,
        StreamWriter raw,
        CancellationToken cancellationToken)
    {
        var result = new CompressionCaseResult
        {
            Profile = profile.Name,
            PayloadBytes = payload.Length,
            Method = method.ToString(),
            CompressionLevel = options.CompressionLevel,
        };

        try
        {
            // The one-time round trip check only establishes that the measured
            // input is usable. Corruption and malformed-frame behavior belongs
            // to CompressionIntegrityTests, not this performance harness.
            var compressed = DataCompression.Compress(method, options.CompressionLevel, payload).ToArray();
            var restored = DataCompression.Decompress(method, compressed, payload.Length);
            if (!restored.Span.SequenceEqual(payload.Span))
            {
                throw new InvalidDataException($"{method} did not restore the {profile.Name} payload.");
            }

            result.CompressedBytes = compressed.Length;
            result.CompressionRatio = compressed.Length / (double)payload.Length;
            result.Compression = await MeasureOperationAsync(
                options,
                profile,
                method,
                CompressionOperation.Compress,
                payload,
                compressed,
                raw,
                cancellationToken).ConfigureAwait(false);
            result.Decompression = await MeasureOperationAsync(
                options,
                profile,
                method,
                CompressionOperation.Decompress,
                payload,
                compressed,
                raw,
                cancellationToken).ConfigureAwait(false);
            result.Status = "complete";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Status = "failed";
            result.Error = "Cancelled by user.";
        }
        catch (Exception exception)
        {
            result.Status = "failed";
            result.Error = exception.ToString();
        }

        return result;
    }

    private static async Task<CompressionOperationResult> MeasureOperationAsync(
        CompressionOptions options,
        PayloadProfile profile,
        CompressionMethod method,
        CompressionOperation operation,
        Memory<byte> payload,
        Memory<byte> compressed,
        StreamWriter raw,
        CancellationToken cancellationToken)
    {
        var result = new CompressionOperationResult
        {
            Operation = operation.ToString(),
        };
        var cold = MeasureCold(
            options,
            method,
            operation,
            payload,
            compressed,
            cancellationToken);
        result.Cold = cold;
        await WriteRawAsync(raw, profile, method, operation, cold, cancellationToken).ConfigureAwait(false);

        await RunBatchAsync(
            options,
            operation,
            method,
            payload,
            compressed,
            latencies: null,
            cancellationToken).ConfigureAwait(false);

        for (var sampleIndex = 1; sampleIndex <= options.Samples; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = await MeasureWarmSampleAsync(
                options,
                operation,
                method,
                payload,
                compressed,
                sampleIndex,
                cancellationToken).ConfigureAwait(false);
            result.WarmSamples.Add(sample);
            await WriteRawAsync(raw, profile, method, operation, sample, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(
                $"    {operation} warm sample {sampleIndex}/{options.Samples}: {sample.ThroughputOpsPerSecond.ToString("F1", CultureInfo.InvariantCulture)} ops/s");
        }

        return result;
    }

    private static CompressionSample MeasureCold(
        CompressionOptions options,
        CompressionMethod method,
        CompressionOperation operation,
        Memory<byte> payload,
        Memory<byte> compressed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(false);
        var started = Stopwatch.GetTimestamp();
        var output = Execute(operation, method, options.CompressionLevel, payload, compressed);
        Volatile.Write(ref blackHole, output.Length);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocations = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocationsBefore);
        var latencyMicroseconds = ToMicroseconds(elapsed);
        return new CompressionSample
        {
            Phase = "cold",
            Sample = 0,
            Operations = 1,
            Concurrency = 1,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            ThroughputOpsPerSecond = 1d / Math.Max(elapsed.TotalSeconds, double.Epsilon),
            P50Microseconds = latencyMicroseconds,
            P95Microseconds = latencyMicroseconds,
            P99Microseconds = latencyMicroseconds,
            AllocatedBytes = allocations,
            AllocatedBytesPerOperation = allocations,
            ProcessCpuMilliseconds = cpu.TotalMilliseconds,
            ProcessCpuPercent = CpuPercent(cpu, elapsed),
            OutputBytes = output.Length,
        };
    }

    private static async Task<CompressionSample> MeasureWarmSampleAsync(
        CompressionOptions options,
        CompressionOperation operation,
        CompressionMethod method,
        Memory<byte> payload,
        Memory<byte> compressed,
        int sampleIndex,
        CancellationToken cancellationToken)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(false);
        var latencies = new long[options.OperationsPerSample];
        var started = Stopwatch.GetTimestamp();
        await RunBatchAsync(
            options,
            operation,
            method,
            payload,
            compressed,
            latencies,
            cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocations = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocationsBefore);
        Array.Sort(latencies);
        return new CompressionSample
        {
            Phase = "warm",
            Sample = sampleIndex,
            Operations = options.OperationsPerSample,
            Concurrency = options.Concurrency,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            ThroughputOpsPerSecond = options.OperationsPerSample / Math.Max(elapsed.TotalSeconds, double.Epsilon),
            P50Microseconds = ToMicroseconds(Percentile(latencies, 0.50)),
            P95Microseconds = ToMicroseconds(Percentile(latencies, 0.95)),
            P99Microseconds = ToMicroseconds(Percentile(latencies, 0.99)),
            AllocatedBytes = allocations,
            AllocatedBytesPerOperation = allocations / (double)options.OperationsPerSample,
            ProcessCpuMilliseconds = cpu.TotalMilliseconds,
            ProcessCpuPercent = CpuPercent(cpu, elapsed),
            OutputBytes = Volatile.Read(ref blackHole),
        };
    }

    private static async Task RunBatchAsync(
        CompressionOptions options,
        CompressionOperation operation,
        CompressionMethod method,
        Memory<byte> payload,
        Memory<byte> compressed,
        long[]? latencies,
        CancellationToken cancellationToken)
    {
        var operationCount = latencies?.Length ?? options.WarmupOperations;
        if (operationCount == 0)
        {
            return;
        }

        var workers = Enumerable.Range(0, options.Concurrency)
            .Select(worker => Task.Run(() =>
            {
                for (var index = worker; index < operationCount; index += options.Concurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var started = Stopwatch.GetTimestamp();
                    var output = Execute(operation, method, options.CompressionLevel, payload, compressed);
                    Volatile.Write(ref blackHole, output.Length);
                    if (latencies is not null)
                    {
                        latencies[index] = Stopwatch.GetTimestamp() - started;
                    }
                }
            }, cancellationToken))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private static Memory<byte> Execute(
        CompressionOperation operation,
        CompressionMethod method,
        int compressionLevel,
        Memory<byte> payload,
        Memory<byte> compressed)
    {
        return operation switch
        {
            CompressionOperation.Compress => DataCompression.Compress(method, compressionLevel, payload),
            CompressionOperation.Decompress => DataCompression.Decompress(method, compressed, payload.Length),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static async Task WriteRawAsync(
        StreamWriter raw,
        PayloadProfile profile,
        CompressionMethod method,
        CompressionOperation operation,
        CompressionSample sample,
        CancellationToken cancellationToken)
    {
        var line = new CompressionRawSample
        {
            Profile = profile.Name,
            PayloadBytes = profile.SizeBytes,
            Method = method.ToString(),
            Operation = operation.ToString(),
            Measurement = sample,
        };
        await raw.WriteLineAsync(JsonSerializer.Serialize(line, RawJsonOptions)).ConfigureAwait(false);
        await raw.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Memory<byte> CreatePayload(PayloadProfile profile, int seed)
    {
        return profile.Kind switch
        {
            PayloadKind.Document => CreateDocumentPayload(profile.SizeBytes),
            PayloadKind.IncompressibleBlock => CreateIncompressiblePayload(profile.SizeBytes, seed),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }

    private static byte[] CreateDocumentPayload(int size)
    {
        const string prefix = "{\"tenant\":\"benchmark-tenant\",\"collection\":\"compression\",\"documentId\":\"document-00000001\",\"payload\":\"";
        const string suffix = "\"}";
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var suffixBytes = Encoding.UTF8.GetBytes(suffix);
        if (size < prefixBytes.Length + suffixBytes.Length)
        {
            throw new ArgumentException("The document payload is smaller than its JSON envelope.", nameof(size));
        }

        var payload = new byte[size];
        prefixBytes.CopyTo(payload, 0);
        var body = payload.AsSpan(prefixBytes.Length, size - prefixBytes.Length - suffixBytes.Length);
        var pattern = Encoding.UTF8.GetBytes("terra-base compression benchmark document value; ");
        for (var offset = 0; offset < body.Length; offset += pattern.Length)
        {
            pattern.AsSpan(0, Math.Min(pattern.Length, body.Length - offset)).CopyTo(body[offset..]);
        }

        suffixBytes.CopyTo(payload, size - suffixBytes.Length);
        return payload;
    }

    private static byte[] CreateIncompressiblePayload(int size, int seed)
    {
        var payload = new byte[size];
        Span<byte> input = stackalloc byte[8];
        Span<byte> hash = stackalloc byte[32];
        for (var offset = 0; offset < payload.Length; offset += hash.Length)
        {
            BinaryPrimitives.WriteInt32LittleEndian(input[..4], seed);
            BinaryPrimitives.WriteInt32LittleEndian(input[4..], offset / hash.Length);
            if (!SHA256.TryHashData(input, hash, out var written) || written != hash.Length)
            {
                throw new InvalidOperationException("The deterministic payload generator could not produce a hash block.");
            }

            hash[..Math.Min(hash.Length, payload.Length - offset)].CopyTo(payload.AsSpan(offset));
        }

        return payload;
    }

    private static double CpuPercent(TimeSpan cpu, TimeSpan elapsed)
    {
        return elapsed.TotalSeconds <= 0
            ? 0
            : cpu.TotalSeconds / elapsed.TotalSeconds / Environment.ProcessorCount * 100;
    }

    private static double ToMicroseconds(TimeSpan elapsed) => elapsed.TotalMilliseconds * 1_000;

    private static long Percentile(long[] sortedTicks, double percentile)
    {
        var index = (int)Math.Ceiling(sortedTicks.Length * percentile) - 1;
        return sortedTicks[Math.Clamp(index, 0, sortedTicks.Length - 1)];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions RawJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class CompressionOptions
    {
        public string Workload { get; private set; } = "compression";
        public string Storage { get; private set; } = "Memory";
        public string Durability { get; private set; } = DefaultDurability;
        public int Samples { get; private set; } = 5;
        public int WarmupOperations { get; private set; } = 500;
        public int OperationsPerSample { get; private set; } = 1_000;
        public int Concurrency { get; private set; } = 1;
        public int Seed { get; private set; } = 1337;
        public int CompressionLevel { get; private set; } = 3;
        public string OutputDirectory { get; private set; } = Path.Combine(
            "bench",
            "receipts",
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture));
        public bool ShowHelp { get; private set; }
        public static CompressionOptions Parse(string[] args)
        {
            var options = new CompressionOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var (name, value) = ReadOption(args, ref index, args[index]);
                switch (name)
                {
                    case "help":
                        options.ShowHelp = true;
                        break;
                    case "workload":
                        if (!value.Equals("compression", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException("--workload must be compression when using the compression harness.");
                        }

                        break;
                    case "storage":
                        if (!value.Equals("memory", StringComparison.OrdinalIgnoreCase) &&
                            !value.Equals("storage-engine", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException("--storage must be memory or storage-engine.");
                        }

                        options.Storage = value.Equals("memory", StringComparison.OrdinalIgnoreCase)
                            ? "Memory"
                            : "StorageEngine";
                        options.Durability = DefaultDurability;
                        break;
                    case "samples":
                        options.Samples = ParseInteger(value, name, 5);
                        break;
                    case "warmup":
                        options.WarmupOperations = ParseInteger(value, name, 0);
                        break;
                    case "operations":
                        options.OperationsPerSample = ParseInteger(value, name, 1);
                        break;
                    case "concurrency":
                        options.Concurrency = ParseInteger(value, name, 1);
                        break;
                    case "seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
                        {
                            throw new ArgumentException($"--{name} must be a 32-bit integer.");
                        }

                        options.Seed = seed;
                        break;
                    case "level":
                        options.CompressionLevel = ParseInteger(value, name, 0);
                        if (options.CompressionLevel > 12)
                        {
                            throw new ArgumentException("--level must be between 0 and 12 for LZ4.");
                        }

                        break;
                    case "output":
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            throw new ArgumentException("--output must not be empty.");
                        }

                        options.OutputDirectory = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '--{name}'.");
                }
            }

            return options;
        }

        public static void PrintHelp()
        {
            Console.WriteLine("TerraBase DataCompression benchmark runner");
            Console.WriteLine("Compares NativeCompressions LZ4 with CompressionMethod.None at the primitive boundary.");
            Console.WriteLine("Options:");
            Console.WriteLine("  --workload compression           Run this harness");
            Console.WriteLine("  --storage memory|storage-engine  Retained in metadata; primitive runs in memory");
            Console.WriteLine("  --samples N                      Post-warmup samples, minimum 5 (default: 5)");
            Console.WriteLine("  --warmup N                       Warmup operations per case (default: 500)");
            Console.WriteLine("  --operations N                   Operations per sample (default: 1000)");
            Console.WriteLine("  --concurrency N                  Concurrent workers (default: 1)");
            Console.WriteLine("  --seed N                         Deterministic document/random payload seed (default: 1337)");
            Console.WriteLine("  --level N                        LZ4 compression level 0-12 (default: 3)");
            Console.WriteLine("  --output DIR                     Receipt directory");
        }

        private static (string Name, string Value) ReadOption(string[] args, ref int index, string argument)
        {
            if (argument.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                return ("help", string.Empty);
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Expected an option, got '{argument}'.");
            }

            var separator = argument.IndexOf('=');
            if (separator > 2)
            {
                return (argument[2..separator], argument[(separator + 1)..]);
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{argument}' requires a value.");
            }

            return (argument[2..], args[++index]);
        }

        private static int ParseInteger(string value, string name, int minimum)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
            {
                throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
            }

            return parsed;
        }
    }

    private sealed record PayloadProfile(string Name, int SizeBytes, PayloadKind Kind)
    {
        public static IReadOnlyList<PayloadProfile> All { get; } =
        [
            new("small-document", SmallDocumentBytes, PayloadKind.Document),
            new("large-document", LargeDocumentBytes, PayloadKind.Document),
            new("incompressible-block", IncompressibleBlockBytes, PayloadKind.IncompressibleBlock),
        ];
    }

    private enum PayloadKind
    {
        Document,
        IncompressibleBlock,
    }

    private enum CompressionOperation
    {
        Compress,
        Decompress,
    }

    private sealed class CompressionReceipt
    {
        public string Status { get; init; } = "failed";
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset FinishedAtUtc { get; init; }
        public CompressionOptions Configuration { get; init; } = new();
        public CompressionEnvironment Environment { get; init; } = CompressionEnvironment.Empty;
        public IReadOnlyList<CompressionCaseResult> Cases { get; init; } = [];
        public string Command { get; init; } = string.Empty;
        public string ReceiptDirectory { get; init; } = string.Empty;
        public string RawSamplesPath { get; init; } = string.Empty;
    }

    private sealed class CompressionCaseResult
    {
        public string Profile { get; init; } = string.Empty;
        public int PayloadBytes { get; init; }
        public string Method { get; init; } = string.Empty;
        public int CompressionLevel { get; init; }
        public string Status { get; set; } = "failed";
        public string? Error { get; set; }
        public int? CompressedBytes { get; set; }
        public double? CompressionRatio { get; set; }
        public CompressionOperationResult? Compression { get; set; }
        public CompressionOperationResult? Decompression { get; set; }
    }

    private sealed class CompressionOperationResult
    {
        public string Operation { get; init; } = string.Empty;
        public CompressionSample? Cold { get; set; }
        public List<CompressionSample> WarmSamples { get; } = [];
    }

    private sealed class CompressionSample
    {
        public string Phase { get; init; } = string.Empty;
        public int Sample { get; init; }
        public int Operations { get; init; }
        public int Concurrency { get; init; }
        public double ElapsedMilliseconds { get; init; }
        public double ThroughputOpsPerSecond { get; init; }
        public double P50Microseconds { get; init; }
        public double P95Microseconds { get; init; }
        public double P99Microseconds { get; init; }
        public long AllocatedBytes { get; init; }
        public double AllocatedBytesPerOperation { get; init; }
        public double ProcessCpuMilliseconds { get; init; }
        public double ProcessCpuPercent { get; init; }
        public long OutputBytes { get; init; }
    }

    private sealed class CompressionRawSample
    {
        public string Profile { get; init; } = string.Empty;
        public int PayloadBytes { get; init; }
        public string Method { get; init; } = string.Empty;
        public string Operation { get; init; } = string.Empty;
        public CompressionSample Measurement { get; init; } = new();
    }

    private sealed record CompressionEnvironment(
        DateTimeOffset StartedAtUtc,
        string Runtime,
        string FrameworkDescription,
        string OperatingSystem,
        string Architecture,
        string BuildConfiguration,
        int ProcessorCount,
        long AvailableMemoryBytes,
        string CpuModel,
        bool ServerGc,
        string GcLatencyMode,
        string? GitCommit,
        bool? GitDirty,
        string? ArtifactPath,
        string? ArtifactSha256,
        string? ZoneTreeAssemblyPath,
        string? ZoneTreeAssemblySha256)
    {
        public static CompressionEnvironment Empty { get; } = new(
            DateTimeOffset.MinValue,
            "unknown",
            "unknown",
            "unknown",
            "unknown",
            "unknown",
            0,
            0,
            "unknown",
            false,
            "unknown",
            null,
            null,
            null,
            null,
            null,
            null);

        public static CompressionEnvironment Create()
        {
            var benchmarkAssembly = Assembly.GetExecutingAssembly();
            var artifactPath = benchmarkAssembly.Location;
            var zoneTreeAssembly = typeof(DataCompression).Assembly;
            var zoneTreePath = zoneTreeAssembly.Location;
            var gitRoot = FindGitRoot();
            var cpuModel = ReadCpuModel();
            return new CompressionEnvironment(
                DateTimeOffset.UtcNow,
                Environment.Version.ToString(),
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                benchmarkAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                cpuModel,
                GCSettings.IsServerGC,
                GCSettings.LatencyMode.ToString(),
                gitRoot is null ? null : RunGit(gitRoot, "rev-parse", "HEAD"),
                gitRoot is null ? null : RunGit(gitRoot, "status", "--porcelain") is { Length: > 0 },
                string.IsNullOrWhiteSpace(artifactPath) ? null : artifactPath,
                HashFile(artifactPath),
                string.IsNullOrWhiteSpace(zoneTreePath) ? null : zoneTreePath,
                HashFile(zoneTreePath));
        }

        private static string? FindGitRoot()
        {
            var candidates = new[]
            {
                new DirectoryInfo(Environment.CurrentDirectory),
                new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory),
            };
            foreach (var candidate in candidates)
            {
                var current = candidate;
                while (current is not null)
                {
                    if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                    {
                        return current.FullName;
                    }

                    current = current.Parent;
                }
            }

            return null;
        }

        private static string? RunGit(string workingDirectory, params string[] arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return null;
                }

                process.WaitForExit();
                return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? HashFile(string path)
        {
            try
            {
                return File.Exists(path)
                    ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                    : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string ReadCpuModel()
        {
            try
            {
                if (File.Exists("/proc/cpuinfo"))
                {
                    var line = File.ReadLines("/proc/cpuinfo")
                        .FirstOrDefault(static value => value.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                    if (line is not null)
                    {
                        var separator = line.IndexOf(':');
                        return separator >= 0 ? line[(separator + 1)..].Trim() : line.Trim();
                    }
                }
            }
            catch (IOException)
            {
                // Host model is optional metadata.
            }

            return "unknown";
        }
    }

    private static class CompressionMarkdown
    {
        public static string Create(CompressionReceipt receipt, string receiptPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# TerraBase compression benchmark receipt");
            builder.AppendLine();
            builder.AppendLine($"Status: **{receipt.Status}**");
            builder.AppendLine();
            builder.AppendLine($"Receipt JSON: `{Path.GetFileName(receiptPath)}`");
            builder.AppendLine($"Raw samples: `{Path.GetFileName(receipt.RawSamplesPath)}`");
            builder.AppendLine();
            builder.AppendLine("## Environment");
            builder.AppendLine();
            builder.AppendLine($"- Commit: `{receipt.Environment.GitCommit ?? "unavailable"}` (dirty: `{receipt.Environment.GitDirty?.ToString() ?? "unknown"}`)");
            builder.AppendLine($"- Benchmark artifact SHA-256: `{receipt.Environment.ArtifactSha256 ?? "unavailable"}`");
            builder.AppendLine($"- ZoneTree assembly SHA-256: `{receipt.Environment.ZoneTreeAssemblySha256 ?? "unavailable"}`");
            builder.AppendLine($"- Runtime: {receipt.Environment.FrameworkDescription}; {receipt.Environment.Runtime}; {receipt.Environment.OperatingSystem}; {receipt.Environment.Architecture}; build `{receipt.Environment.BuildConfiguration}`");
            builder.AppendLine($"- CPU: {receipt.Environment.CpuModel}; {receipt.Environment.ProcessorCount} logical processors; available memory {receipt.Environment.AvailableMemoryBytes:N0} bytes");
            builder.AppendLine($"- GC: server `{receipt.Environment.ServerGc}`, latency mode `{receipt.Environment.GcLatencyMode}`");
            builder.AppendLine();
            builder.AppendLine("## Case summary");
            builder.AppendLine();
            builder.AppendLine("| Profile | Payload bytes | Method | Operation | Compressed bytes | Cold p50/p95/p99 µs | Warm median p50/p95/p99 µs | Warm alloc/op | Warm CPU ms/op | Warm ops/s |");
            builder.AppendLine("| --- | ---: | --- | --- | ---: | --- | --- | ---: | ---: | ---: |");
            foreach (var item in receipt.Cases)
            {
                AppendSummaryRows(builder, item);
            }

            builder.AppendLine();
            builder.AppendLine("## Warm samples");
            builder.AppendLine();
            builder.AppendLine("| Profile | Method | Operation | Sample | Operations | p50 µs | p95 µs | p99 µs | Allocated bytes | CPU ms | CPU % | Output bytes |");
            builder.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var item in receipt.Cases)
            {
                AppendSampleRows(builder, item);
            }

            builder.AppendLine();
            builder.AppendLine($"Command: `{receipt.Command}`");
            builder.AppendLine();
            builder.AppendLine("Cold is the first operation for each profile/method/operation case. Warm rows are measured after the configured warmup and are retained individually; summary values use medians of per-sample values.");
            builder.AppendLine($"Durability setting: `{receipt.Configuration.Durability}`; requested storage setting: `{receipt.Configuration.Storage}`.");
            return builder.ToString();
        }

        private static void AppendSummaryRows(StringBuilder builder, CompressionCaseResult item)
        {
            if (item.Status != "complete")
            {
                builder.Append('|').Append(item.Profile).Append(" | ")
                    .Append(item.PayloadBytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(item.Method).Append(" | — | — | — | — | — | — | — |");
                builder.AppendLine($" Failure: {item.Error?.Replace('\n', ' ') ?? "unknown"} |");
                return;
            }

            AppendSummaryRow(builder, item, item.Compression);
            AppendSummaryRow(builder, item, item.Decompression);
        }

        private static void AppendSummaryRow(StringBuilder builder, CompressionCaseResult item, CompressionOperationResult? operation)
        {
            if (operation?.Cold is null)
            {
                return;
            }

            var warm = operation.WarmSamples;
            builder.Append('|').Append(item.Profile).Append(" | ")
                .Append(item.PayloadBytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(item.Method).Append(" | ").Append(operation.Operation).Append(" | ")
                .Append(item.CompressedBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "—").Append(" | ")
                .Append(FormatTriplet(operation.Cold)).Append(" | ")
                .Append(FormatTriplet(MedianSample(warm))).Append(" | ")
                .Append(Format(Median(warm.Select(static sample => sample.AllocatedBytesPerOperation)))).Append(" | ")
                .Append(Format(Median(warm.Select(static sample => sample.ProcessCpuMilliseconds / Math.Max(sample.Operations, 1))))).Append(" | ")
                .Append(Format(Median(warm.Select(static sample => sample.ThroughputOpsPerSecond)))).AppendLine(" |");
        }

        private static void AppendSampleRows(StringBuilder builder, CompressionCaseResult item)
        {
            foreach (var operation in new[] { item.Compression, item.Decompression })
            {
                if (operation is null)
                {
                    continue;
                }

                foreach (var sample in operation.WarmSamples)
                {
                    builder.Append('|').Append(item.Profile).Append(" | ").Append(item.Method).Append(" | ")
                        .Append(operation.Operation).Append(" | ").Append(sample.Sample).Append(" | ")
                        .Append(sample.Operations).Append(" | ").Append(Format(sample.P50Microseconds)).Append(" | ")
                        .Append(Format(sample.P95Microseconds)).Append(" | ").Append(Format(sample.P99Microseconds)).Append(" | ")
                        .Append(sample.AllocatedBytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                        .Append(Format(sample.ProcessCpuMilliseconds)).Append(" | ").Append(Format(sample.ProcessCpuPercent)).Append(" | ")
                        .Append(sample.OutputBytes.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" |");
                }
            }
        }

        private static string FormatTriplet(CompressionSample? sample)
        {
            return sample is null
                ? "—"
                : $"{Format(sample.P50Microseconds)}/{Format(sample.P95Microseconds)}/{Format(sample.P99Microseconds)}";
        }

        private static CompressionSample? MedianSample(IReadOnlyList<CompressionSample> samples)
        {
            if (samples.Count == 0)
            {
                return null;
            }

            return new CompressionSample
            {
                P50Microseconds = Median(samples.Select(static sample => sample.P50Microseconds)) ?? 0,
                P95Microseconds = Median(samples.Select(static sample => sample.P95Microseconds)) ?? 0,
                P99Microseconds = Median(samples.Select(static sample => sample.P99Microseconds)) ?? 0,
            };
        }

        private static string Format(double? value) => value is null ? "—" : value.Value.ToString("F2", CultureInfo.InvariantCulture);

        private static double? Median(IEnumerable<double> values)
        {
            var array = values.ToArray();
            if (array.Length == 0)
            {
                return null;
            }

            Array.Sort(array);
            return array[array.Length / 2];
        }
    }
}
