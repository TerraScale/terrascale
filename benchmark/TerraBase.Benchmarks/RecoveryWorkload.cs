using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Backup;
using TerraBase.Reactive;
using TerraBase.Replication;

namespace TerraBase.Benchmarks;

/// <summary>
/// Measures the replication and restore boundaries that are not part of the
/// provider point-operation runner. The workload deliberately uses the
/// existing in-memory replication and backup seams; it does not call them a
/// network, multi-region, or filesystem durability benchmark.
/// </summary>
public static class RecoveryWorkload
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions RawJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Returns whether this runner owns the requested workload.</summary>
    public static bool IsRequested(string[] args)
    {
        var workload = ReadWorkload(args);
        return workload is "capacity" or "recovery" or "replication-lag" or "restore";
    }

    /// <summary>Runs the selected bounded recovery/capacity workload.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        RecoveryOptions options;
        try
        {
            options = RecoveryOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid recovery benchmark configuration: {exception.Message}");
            RecoveryOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            RecoveryOptions.PrintHelp();
            return 0;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var environment = RecoveryEnvironment.Create();
        if (!string.Equals(environment.BuildConfiguration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            var invalid = RecoveryReceipt.Invalid(
                options,
                environment,
                "Recovery benchmark execution requires a Release artifact.",
                outputDirectory);
            await WriteReceiptAsync(invalid, outputDirectory).ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            ReplicationRun? replication = null;
            RestoreRun? restore = null;
            var fanout = FanoutBoundary.Unsupported;
            if (options.Cases.Contains(RecoveryCase.ReplicationLag))
            {
                replication = await RunReplicationAsync(options, cancellation.Token).ConfigureAwait(false);
            }

            if (options.Cases.Contains(RecoveryCase.Restore))
            {
                restore = await RunRestoreAsync(options, cancellation.Token).ConfigureAwait(false);
            }

            var status = replication?.Status == "failed" || restore?.Status == "failed"
                ? "failed"
                : "complete";
            var receipt = new RecoveryReceipt(
                status,
                environment.StartedAtUtc,
                DateTimeOffset.UtcNow,
                options,
                environment,
                replication,
                restore,
                fanout,
                Environment.CommandLine,
                outputDirectory);
            await WriteReceiptAsync(receipt, outputDirectory).ConfigureAwait(false);
            return status == "complete" ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var cancelled = RecoveryReceipt.Invalid(
                options,
                environment,
                "Cancelled by user.",
                outputDirectory);
            await WriteReceiptAsync(cancelled, outputDirectory).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<ReplicationRun> RunReplicationAsync(
        RecoveryOptions options,
        CancellationToken cancellationToken)
    {
        var samples = new List<ReplicationSample>(options.Samples);
        var rawPath = Path.Combine(Path.GetFullPath(options.OutputDirectory), "raw-samples-replication-lag.jsonl");
        await using var raw = new StreamWriter(rawPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        for (var sampleIndex = 0; sampleIndex < options.Samples; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = RunReplicationSample(options, sampleIndex, cancellationToken);
            samples.Add(sample);
            await raw.WriteLineAsync(JsonSerializer.Serialize(sample, RawJsonOptions)).ConfigureAwait(false);
            await raw.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ReplicationRun(
            "complete",
            "LeaderlessReplicationEngine",
            "source apply -> source delta export -> destination apply; in-memory process-local boundary",
            rawPath,
            samples);
    }

    private static ReplicationSample RunReplicationSample(
        RecoveryOptions options,
        int sampleIndex,
        CancellationToken cancellationToken)
    {
        var records = CreateReplicationRecords(options, sampleIndex);
        var source = new LeaderlessReplicationEngine();
        var destination = new LeaderlessReplicationEngine();
        var latencies = new long[records.Count];
        var backlog = new long[records.Count];
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var workingSetBefore = process.WorkingSet64;
        var started = Stopwatch.GetTimestamp();
        var logicalBytes = 0L;

        for (var index = 0; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = records[index];
            logicalBytes += record.CanonicalBody?.Length ?? Encoding.UTF8.GetByteCount(record.Body?.GetRawText() ?? string.Empty);
            var operationStarted = Stopwatch.GetTimestamp();
            _ = source.Apply(record);
            foreach (var delta in source.ExportDelta(destination.Frontier))
            {
                _ = destination.Apply(delta);
            }

            latencies[index] = Stopwatch.GetTimestamp() - operationStarted;
            var sourceFrontier = source.Frontier.GetValueOrDefault(record.Dot.Origin);
            var destinationFrontier = destination.Frontier.GetValueOrDefault(record.Dot.Origin);
            backlog[index] = checked((long)(sourceFrontier - destinationFrontier));
        }

        process.Refresh();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocatedBefore);
        var sourceSequence = source.Frontier.GetValueOrDefault("benchmark-origin");
        var destinationSequence = destination.Frontier.GetValueOrDefault("benchmark-origin");
        return new ReplicationSample(
            sampleIndex + 1,
            records.Count,
            elapsed.TotalMilliseconds,
            records.Count / Math.Max(elapsed.TotalSeconds, double.Epsilon),
            PercentileMicroseconds(latencies, 0.50),
            PercentileMicroseconds(latencies, 0.95),
            PercentileMicroseconds(latencies, 0.99),
            allocated,
            allocated / (double)records.Count,
            cpu.TotalMilliseconds,
            CpuPercent(cpu, elapsed),
            workingSetBefore,
            process.WorkingSet64,
            process.WorkingSet64 - workingSetBefore,
            logicalBytes,
            PercentileMicroseconds(latencies, 0.50),
            PercentileMicroseconds(latencies, 0.95),
            PercentileMicroseconds(latencies, 0.99),
            backlog.Max(),
            sourceSequence,
            destinationSequence);
    }

    private static List<ReplicationRecord> CreateReplicationRecords(
        RecoveryOptions options,
        int sampleIndex)
    {
        var records = new List<ReplicationRecord>(options.Operations);
        for (var index = 0; index < options.Operations; index++)
        {
            var sequence = checked((ulong)index + 1);
            var key = new DocumentKey(
                new TenantId("benchmark-tenant"),
                new CollectionId("benchmark-replication"),
                $"key-{sequence:D8}");
            var body = JsonDocument.Parse($"{{\"sample\":{sampleIndex},\"sequence\":{sequence},\"seed\":{options.Seed}}}");
            records.Add(new ReplicationRecord(
                key,
                new ReplicationDot("benchmark-origin", sequence),
                new Dictionary<string, ulong>(StringComparer.Ordinal),
                Hlc: checked((long)sequence),
                Deleted: false,
                Body: body.RootElement.Clone()));
            body.Dispose();
        }

        return records;
    }

    private static async Task<RestoreRun> RunRestoreAsync(
        RecoveryOptions options,
        CancellationToken cancellationToken)
    {
        var source = new InMemoryBackupSource(
            "benchmark-restore",
            CreatePayload(options.PayloadSize, options.Seed));
        var store = new InMemoryBackupObjectStore();
        using var signer = new HmacSha256BackupManifestSignatureProvider(
            "terrabase-recovery-benchmark-trust-root-2026"u8,
            "recovery-benchmark-root-v1");
        var manager = new BackupManager(store, new NoBackupEncryption(), signer);
        var full = await manager.CreateFullBackupAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
        var baseTime = full.Boundary.Timestamp;
        for (var index = 0; index < options.LogEntries; index++)
        {
            source.Append(
                index + 1,
                baseTime.AddMilliseconds(index + 1),
                CreatePayload(options.PayloadSize, options.Seed + index + 1));
        }

        var incremental = await manager.CreateIncrementalBackupAsync(
            source,
            full,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var samples = new List<RestoreSample>(options.Samples);
        var rawPath = Path.Combine(Path.GetFullPath(options.OutputDirectory), "raw-samples-restore.jsonl");
        await using var raw = new StreamWriter(rawPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        for (var sampleIndex = 0; sampleIndex < options.Samples; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = await RunRestoreSampleAsync(
                manager,
                incremental,
                options,
                sampleIndex,
                cancellationToken).ConfigureAwait(false);
            samples.Add(sample);
            await raw.WriteLineAsync(JsonSerializer.Serialize(sample, RawJsonOptions)).ConfigureAwait(false);
            await raw.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new RestoreRun(
            "complete",
            "BackupManager",
            "manifest chain load + checksum verification + in-memory transactional target activation",
            rawPath,
            full.Boundary.Sequence,
            incremental.Boundary.Sequence,
            options.LogEntries,
            samples);
    }

    private static async Task<RestoreSample> RunRestoreSampleAsync(
        BackupManager manager,
        BackupManifest restorePoint,
        RecoveryOptions options,
        int sampleIndex,
        CancellationToken cancellationToken)
    {
        var latencies = new long[options.Operations];
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var workingSetBefore = process.WorkingSet64;
        var started = Stopwatch.GetTimestamp();
        var bytesRestored = 0L;
        for (var operation = 0; operation < latencies.Length; operation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = new BenchmarkRestoreTarget();
            var operationStarted = Stopwatch.GetTimestamp();
            await manager.RestoreAsync(restorePoint, target, cancellationToken).ConfigureAwait(false);
            latencies[operation] = Stopwatch.GetTimestamp() - operationStarted;
            bytesRestored += target.RestoredBytes;
        }

        process.Refresh();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocatedBefore);
        return new RestoreSample(
            sampleIndex + 1,
            latencies.Length,
            elapsed.TotalMilliseconds,
            latencies.Length / Math.Max(elapsed.TotalSeconds, double.Epsilon),
            PercentileMilliseconds(latencies, 0.50),
            PercentileMilliseconds(latencies, 0.95),
            PercentileMilliseconds(latencies, 0.99),
            allocated,
            allocated / (double)latencies.Length,
            cpu.TotalMilliseconds,
            CpuPercent(cpu, elapsed),
            workingSetBefore,
            process.WorkingSet64,
            process.WorkingSet64 - workingSetBefore,
            bytesRestored,
            options.LogEntries,
            restorePoint.Boundary.Sequence);
    }

    private static byte[] CreatePayload(int size, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var payload = new byte[size];
        var random = new Random(seed);
        random.NextBytes(payload);
        return payload;
    }

    private static double CpuPercent(TimeSpan cpu, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0
            ? 0
            : cpu.TotalSeconds / elapsed.TotalSeconds / Environment.ProcessorCount * 100;

    private static double PercentileMicroseconds(long[] values, double percentile) =>
        Stopwatch.GetElapsedTime(0, PercentileTicks(values, percentile)).TotalMilliseconds * 1_000;

    private static double PercentileMilliseconds(long[] values, double percentile) =>
        Stopwatch.GetElapsedTime(0, PercentileTicks(values, percentile)).TotalMilliseconds;

    private static long PercentileTicks(long[] values, double percentile)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        Array.Sort(values);
        if (values.Length == 1)
        {
            return values[0];
        }

        var position = (values.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return values[lower];
        }

        return checked((long)(values[lower] + (values[upper] - values[lower]) * (position - lower)));
    }

    private static string ReadWorkload(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--workload=", StringComparison.OrdinalIgnoreCase))
            {
                return args[index]["--workload=".Length..].ToLowerInvariant();
            }

            if (args[index].Equals("--workload", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return args[index + 1].ToLowerInvariant();
            }
        }

        return string.Empty;
    }

    private static async Task WriteReceiptAsync(RecoveryReceipt receipt, string outputDirectory)
    {
        var receiptPath = Path.Combine(outputDirectory, "recovery-receipt.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
        var summaryPath = Path.Combine(outputDirectory, "recovery-summary.md");
        await File.WriteAllTextAsync(summaryPath, RecoveryMarkdown.Create(receipt)).ConfigureAwait(false);
        Console.WriteLine($"Recovery receipt: {receiptPath}");
        Console.WriteLine($"Recovery summary: {summaryPath}");
    }

    private sealed class RecoveryOptions
    {
        public IReadOnlyList<RecoveryCase> Cases { get; private set; } = [RecoveryCase.ReplicationLag, RecoveryCase.Restore];
        public int Samples { get; private set; } = 5;
        public int Operations { get; private set; } = 100;
        public int LogEntries { get; private set; } = 100;
        public int PayloadSize { get; private set; } = 256;
        public int Seed { get; private set; } = 1337;
        public string OutputDirectory { get; private set; } = Path.Combine("bench", "receipts", $"recovery-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");
        public bool ShowHelp { get; private set; }

        public static RecoveryOptions Parse(string[] args)
        {
            var options = new RecoveryOptions();
            var workload = ReadWorkload(args);
            if (workload is "replication-lag")
            {
                options.Cases = [RecoveryCase.ReplicationLag];
            }
            else if (workload is "restore")
            {
                options.Cases = [RecoveryCase.Restore];
            }

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
                    case "workload":
                        break;
                    case "samples":
                        options.Samples = ParseInteger(value, name, 5);
                        break;
                    case "operations":
                        options.Operations = ParseInteger(value, name, 1);
                        break;
                    case "log-entries":
                        options.LogEntries = ParseInteger(value, name, 1);
                        break;
                    case "payload-size":
                        options.PayloadSize = ParseInteger(value, name, 1);
                        break;
                    case "seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
                        {
                            throw new ArgumentException($"--{name} must be a 32-bit integer.");
                        }

                        options.Seed = seed;
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
            Console.WriteLine("TerraBase recovery/capacity benchmark runner");
            Console.WriteLine("  --workload recovery|capacity|replication-lag|restore");
            Console.WriteLine("  --samples N            Post-warmup samples, minimum 5 (default: 5)");
            Console.WriteLine("  --operations N         Operations per sample (default: 100)");
            Console.WriteLine("  --log-entries N        Incremental restore log entries (default: 100)");
            Console.WriteLine("  --payload-size N       Snapshot/log payload bytes (default: 256)");
            Console.WriteLine("  --seed N               Fixed workload seed (default: 1337)");
            Console.WriteLine("  --output DIR           Receipt directory");
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

        private static int ParseInteger(string value, string name, int minimum)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
            {
                throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
            }

            return parsed;
        }

    }

    private enum RecoveryCase
    {
        ReplicationLag,
        Restore,
    }

    private sealed record RecoveryReceipt(
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        RecoveryOptions Configuration,
        RecoveryEnvironment Environment,
        ReplicationRun? Replication,
        RestoreRun? Restore,
        FanoutBoundary Fanout,
        string Command,
        string ReceiptDirectory)
    {
        public static RecoveryReceipt Invalid(
            RecoveryOptions options,
            RecoveryEnvironment environment,
            string error,
            string outputDirectory) =>
            new(
                "invalid",
                environment.StartedAtUtc,
                DateTimeOffset.UtcNow,
                options,
                environment,
                null,
                null,
                FanoutBoundary.Unsupported with { Reason = error },
                System.Environment.CommandLine,
                outputDirectory);
    }

    private sealed record ReplicationRun(
        string Status,
        string Subject,
        string OperationBoundary,
        string RawSamplesPath,
        IReadOnlyList<ReplicationSample> Samples);

    private sealed record RestoreRun(
        string Status,
        string Subject,
        string OperationBoundary,
        string RawSamplesPath,
        long FullBoundarySequence,
        long IncrementalBoundarySequence,
        int LogEntries,
        IReadOnlyList<RestoreSample> Samples);

    private sealed record FanoutBoundary(
        string Status,
        string Subject,
        string OperationBoundary,
        string Reason)
    {
        public static FanoutBoundary Unsupported { get; } = new(
            "unsupported",
            "ReactiveSubscriptionHub",
            "durable commit -> shared maintenance -> subscriber frame delivery",
            "The existing fan-out CLI has no durable commit-to-reactive source callback; run --workload subscriptions to retain the explicit unsupported receipt.");
    }

    private sealed record ReplicationSample(
        int Sample,
        int Operations,
        double ElapsedMilliseconds,
        double ThroughputOpsPerSecond,
        double P50Microseconds,
        double P95Microseconds,
        double P99Microseconds,
        long AllocatedBytes,
        double AllocatedBytesPerOperation,
        double ProcessCpuMilliseconds,
        double ProcessCpuPercent,
        long ProcessWorkingSetBytesBefore,
        long ProcessWorkingSetBytesAfter,
        long ProcessWorkingSetDeltaBytes,
        long LogicalBytes,
        double P50LagMicroseconds,
        double P95LagMicroseconds,
        double P99LagMicroseconds,
        long MaxBacklogOperations,
        ulong SourceFrontier,
        ulong DestinationFrontier);

    private sealed record RestoreSample(
        int Sample,
        int Operations,
        double ElapsedMilliseconds,
        double ThroughputOpsPerSecond,
        double P50RestoreMilliseconds,
        double P95RestoreMilliseconds,
        double P99RestoreMilliseconds,
        long AllocatedBytes,
        double AllocatedBytesPerOperation,
        double ProcessCpuMilliseconds,
        double ProcessCpuPercent,
        long ProcessWorkingSetBytesBefore,
        long ProcessWorkingSetBytesAfter,
        long ProcessWorkingSetDeltaBytes,
        long BytesRestored,
        int AppliedLogEntries,
        long RestoreBoundarySequence);

    private sealed record RecoveryEnvironment(
        DateTimeOffset StartedAtUtc,
        string Runtime,
        string FrameworkDescription,
        string BuildConfiguration,
        string OperatingSystem,
        string Architecture,
        int ProcessorCount,
        long AvailableMemoryBytes,
        long? CgroupMemoryLimitBytes,
        string? CgroupCpuLimit,
        string CpuModel,
        bool ServerGc,
        string GcLatencyMode,
        string? GitCommit,
        bool? GitDirty,
        string? ArtifactPath,
        string? ArtifactSha256,
        string? TerraBaseArtifactPath,
        string? TerraBaseArtifactSha256,
        string? ZoneTreeArtifactPath,
        string? ZoneTreeArtifactSha256)
    {
        public static RecoveryEnvironment Create()
        {
            var artifactPath = Assembly.GetExecutingAssembly().Location;
            var buildConfiguration = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
            var gitRoot = FindGitRoot();
            var commit = gitRoot is null ? null : RunGit(gitRoot, "rev-parse", "HEAD");
            bool? dirty = null;
            if (gitRoot is not null)
            {
                dirty = RunGit(gitRoot, "status", "--porcelain") is { } status && status.Length != 0;
            }

            return new RecoveryEnvironment(
                DateTimeOffset.UtcNow,
                Environment.Version.ToString(),
                RuntimeInformation.FrameworkDescription,
                buildConfiguration,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                ReadCgroupMemoryLimit(),
                ReadCgroupCpuLimit(),
                ReadCpuModel(),
                GCSettings.IsServerGC,
                GCSettings.LatencyMode.ToString(),
                commit,
                dirty,
                string.IsNullOrWhiteSpace(artifactPath) ? null : artifactPath,
                HashFile(artifactPath),
                AssemblyArtifactPath(typeof(BackupManager).Assembly),
                HashFile(typeof(BackupManager).Assembly.Location),
                AssemblyArtifactPath(typeof(ZoneTree.Core.ZoneTree<,>).Assembly),
                HashFile(typeof(ZoneTree.Core.ZoneTree<,>).Assembly.Location));
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

        private static long? ReadCgroupMemoryLimit()
        {
            var value = ReadCgroupValue("/sys/fs/cgroup/memory.max");
            return value is null or "max" || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
                ? null
                : limit;
        }

        private static string? ReadCgroupCpuLimit() => ReadCgroupValue("/sys/fs/cgroup/cpu.max");

        private static string? AssemblyArtifactPath(Assembly assembly)
        {
            var path = assembly.Location;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        private static string? HashFile(string? path) =>
            path is { Length: > 0 } && File.Exists(path)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                : null;

        private static string? ReadCgroupValue(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
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
                        .FirstOrDefault(static item => item.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                    if (line is not null)
                    {
                        var separator = line.IndexOf(':');
                        return separator >= 0 ? line[(separator + 1)..].Trim() : line.Trim();
                    }
                }
            }
            catch
            {
                // CPU model metadata is optional.
            }

            return "unknown";
        }
    }

    private sealed class BenchmarkRestoreTarget : IBackupRestoreTarget, ITransactionalBackupRestoreTarget
    {
        public long RestoredBytes { get; private set; }

        public ValueTask ApplySnapshotAsync(Stream snapshot, BackupWatermark boundary, CancellationToken cancellationToken = default) =>
            ApplySnapshotCoreAsync(snapshot, boundary, cancellationToken);

        public ValueTask ApplyLogEntryAsync(Stream content, BackupLogEntry entry, CancellationToken cancellationToken = default) =>
            ApplyLogEntryCoreAsync(content, entry, cancellationToken);

        public ValueTask<IBackupRestoreSession> BeginRestoreAsync(
            BackupRestorePlan plan,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plan);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IBackupRestoreSession>(new Session(this, plan));
        }

        private async ValueTask ApplySnapshotCoreAsync(
            Stream snapshot,
            BackupWatermark boundary,
            CancellationToken cancellationToken)
        {
            await using var copy = new MemoryStream();
            await snapshot.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            RestoredBytes = copy.Length;
        }

        private async ValueTask ApplyLogEntryCoreAsync(
            Stream content,
            BackupLogEntry entry,
            CancellationToken cancellationToken)
        {
            await using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            RestoredBytes = checked(RestoredBytes + copy.Length);
        }

        private sealed class Session : IBackupRestoreSession
        {
            private readonly BenchmarkRestoreTarget owner;
            private readonly BackupRestorePlan plan;
            private byte[]? snapshot;
            private BackupWatermark? snapshotBoundary;
            private readonly List<BackupLogEntry> entries = [];
            private BackupRestoreSessionCheckpoint checkpoint;
            private bool disposed;

            public Session(BenchmarkRestoreTarget owner, BackupRestorePlan plan)
            {
                this.owner = owner;
                this.plan = plan;
                Plan = plan;
                checkpoint = new BackupRestoreSessionCheckpoint(
                    plan.PlanId,
                    BackupRestoreSessionState.Staging,
                    false,
                    null,
                    0);
            }

            public BackupRestorePlan Plan { get; }

            public BackupRestoreSessionCheckpoint Checkpoint
            {
                get
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    return checkpoint;
                }
            }

            public async ValueTask ApplySnapshotAsync(
                Stream source,
                BackupWatermark boundary,
                CancellationToken cancellationToken = default)
            {
                ThrowIfStaging();
                await using var copy = new MemoryStream();
                await source.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                snapshot = copy.ToArray();
                snapshotBoundary = boundary;
                checkpoint = new BackupRestoreSessionCheckpoint(plan.PlanId, BackupRestoreSessionState.Staging, true, boundary.Sequence, 0);
            }

            public async ValueTask ApplyLogEntryAsync(
                Stream source,
                BackupLogEntry entry,
                CancellationToken cancellationToken = default)
            {
                ThrowIfStaging();
                await using var copy = new MemoryStream();
                await source.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                entries.Add(new BackupLogEntry(entry.Watermark, copy.ToArray()));
                checkpoint = new BackupRestoreSessionCheckpoint(
                    plan.PlanId,
                    BackupRestoreSessionState.Staging,
                    true,
                    entry.Watermark.Sequence,
                    checked(checkpoint.AppliedLogEntries + 1));
            }

            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (checkpoint.State == BackupRestoreSessionState.Committed)
                {
                    return;
                }

                if (checkpoint.State == BackupRestoreSessionState.Aborted
                    || snapshot is null
                    || snapshotBoundary is null)
                {
                    throw new InvalidDataException("The benchmark restore session is incomplete.");
                }

                await using (var snapshotStream = new MemoryStream(snapshot, writable: false))
                {
                    await owner.ApplySnapshotAsync(snapshotStream, snapshotBoundary, cancellationToken).ConfigureAwait(false);
                }

                foreach (var entry in entries)
                {
                    await using var content = new MemoryStream(entry.Content, writable: false);
                    await owner.ApplyLogEntryAsync(content, entry, cancellationToken).ConfigureAwait(false);
                }

                checkpoint = new BackupRestoreSessionCheckpoint(
                    plan.PlanId,
                    BackupRestoreSessionState.Committed,
                    true,
                    plan.FinalBoundary.Sequence,
                    plan.ExpectedLogEntries);
            }

            public ValueTask AbortAsync(CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint = new BackupRestoreSessionCheckpoint(
                    plan.PlanId,
                    BackupRestoreSessionState.Aborted,
                    checkpoint.SnapshotApplied,
                    checkpoint.LastAppliedSequence,
                    checkpoint.AppliedLogEntries);
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                disposed = true;
                return ValueTask.CompletedTask;
            }

            private void ThrowIfStaging()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (checkpoint.State != BackupRestoreSessionState.Staging)
                {
                    throw new InvalidOperationException($"The benchmark restore session is {checkpoint.State.ToString().ToLowerInvariant()}.");
                }
            }
        }
    }

    private static class RecoveryMarkdown
    {
        public static string Create(RecoveryReceipt receipt)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# TerraBase recovery and capacity benchmark");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Status: **{receipt.Status}**");
            builder.AppendLine();
            builder.AppendLine("## Source and environment");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Commit: `{receipt.Environment.GitCommit ?? "unavailable"}` (dirty: `{receipt.Environment.GitDirty?.ToString() ?? "unknown"}`)");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Artifact SHA-256: `{receipt.Environment.ArtifactSha256 ?? "unavailable"}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- TerraBase assembly SHA-256: `{receipt.Environment.TerraBaseArtifactSha256 ?? "unavailable"}` ({receipt.Environment.TerraBaseArtifactPath ?? "unavailable"})");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- ZoneTree assembly SHA-256: `{receipt.Environment.ZoneTreeArtifactSha256 ?? "unavailable"}` ({receipt.Environment.ZoneTreeArtifactPath ?? "unavailable"})");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Runtime: {receipt.Environment.FrameworkDescription}; build `{receipt.Environment.BuildConfiguration}`; {receipt.Environment.OperatingSystem}; {receipt.Environment.Architecture}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- CPU: {receipt.Environment.CpuModel}; {receipt.Environment.ProcessorCount} logical processors; available memory {receipt.Environment.AvailableMemoryBytes:N0} bytes");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Cgroup memory limit: `{FormatLong(receipt.Environment.CgroupMemoryLimitBytes)}`; CPU quota: `{receipt.Environment.CgroupCpuLimit ?? "unavailable"}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- GC: server `{receipt.Environment.ServerGc}`, latency mode `{receipt.Environment.GcLatencyMode}`");
            builder.AppendLine();
            if (receipt.Replication is { } replication)
            {
                builder.AppendLine("## Replication lag");
                builder.AppendLine();
                builder.AppendLine(CultureInfo.InvariantCulture, $"Boundary: `{replication.OperationBoundary}`. Percentiles are medians of per-sample operation percentiles; the lag timer covers one in-memory source-to-destination cycle.");
                builder.AppendLine();
                builder.AppendLine("| Subject | Status | Samples | Throughput ops/s | p50 lag µs | p95 lag µs | p99 lag µs | Allocated bytes/op | CPU % | RSS after bytes | Max backlog ops | Raw samples |");
                builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
                var samples = replication.Samples;
                builder.Append('|').Append(replication.Subject).Append(" | ").Append(replication.Status).Append(" | ")
                    .Append(samples.Count.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.ThroughputOpsPerSecond)))).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.P50LagMicroseconds)))).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.P95LagMicroseconds)))).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.P99LagMicroseconds)))).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.AllocatedBytesPerOperation)))).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.ProcessCpuPercent)))).Append(" | ")
                    .Append(FormatLong(MedianLong(samples.Select(static sample => sample.ProcessWorkingSetBytesAfter)))).Append(" | ")
                    .Append(FormatLong(MedianLong(samples.Select(static sample => sample.MaxBacklogOperations)))).Append(" | `")
                    .Append(replication.RawSamplesPath).AppendLine("` |");
                builder.AppendLine();
            }

            if (receipt.Restore is { } restore)
            {
                builder.AppendLine("## Restore time");
                builder.AppendLine();
                builder.AppendLine(CultureInfo.InvariantCulture, $"Boundary: `{restore.OperationBoundary}`. The source and object store are in-memory; no filesystem reopen, object-store network, or ZoneTree restore adapter is measured.");
                builder.AppendLine();
                builder.AppendLine("| Subject | Status | Samples | Throughput restores/s | p50 restore ms | p95 restore ms | p99 restore ms | Allocated bytes/op | CPU % | RSS after bytes | Log entries | Raw samples |");
                builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
                var samples = restore.Samples;
                builder.Append('|').Append(restore.Subject).Append(" | ").Append(restore.Status).Append(" | ")
                    .Append(samples.Count.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.ThroughputOpsPerSecond)))).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.P50RestoreMilliseconds)))).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.P95RestoreMilliseconds)))).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.P99RestoreMilliseconds)))).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.AllocatedBytesPerOperation)))).Append(" | ")
                    .Append(Format(Median(samples.Select(static sample => sample.ProcessCpuPercent)))).Append(" | ")
                    .Append(FormatLong(MedianLong(samples.Select(static sample => sample.ProcessWorkingSetBytesAfter)))).Append(" | ")
                    .Append(restore.LogEntries.ToString(CultureInfo.InvariantCulture)).Append(" | `")
                    .Append(restore.RawSamplesPath).AppendLine("` |");
                builder.AppendLine();
            }

            builder.AppendLine("## Fan-out boundary");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Status: **{receipt.Fanout.Status}**. {receipt.Fanout.Reason}");
            builder.AppendLine("Use the existing `--workload subscriptions` command for retained claim-free receipts. It reports p50/p95/p99 delivery latency, allocations, CPU, RSS, subscriber counts, egress, and backpressure only when a real durable commit callback is supplied.");
            builder.AppendLine();
            builder.AppendLine("## Limits and next measurements");
            builder.AppendLine();
            builder.AppendLine("- Replication lag is process-local `LeaderlessReplicationEngine` apply/export/apply latency. It does not qualify MagicOnion, network RTT, durable acknowledgement, repair, or multi-region lag.");
            builder.AppendLine("- Restore timing is `BackupManager` over `InMemoryBackupObjectStore` and a transactional in-memory target. It does not qualify StorageEngine or ZoneTree filesystem restore, object-store bandwidth, crash recovery, or RPO/RTO.");
            builder.AppendLine("- Compare horizontal fan-out with the subscriber-count matrix and vertical throughput with `--concurrency` on the provider runner. Record the exact cgroup limits; host RAM alone is not a resource floor.");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Command: `{receipt.Command}`");
            return builder.ToString();
        }

        private static double? Median(IEnumerable<double> values)
        {
            var ordered = values.OrderBy(static value => value).ToArray();
            if (ordered.Length == 0)
            {
                return null;
            }

            var middle = (ordered.Length - 1) / 2.0;
            var lower = (int)Math.Floor(middle);
            var upper = (int)Math.Ceiling(middle);
            return ordered[lower] + (ordered[upper] - ordered[lower]) * (middle - lower);
        }

        private static long? MedianLong(IEnumerable<long> values)
        {
            var ordered = values.OrderBy(static value => value).ToArray();
            if (ordered.Length == 0)
            {
                return null;
            }

            var middle = (ordered.Length - 1) / 2.0;
            var lower = (int)Math.Floor(middle);
            var upper = (int)Math.Ceiling(middle);
            return checked((long)(ordered[lower] + (ordered[upper] - ordered[lower]) * (middle - lower)));
        }

        private static string Format(double? value) => value?.ToString("F2", CultureInfo.InvariantCulture) ?? "—";

        private static string FormatLong(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
    }
}
