using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = BenchmarkOptions.Parse(args);
            if (options.ShowHelp)
            {
                BenchmarkOptions.PrintHelp();
                return 0;
            }

            return await new BenchmarkRunner(options).RunAsync().ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid benchmark configuration: {exception.Message}");
            BenchmarkOptions.PrintHelp();
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Benchmark runner failed: {exception}");
            return 1;
        }
    }

    private sealed class BenchmarkRunner(BenchmarkOptions options)
    {
        private readonly string receiptDirectory = Path.GetFullPath(options.OutputDirectory);

        public async Task<int> RunAsync()
        {
            Directory.CreateDirectory(receiptDirectory);
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
                var environment = EnvironmentInfo.Create();
                var results = new List<WorkloadResult>();
                foreach (var workload in options.Workloads)
                {
                    Console.WriteLine($"Running {options.Storage} {workload} ({options.Samples} samples)...");
                    results.Add(await RunWorkloadAsync(workload, environment, cancellation.Token).ConfigureAwait(false));
                }

                var receipt = new BenchmarkReceipt(
                    Status: results.All(static result => result.Status == "complete") ? "complete" : "failed",
                    StartedAtUtc: environment.StartedAtUtc,
                    FinishedAtUtc: DateTimeOffset.UtcNow,
                    Configuration: options,
                    Environment: environment,
                    Workloads: results,
                    Command: Environment.CommandLine,
                    ReceiptDirectory: receiptDirectory);
                var receiptPath = Path.Combine(receiptDirectory, "receipt.json");
                await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions), cancellation.Token).ConfigureAwait(false);

                var markdownPath = Path.Combine(receiptDirectory, "summary.md");
                await File.WriteAllTextAsync(markdownPath, MarkdownReport.Create(receipt, receiptPath), cancellation.Token).ConfigureAwait(false);
                Console.WriteLine($"Receipt: {receiptPath}");
                Console.WriteLine($"Summary: {markdownPath}");
                return receipt.Status == "complete" ? 0 : 1;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        private async Task<WorkloadResult> RunWorkloadAsync(
            Workload workload,
            EnvironmentInfo environment,
            CancellationToken cancellationToken)
        {
            var rawPath = Path.Combine(receiptDirectory, $"raw-samples-{options.Storage}-{workload}.jsonl");
            var result = new WorkloadResult
            {
                Storage = options.Storage.ToString(),
                Workload = workload.ToString(),
                RawSamplesPath = rawPath,
                StartedAtUtc = DateTimeOffset.UtcNow,
            };

            try
            {
                await using var engine = CreateEngine(workload);
                using var payload = CreatePayload(options.PayloadSize);
                var keys = CreateKeys(options.DatasetSize);
                await SeedAsync(engine, keys, payload.RootElement, cancellationToken).ConfigureAwait(false);

                var digest = WorkloadDigest.Create(options, workload, keys, payload.RootElement);
                result.WorkloadDigest = digest;
                await RunOperationsAsync(engine, workload, keys, payload.RootElement, options.WarmupOperations, null, 0, cancellationToken).ConfigureAwait(false);

                await using var raw = new StreamWriter(rawPath, append: false, Encoding.UTF8);
                for (var sampleIndex = 0; sampleIndex < options.Samples; sampleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sample = await RunSampleAsync(
                        engine,
                        workload,
                        keys,
                        payload.RootElement,
                        sampleIndex,
                        cancellationToken).ConfigureAwait(false);
                    result.Samples.Add(sample);
                    await raw.WriteLineAsync(JsonSerializer.Serialize(sample, JsonOptions)).ConfigureAwait(false);
                    await raw.FlushAsync(cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"  sample {sampleIndex + 1}/{options.Samples}: {sample.ThroughputOpsPerSecond.ToString("F1", CultureInfo.InvariantCulture)} ops/s");
                }

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

            result.FinishedAtUtc = DateTimeOffset.UtcNow;
            return result;
        }

        private TerraBaseEngine CreateEngine(Workload workload)
        {
            if (options.Storage == StorageKind.Memory)
            {
                return TerraBaseEngine.Open();
            }

            var path = Path.Combine(receiptDirectory, "storage-engine", workload.ToString());
            Directory.CreateDirectory(path);
            return TerraBaseEngine.OpenDurable(path);
        }

        private static async Task SeedAsync(
            TerraBaseEngine engine,
            IReadOnlyList<DocumentKey> keys,
            JsonElement body,
            CancellationToken cancellationToken)
        {
            foreach (var key in keys)
            {
                var result = await engine.Documents.UpsertAsync(key, body, DocumentPrecondition.Missing, cancellationToken).ConfigureAwait(false);
                if (result.Outcome != WriteOutcome.Created)
                {
                    throw new InvalidOperationException($"Dataset seed did not create {key.DocumentId}: {result.Outcome}.");
                }
            }
        }

        private async Task<SampleMeasurement> RunSampleAsync(
            TerraBaseEngine engine,
            Workload workload,
            IReadOnlyList<DocumentKey> keys,
            JsonElement body,
            int sampleIndex,
            CancellationToken cancellationToken)
        {
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var processCpuBefore = process.TotalProcessorTime;
            var workingSetBefore = process.WorkingSet64;
            var allocationsBefore = GC.GetTotalAllocatedBytes(false);
            var storageBefore = FileMetrics.TryRead(options.Storage, receiptDirectory, workload);
            var latencies = new long[options.OperationsPerSample];
            var bytesRead = 0L;
            var logicalBytes = 0L;
            var wallStart = Stopwatch.GetTimestamp();

            await RunOperationsAsync(
                engine,
                workload,
                keys,
                body,
                options.OperationsPerSample,
                latencies,
                sampleIndex + 1,
                cancellationToken,
                bytes => Interlocked.Add(ref bytesRead, bytes),
                bytes => Interlocked.Add(ref logicalBytes, bytes)).ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(wallStart);
            process.Refresh();
            var processCpuAfter = process.TotalProcessorTime;
            var workingSetAfter = process.WorkingSet64;
            var allocationsAfter = GC.GetTotalAllocatedBytes(false);
            var storageAfter = FileMetrics.TryRead(options.Storage, receiptDirectory, workload);
            Array.Sort(latencies);

            var bytesWritten = storageBefore is not null && storageAfter is not null
                ? Math.Max(0, storageAfter.WalBytes - storageBefore.WalBytes)
                : (long?)null;
            var writeAmplification = bytesWritten is > 0 && logicalBytes > 0
                ? bytesWritten.Value / (double)logicalBytes
                : (double?)null;
            var cpuTime = processCpuAfter - processCpuBefore;
            var cpuPercent = elapsed.TotalSeconds <= 0
                ? 0
                : cpuTime.TotalSeconds / elapsed.TotalSeconds / Environment.ProcessorCount * 100;

            return new SampleMeasurement
            {
                Sample = sampleIndex + 1,
                Operations = options.OperationsPerSample,
                Concurrency = options.Concurrency,
                ElapsedMilliseconds = elapsed.TotalMilliseconds,
                ThroughputOpsPerSecond = options.OperationsPerSample / Math.Max(elapsed.TotalSeconds, double.Epsilon),
                P50Microseconds = ToMicroseconds(Percentile(latencies, 0.50)),
                P95Microseconds = ToMicroseconds(Percentile(latencies, 0.95)),
                P99Microseconds = ToMicroseconds(Percentile(latencies, 0.99)),
                AllocatedBytes = Math.Max(0, allocationsAfter - allocationsBefore),
                AllocatedBytesPerOperation = Math.Max(0, allocationsAfter - allocationsBefore) / (double)options.OperationsPerSample,
                ProcessCpuMilliseconds = cpuTime.TotalMilliseconds,
                ProcessCpuPercent = cpuPercent,
                ProcessWorkingSetBytesBefore = workingSetBefore,
                ProcessWorkingSetBytesAfter = workingSetAfter,
                ProcessWorkingSetDeltaBytes = workingSetAfter - workingSetBefore,
                LogicalBytes = logicalBytes,
                ReturnedBytes = bytesRead,
                StorageBytesBefore = storageBefore?.TotalBytes,
                StorageBytesAfter = storageAfter?.TotalBytes,
                BytesWritten = bytesWritten,
                WriteAmplification = writeAmplification,
            };
        }

        private async Task RunOperationsAsync(
            TerraBaseEngine engine,
            Workload workload,
            IReadOnlyList<DocumentKey> keys,
            JsonElement body,
            int operationCount,
            long[]? latencies,
            int randomSalt,
            CancellationToken cancellationToken,
            Action<long>? onReadBytes = null,
            Action<long>? onLogicalBytes = null)
        {
            var workers = Enumerable.Range(0, options.Concurrency)
                .Select(worker => Task.Run(async () =>
                {
                    var random = new Random(unchecked(options.Seed + randomSalt * 7919 + worker * 104729));
                    for (var index = worker; index < operationCount; index += options.Concurrency)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var key = keys[random.Next(keys.Count)];
                        var started = Stopwatch.GetTimestamp();
                        var operation = await RunOperationAsync(engine, workload, key, body, cancellationToken).ConfigureAwait(false);
                        if (latencies is not null)
                        {
                            latencies[index] = Stopwatch.GetTimestamp() - started;
                        }

                        onReadBytes?.Invoke(operation.ReadBytes);
                        onLogicalBytes?.Invoke(operation.LogicalBytes);
                    }
                }, cancellationToken))
                .ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        private static async ValueTask<OperationResult> RunOperationAsync(
            TerraBaseEngine engine,
            Workload workload,
            DocumentKey key,
            JsonElement body,
            CancellationToken cancellationToken)
        {
            switch (workload)
            {
                case Workload.PointRead:
                {
                    var document = await engine.Documents.ReadAsync(key, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Point read missed seeded key {key.DocumentId}.");
                    return new OperationResult(
                        ReadBytes: Encoding.UTF8.GetByteCount(document.Body.GetRawText()),
                        LogicalBytes: Encoding.UTF8.GetByteCount(document.Body.GetRawText()));
                }
                case Workload.PointWrite:
                {
                    var result = await engine.Documents.UpsertAsync(key, body, DocumentPrecondition.Any, cancellationToken).ConfigureAwait(false);
                    if (result.Outcome is not (WriteOutcome.Created or WriteOutcome.Updated))
                    {
                        throw new InvalidOperationException($"Point write failed for {key.DocumentId}: {result.Outcome}.");
                    }

                    var bytes = Encoding.UTF8.GetByteCount(body.GetRawText());
                    return new OperationResult(ReadBytes: 0, LogicalBytes: bytes);
                }
                case Workload.Scan:
                {
                    long bytes = 0;
                    await foreach (var document in engine.Documents.ScanAsync(key.Tenant, key.Collection, cancellationToken).ConfigureAwait(false))
                    {
                        bytes += Encoding.UTF8.GetByteCount(document.Body.GetRawText());
                    }

                    return new OperationResult(ReadBytes: bytes, LogicalBytes: bytes);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(workload));
            }
        }

        private IReadOnlyList<DocumentKey> CreateKeys(int count)
        {
            var tenant = new TenantId("benchmark-tenant");
            var collection = new CollectionId("benchmark-collection");
            return Enumerable.Range(0, count)
                .Select(index => new DocumentKey(tenant, collection, $"key-{index:D8}"))
                .ToArray();
        }
    }

    private sealed class BenchmarkOptions
    {
        public StorageKind Storage { get; private set; } = StorageKind.Memory;
        public IReadOnlyList<Workload> Workloads { get; private set; } = [Workload.PointRead, Workload.PointWrite, Workload.Scan];
        public int DatasetSize { get; private set; } = 1_000;
        public int PayloadSize { get; private set; } = 256;
        public int Samples { get; private set; } = 5;
        public int WarmupOperations { get; private set; } = 500;
        public int OperationsPerSample { get; private set; } = 1_000;
        public int Concurrency { get; private set; } = 1;
        public int Seed { get; private set; } = 1337;
        public string OutputDirectory { get; private set; } = Path.Combine("bench", "receipts", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture));
        public bool ShowHelp { get; private set; }

        public static BenchmarkOptions Parse(string[] args)
        {
            var options = new BenchmarkOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument is "--help" or "-h")
                {
                    options.ShowHelp = true;
                    continue;
                }

                var (name, value) = ReadOption(args, ref index, argument);
                switch (name)
                {
                    case "storage":
                        options.Storage = ParseEnum<StorageKind>(value, name);
                        break;
                    case "workload":
                        options.Workloads = value.Equals("all", StringComparison.OrdinalIgnoreCase)
                            ? [Workload.PointRead, Workload.PointWrite, Workload.Scan]
                            : [ParseEnum<Workload>(value, name)];
                        break;
                    case "dataset-size":
                        options.DatasetSize = ParsePositive(value, name);
                        break;
                    case "payload-size":
                        options.PayloadSize = ParsePositive(value, name);
                        break;
                    case "samples":
                        options.Samples = ParsePositive(value, name);
                        if (options.Samples < 5)
                        {
                            throw new ArgumentException("--samples must be at least 5 so every receipt has five post-warmup samples.");
                        }

                        break;
                    case "warmup":
                        options.WarmupOperations = ParseNonNegative(value, name);
                        break;
                    case "operations":
                        options.OperationsPerSample = ParsePositive(value, name);
                        break;
                    case "concurrency":
                        options.Concurrency = ParsePositive(value, name);
                        break;
                    case "seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out options.Seed))
                        {
                            throw new ArgumentException($"--{name} must be a 32-bit integer.");
                        }

                        break;
                    case "output":
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
            Console.WriteLine("TerraBase real storage benchmark runner");
            Console.WriteLine("Options:");
            Console.WriteLine("  --storage memory|storage-engine   Storage provider (default: memory)");
            Console.WriteLine("  --workload point-read|point-write|scan|all");
            Console.WriteLine("  --dataset-size N                  Seeded documents (default: 1000)");
            Console.WriteLine("  --payload-size N                  Payload string bytes (default: 256)");
            Console.WriteLine("  --samples N                       Post-warmup samples, minimum 5 (default: 5)");
            Console.WriteLine("  --warmup N                        Warmup operations (default: 500)");
            Console.WriteLine("  --operations N                    Operations per sample (default: 1000)");
            Console.WriteLine("  --concurrency N                   Concurrent workers (default: 1)");
            Console.WriteLine("  --seed N                          Fixed workload seed (default: 1337)");
            Console.WriteLine("  --output DIR                      Receipt directory");
        }

        private static (string Name, string Value) ReadOption(string[] args, ref int index, string argument)
        {
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

        private static int ParsePositive(string value, string name) => ParseInteger(value, name, 1);

        private static int ParseNonNegative(string value, string name) => ParseInteger(value, name, 0);

        private static int ParseInteger(string value, string name, int minimum)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
            {
                throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
            }

            return parsed;
        }

        private static T ParseEnum<T>(string value, string name)
            where T : struct, Enum => Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
                ? parsed
                : throw new ArgumentException($"--{name} must be one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }

    private enum StorageKind
    {
        Memory,
        StorageEngine,
    }

    private enum Workload
    {
        PointRead,
        PointWrite,
        Scan,
    }

    private sealed class WorkloadResult
    {
        public string Storage { get; init; } = string.Empty;
        public string Workload { get; init; } = string.Empty;
        public string Status { get; set; } = "failed";
        public string? Error { get; set; }
        public string RawSamplesPath { get; init; } = string.Empty;
        public string? WorkloadDigest { get; set; }
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset FinishedAtUtc { get; set; }
        public List<SampleMeasurement> Samples { get; } = [];
    }

    private sealed class SampleMeasurement
    {
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
        public long ProcessWorkingSetBytesBefore { get; init; }
        public long ProcessWorkingSetBytesAfter { get; init; }
        public long ProcessWorkingSetDeltaBytes { get; init; }
        public long LogicalBytes { get; init; }
        public long ReturnedBytes { get; init; }
        public long? StorageBytesBefore { get; init; }
        public long? StorageBytesAfter { get; init; }
        public long? BytesWritten { get; init; }
        public double? WriteAmplification { get; init; }
    }

    private sealed record BenchmarkReceipt(
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        BenchmarkOptions Configuration,
        EnvironmentInfo Environment,
        IReadOnlyList<WorkloadResult> Workloads,
        string Command,
        string ReceiptDirectory);

    private sealed record EnvironmentInfo(
        DateTimeOffset StartedAtUtc,
        string Runtime,
        string FrameworkDescription,
        string OperatingSystem,
        string Architecture,
        int ProcessorCount,
        long AvailableMemoryBytes,
        string CpuModel,
        bool ServerGc,
        string GcLatencyMode,
        string? GitCommit,
        bool? GitDirty,
        string? ArtifactPath,
        string? ArtifactSha256)
    {
        public static EnvironmentInfo Create()
        {
            var artifactPath = Assembly.GetExecutingAssembly().Location;
            var artifactHash = File.Exists(artifactPath)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))).ToLowerInvariant()
                : null;
            var gitRoot = FindGitRoot();
            var commit = gitRoot is null ? null : RunGit(gitRoot, "rev-parse", "HEAD");
            var dirty = gitRoot is null ? null : RunGit(gitRoot, "status", "--porcelain") is { } status && status.Length != 0;
            var cpuModel = ReadCpuModel();
            return new EnvironmentInfo(
                DateTimeOffset.UtcNow,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                cpuModel,
                GCSettings.IsServerGC,
                GCSettings.LatencyMode.ToString(),
                commit,
                dirty,
                string.IsNullOrWhiteSpace(artifactPath) ? null : artifactPath,
                artifactHash);
        }

        private static string? FindGitRoot()
        {
            var current = new DirectoryInfo(Environment.CurrentDirectory);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        private static string? RunGit(string workingDirectory, params string[] arguments)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { },
                });
                if (process is null)
                {
                    return null;
                }

                foreach (var argument in arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                process.WaitForExit();
                return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
            }
            catch
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
                        .FirstOrDefault(static line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                    if (line is not null)
                    {
                        var separator = line.IndexOf(':');
                        return separator >= 0 ? line[(separator + 1)..].Trim() : line.Trim();
                    }
                }
            }
            catch
            {
                // The model is optional environment metadata.
            }

            return "unknown";
        }
    }

    private sealed record FileMetrics(long TotalBytes, long WalBytes)
    {
        public static FileMetrics? TryRead(StorageKind storage, string receiptDirectory, Workload workload)
        {
            if (storage != StorageKind.StorageEngine)
            {
                return null;
            }

            var root = Path.Combine(receiptDirectory, "storage-engine", workload.ToString());
            if (!Directory.Exists(root))
            {
                return null;
            }

            try
            {
                var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
                var total = files.Sum(static path => new FileInfo(path).Length);
                var walPath = Path.Combine(root, "wal");
                var wal = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
                return new FileMetrics(total, wal);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private static class WorkloadDigest
    {
        public static string Create(BenchmarkOptions options, Workload workload, IReadOnlyList<DocumentKey> keys, JsonElement body)
        {
            var text = string.Join("\n", [
                options.Storage.ToString(),
                workload.ToString(),
                options.DatasetSize.ToString(CultureInfo.InvariantCulture),
                options.PayloadSize.ToString(CultureInfo.InvariantCulture),
                options.Samples.ToString(CultureInfo.InvariantCulture),
                options.WarmupOperations.ToString(CultureInfo.InvariantCulture),
                options.OperationsPerSample.ToString(CultureInfo.InvariantCulture),
                options.Concurrency.ToString(CultureInfo.InvariantCulture),
                options.Seed.ToString(CultureInfo.InvariantCulture),
                body.GetRawText(),
                string.Join("\n", keys.Select(static key => $"{key.Tenant.Value}/{key.Collection.Value}/{key.DocumentId}")),
            ]);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }
    }

    private static class MarkdownReport
    {
        public static string Create(BenchmarkReceipt receipt, string receiptPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# TerraBase benchmark receipt");
            builder.AppendLine();
            builder.AppendLine($"Status: **{receipt.Status}**");
            builder.AppendLine();
            builder.AppendLine($"Receipt JSON: `{Path.GetFileName(receiptPath)}`");
            builder.AppendLine();
            builder.AppendLine("## Environment");
            builder.AppendLine();
            builder.AppendLine($"- Commit: `{receipt.Environment.GitCommit ?? "unavailable"}` (dirty: `{receipt.Environment.GitDirty?.ToString() ?? "unknown"}`)");
            builder.AppendLine($"- Artifact SHA-256: `{receipt.Environment.ArtifactSha256 ?? "unavailable"}`");
            builder.AppendLine($"- Runtime: {receipt.Environment.FrameworkDescription}; {receipt.Environment.OperatingSystem}; {receipt.Environment.Architecture}");
            builder.AppendLine($"- CPU: {receipt.Environment.CpuModel}; {receipt.Environment.ProcessorCount} logical processors; available memory {receipt.Environment.AvailableMemoryBytes} bytes");
            builder.AppendLine($"- GC: server `{receipt.Environment.ServerGc}`, latency mode `{receipt.Environment.GcLatencyMode}`");
            builder.AppendLine();
            builder.AppendLine("## Results");
            builder.AppendLine();
            builder.AppendLine("| Storage | Workload | Status | Samples | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |");
            builder.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var result in receipt.Workloads)
            {
                var samples = result.Samples;
                builder.Append('|').Append(result.Storage).Append(" | ").Append(result.Workload).Append(" | ").Append(result.Status).Append(" | ");
                builder.Append(samples.Count.ToString(CultureInfo.InvariantCulture)).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.ThroughputOpsPerSecond)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.P50Microseconds)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.P95Microseconds)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.P99Microseconds)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.AllocatedBytesPerOperation)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.ProcessCpuPercent)))).Append(" | ");
                builder.Append(FormatLong(MedianLong(samples.Select(static sample => sample.ProcessWorkingSetBytesAfter)))).Append(" | ");
                builder.Append(FormatLong(MedianNullableLong(samples.Select(static sample => sample.BytesWritten)))).Append(" | ");
                builder.Append(Format(MedianNullable(samples.Select(static sample => sample.WriteAmplification)))).Append(" | ");
                builder.Append('`').Append(Path.GetFileName(result.RawSamplesPath)).AppendLine("` |");
                if (result.Error is not null)
                {
                    builder.AppendLine();
                    builder.AppendLine($"Failure for `{result.Workload}`: `{result.Error.Replace("\n", " ", StringComparison.Ordinal)}`");
                }
            }

            builder.AppendLine();
            builder.AppendLine($"Command: `{receipt.Command}`");
            builder.AppendLine();
            builder.AppendLine("All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory storage reports no filesystem write measurement.");
            return builder.ToString();
        }

        private static string Format(double? value) => value is null ? "—" : value.Value.ToString("F2", CultureInfo.InvariantCulture);

        private static string FormatLong(long? value) => value is null ? "—" : value.Value.ToString("N0", CultureInfo.InvariantCulture);

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

        private static long? MedianLong(IEnumerable<long> values)
        {
            var array = values.ToArray();
            if (array.Length == 0)
            {
                return null;
            }

            Array.Sort(array);
            return array[array.Length / 2];
        }

        private static long? MedianNullableLong(IEnumerable<long?> values)
        {
            var array = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
            return array.Length == 0 ? null : MedianLong(array);
        }

        private static double? MedianNullable(IEnumerable<double?> values)
        {
            var array = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
            return array.Length == 0 ? null : Median(array);
        }
    }

    private readonly record struct OperationResult(long ReadBytes, long LogicalBytes);

    private static JsonDocument CreatePayload(int payloadSize)
    {
        var payload = new string('x', payloadSize);
        return JsonDocument.Parse(JsonSerializer.Serialize(new { payload }));
    }

    private static long Percentile(long[] sortedTicks, double percentile)
    {
        var index = (int)Math.Ceiling(sortedTicks.Length * percentile) - 1;
        return sortedTicks[Math.Clamp(index, 0, sortedTicks.Length - 1)];
    }

    private static double ToMicroseconds(long stopwatchTicks) => stopwatchTicks * 1_000_000d / Stopwatch.Frequency;
}
