using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Backup;
using TerraBase.Storage;
using TerraBase.Storage.Backup;
using TerraBase.Storage.ChangeFeed;
using TerraBase.StorageEngine;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>
/// Qualifies the native filesystem backup path: durable source snapshot,
/// incremental publication logs, checksummed local object store, PITR restore,
/// native generation activation, and a fresh StorageEngine reopen.
/// </summary>
public static class NativeBackupQualificationWorkload
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions RawJsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsRequested(string[] args)
    {
        var workload = ReadWorkload(args);
        return workload is "native-restore" or "native-backup";
    }

    public static async Task<int> RunAsync(string[] args)
    {
        NativeOptions options;
        try
        {
            options = NativeOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid native backup qualification configuration: {exception.Message}");
            NativeOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            NativeOptions.PrintHelp();
            return 0;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var environment = NativeEnvironment.Create();
        if (!string.Equals(environment.BuildConfiguration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            var invalid = NativeReceipt.Invalid(
                options,
                environment,
                "Native backup qualification requires a Release artifact.",
                outputDirectory);
            await WriteReceiptAsync(invalid, outputDirectory).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var result = await RunQualificationAsync(options, outputDirectory).ConfigureAwait(false);
            var receipt = new NativeReceipt(
                "complete",
                result.StartedAtUtc,
                DateTimeOffset.UtcNow,
                options,
                environment,
                result,
                null,
                Environment.CommandLine,
                outputDirectory);
            await WriteReceiptAsync(receipt, outputDirectory).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            var failed = NativeReceipt.Invalid(
                options,
                environment,
                exception.ToString(),
                outputDirectory);
            await WriteReceiptAsync(failed, outputDirectory).ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<NativeRun> RunQualificationAsync(
        NativeOptions options,
        string outputDirectory)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sourcePath = Path.Combine(outputDirectory, "source");
        var objectStorePath = Path.Combine(outputDirectory, "object-store");
        var targetRoot = Path.Combine(outputDirectory, "targets");
        Directory.CreateDirectory(targetRoot);
        var store = new FileSystemBackupObjectStore(objectStorePath);
        using var signer = new HmacSha256BackupManifestSignatureProvider(
            "terrabase-native-backup-qualification-trust-root-2026"u8,
            "native-backup-qualification-root-v1");
        var manager = new BackupManager(store, new NoBackupEncryption(), signer);

        BackupManifest full;
        BackupManifest incremental;
        BackupLogEntry[] logEntries;
        await using (var sourceEngine = new DurableStorageEngine(sourcePath))
        {
            var tenant = new TenantId("native-qualification");
            var collection = new CollectionId("documents");
            var initial = Enumerable.Range(0, options.Operations)
                .Select(index => StorageMutation.Upsert(
                    new DocumentKey(tenant, collection, $"doc-{index:D6}"),
                    JsonSerializer.SerializeToElement(new
                    {
                        phase = "snapshot",
                        index,
                        seed = options.Seed,
                    }),
                    DocumentPrecondition.Missing))
                .ToArray();
            var initialResult = await sourceEngine.ApplyBatchAsync(initial).ConfigureAwait(false);
            EnsureCommitted(initialResult.Outcome, "initial snapshot population");

            using var source = new StorageEngineBackupSource(
                "native-qualification",
                sourceEngine,
                new StaticMetadataProvider(tenant.Value, collection.Value));
            full = await manager.CreateFullBackupAsync(source).ConfigureAwait(false);
            for (var index = 0; index < options.LogEntries; index++)
            {
                var mutation = StorageMutation.Upsert(
                    new DocumentKey(tenant, collection, "doc-000000"),
                    JsonSerializer.SerializeToElement(new
                    {
                        phase = "incremental",
                        sequence = index + 1,
                        seed = options.Seed,
                    }),
                    DocumentPrecondition.Version((ulong)index + 1));
                var result = await sourceEngine.ApplyBatchAsync([mutation]).ConfigureAwait(false);
                EnsureCommitted(result.Outcome, $"incremental publication {index + 1}");
                // Publication timestamps are generated by the durable source.
                // A short gap makes the PITR cut deterministic while remaining
                // outside the measured restore interval.
                await Task.Delay(TimeSpan.FromMilliseconds(2)).ConfigureAwait(false);
            }

            incremental = await manager.CreateIncrementalBackupAsync(source, full).ConfigureAwait(false);
            logEntries = await ReadLogEntriesAsync(source, full.Boundary.Sequence, incremental.Boundary.Sequence)
                .ConfigureAwait(false);
        }

        if (logEntries.Length == 0)
            throw new InvalidDataException("The native qualification produced no incremental log entries.");
        if (logEntries.Length != options.LogEntries)
            throw new InvalidDataException($"Expected {options.LogEntries} incremental log entries, got {logEntries.Length}.");

        var pitEntry = logEntries[0];
        var pitTarget = logEntries.Length == 1
            ? pitEntry.Watermark.Timestamp
            : Midpoint(pitEntry.Watermark.Timestamp, logEntries[1].Watermark.Timestamp);
        var expectedPitrSequence = logEntries
            .Where(entry => entry.Watermark.Timestamp <= pitTarget)
            .Select(entry => entry.Watermark.Sequence)
            .DefaultIfEmpty(full.Boundary.Sequence)
            .Max();
        var expectedPitrEntries = checked((int)(expectedPitrSequence - full.Boundary.Sequence));
        if (expectedPitrEntries < 1 || expectedPitrEntries >= logEntries.Length && logEntries.Length > 1)
            throw new InvalidDataException("The PITR target did not select an interior incremental boundary.");

        var manifests = await manager.ListRestorePointsAsync("native-qualification").ConfigureAwait(false);
        if (manifests.Count != 2)
            throw new InvalidDataException($"Expected two committed manifests, got {manifests.Count}.");
        var checksums = await VerifyChecksumsAsync(store, full, incremental).ConfigureAwait(false);
        var rawPath = Path.Combine(outputDirectory, "raw-samples-native-restore.jsonl");
        var samples = new List<NativeSample>(options.Samples);
        await using var raw = new StreamWriter(rawPath, append: false, new UTF8Encoding(false));
        for (var index = 0; index < options.Samples; index++)
        {
            var sample = await RunSampleAsync(
                manager,
                targetRoot,
                options,
                pitTarget,
                expectedPitrSequence,
                expectedPitrEntries,
                index + 1).ConfigureAwait(false);
            samples.Add(sample);
            await raw.WriteLineAsync(JsonSerializer.Serialize(sample, RawJsonOptions)).ConfigureAwait(false);
            await raw.FlushAsync().ConfigureAwait(false);
        }

        return new NativeRun(
            startedAt,
            rawPath,
            objectStorePath,
            full.BackupId,
            incremental.BackupId,
            full.Boundary.Sequence,
            incremental.Boundary.Sequence,
            pitTarget,
            expectedPitrSequence,
            expectedPitrEntries,
            checksums,
            samples);
    }

    private static async Task<NativeSample> RunSampleAsync(
        BackupManager manager,
        string targetRoot,
        NativeOptions options,
        DateTimeOffset pitTarget,
        long expectedPitrSequence,
        int expectedPitrEntries,
        int sample)
    {
        var root = Path.Combine(targetRoot, $"sample-{sample:D3}");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        var target = new StorageEngineRestoreTarget(root);
        var key = new DocumentKey(
            new TenantId("native-qualification"),
            new CollectionId("documents"),
            "doc-000000");
        var restoreStarted = Stopwatch.GetTimestamp();
        var result = await manager.RestoreToTimeAsync(
            "native-qualification",
            pitTarget,
            target).ConfigureAwait(false);
        var restoreOnly = Stopwatch.GetElapsedTime(restoreStarted);
        if (result.AppliedLogEntries != expectedPitrEntries
            || result.RpoRto.ObservedRecoveryTime is null)
            throw new InvalidDataException("The native PITR result did not report the expected boundary.");

        var reopenStarted = Stopwatch.GetTimestamp();
        var activePath = target.ResolveActivePath();
        await using (var reopened = new DurableStorageEngine(activePath))
        {
            var document = await reopened.ReadAsync(key).ConfigureAwait(false);
            if (document is null
                || document.Body.ValueKind != JsonValueKind.Object
                || document.Body.GetProperty("phase").GetString() != "incremental"
                || document.Body.GetProperty("sequence").GetInt32() != expectedPitrEntries)
                throw new InvalidDataException("A clean StorageEngine reopen did not observe the PITR state.");
            if (reopened.LastCommitSequence != (ulong)expectedPitrSequence)
                throw new InvalidDataException("A clean StorageEngine reopen did not preserve the PITR frontier.");
        }

        var reopenTime = Stopwatch.GetElapsedTime(reopenStarted);
        var total = Stopwatch.GetElapsedTime(restoreStarted);
        return new NativeSample(
            sample,
            result.AppliedLogEntries,
            expectedPitrSequence,
            restoreOnly.TotalMilliseconds,
            reopenTime.TotalMilliseconds,
            total.TotalMilliseconds,
            result.RecoveryTime.TotalMilliseconds,
            target.ActiveGenerationPath is not null,
            Path.GetFullPath(activePath),
            options.Operations,
            options.LogEntries,
            new DirectoryInfo(activePath).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length));
    }

    private static async Task<BackupLogEntry[]> ReadLogEntriesAsync(
        StorageEngineBackupSource source,
        long afterSequence,
        long throughSequence)
    {
        var entries = new List<BackupLogEntry>();
        await foreach (var entry in source.ReadLogAsync(afterSequence, throughSequence).ConfigureAwait(false))
            entries.Add(entry);
        return entries.ToArray();
    }

    private static async Task<ChecksumReceipt> VerifyChecksumsAsync(
        FileSystemBackupObjectStore store,
        BackupManifest full,
        BackupManifest incremental)
    {
        var artifacts = full.Artifacts.Concat(incremental.Artifacts).ToArray();
        var artifactChecksums = new List<string>(artifacts.Length + 2);
        foreach (var artifact in artifacts)
        {
            var metadata = await store.HeadAsync(artifact.Key).ConfigureAwait(false)
                ?? throw new FileNotFoundException("A committed backup artifact is missing.", artifact.Key);
            if (!string.Equals(metadata.Sha256, artifact.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Backup artifact checksum mismatch: {artifact.Key}");
            artifactChecksums.Add(metadata.Sha256);
        }

        foreach (var manifest in new[] { full, incremental })
        {
            var key = $"backups/{manifest.SourceId}/{manifest.BackupId}/manifest.json";
            var metadata = await store.HeadAsync(key).ConfigureAwait(false)
                ?? throw new FileNotFoundException("A committed backup manifest is missing.", key);
            if (metadata.Sha256.Length != 64 || metadata.Sha256.All(static character => character == '0'))
                throw new InvalidDataException($"Backup manifest object checksum is unavailable: {key}");
            artifactChecksums.Add(metadata.Sha256);
        }

        return new ChecksumReceipt(
            true,
            artifacts.Length,
            artifactChecksums);
    }

    private static DateTimeOffset Midpoint(DateTimeOffset first, DateTimeOffset second)
    {
        if (second <= first)
            return first;
        return first.AddTicks((second - first).Ticks / 2);
    }

    private static void EnsureCommitted(StorageBatchOutcome outcome, string operation)
    {
        if (outcome != StorageBatchOutcome.Committed)
            throw new InvalidDataException($"The source operation '{operation}' was not committed: {outcome}.");
    }

    private static string ReadWorkload(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--workload=", StringComparison.OrdinalIgnoreCase))
                return args[index]["--workload=".Length..].ToLowerInvariant();
            if (args[index].Equals("--workload", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return args[index + 1].ToLowerInvariant();
        }

        return string.Empty;
    }

    private static async Task WriteReceiptAsync(NativeReceipt receipt, string outputDirectory)
    {
        var receiptPath = Path.Combine(outputDirectory, "native-backup-receipt.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
        var summaryPath = Path.Combine(outputDirectory, "native-backup-summary.md");
        await File.WriteAllTextAsync(summaryPath, NativeMarkdown.Create(receipt)).ConfigureAwait(false);
        Console.WriteLine($"Native backup receipt: {receiptPath}");
        Console.WriteLine($"Native backup summary: {summaryPath}");
    }

    private sealed class StaticMetadataProvider(string tenant, string collection)
        : IStorageBackupMetadataProvider
    {
        public ValueTask<StorageBackupMetadataSnapshot> CaptureAsync(
            ulong commitSequence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new StorageBackupMetadataSnapshot(
                [new StorageBackupCollectionScope(tenant, collection)],
                [],
                []));
        }
    }

    private sealed class NativeOptions
    {
        public int Samples { get; private set; } = 5;
        public int Operations { get; private set; } = 128;
        public int LogEntries { get; private set; } = 8;
        public int Seed { get; private set; } = 1337;
        public string OutputDirectory { get; private set; } = Path.Combine(
            "bench",
            "receipts",
            $"native-backup-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");
        public bool ShowHelp { get; private set; }

        public static NativeOptions Parse(string[] args)
        {
            var options = new NativeOptions();
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
                        options.Samples = ParseInteger(value, name, 1);
                        break;
                    case "operations":
                        options.Operations = ParseInteger(value, name, 1);
                        break;
                    case "log-entries":
                        options.LogEntries = ParseInteger(value, name, 1);
                        break;
                    case "seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
                            throw new ArgumentException($"--{name} must be a 32-bit integer.");
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
            Console.WriteLine("TerraBase native filesystem backup qualification");
            Console.WriteLine("  --workload native-restore|native-backup");
            Console.WriteLine("  --samples N            Fresh target restore samples (default: 5)");
            Console.WriteLine("  --operations N         Documents in the full snapshot (default: 128)");
            Console.WriteLine("  --log-entries N        Incremental publications and PITR log depth (default: 8)");
            Console.WriteLine("  --seed N               Fixed fixture seed (default: 1337)");
            Console.WriteLine("  --output DIR           Receipt and object-store directory");
        }

        private static (string Name, string Value) ReadOption(string[] args, ref int index, string argument)
        {
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Expected an option, got '{argument}'.");
            var separator = argument.IndexOf('=');
            if (separator > 2)
                return (argument[2..separator], argument[(separator + 1)..]);
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option '{argument}' requires a value.");
            return (argument[2..], args[++index]);
        }

        private static int ParseInteger(string value, string name, int minimum)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
                throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
            return parsed;
        }
    }

    private sealed record NativeReceipt(
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        NativeOptions Configuration,
        NativeEnvironment Environment,
        NativeRun? Native,
        string? Error,
        string Command,
        string ReceiptDirectory)
    {
        public static NativeReceipt Invalid(
            NativeOptions options,
            NativeEnvironment environment,
            string error,
            string outputDirectory) =>
            new(
                "invalid",
                environment.StartedAtUtc,
                DateTimeOffset.UtcNow,
                options,
                environment,
                null,
                error,
                System.Environment.CommandLine,
                outputDirectory);
    }

    private sealed record NativeRun(
        DateTimeOffset StartedAtUtc,
        string RawSamplesPath,
        string ObjectStorePath,
        string FullBackupId,
        string IncrementalBackupId,
        long FullBoundarySequence,
        long IncrementalBoundarySequence,
        DateTimeOffset PitrTargetUtc,
        long ExpectedPitrSequence,
        int ExpectedPitrLogEntries,
        ChecksumReceipt Checksums,
        IReadOnlyList<NativeSample> Samples);

    private sealed record ChecksumReceipt(
        bool Verified,
        int ArtifactCount,
        IReadOnlyList<string> ObjectSha256);

    private sealed record NativeSample(
        int Sample,
        int AppliedLogEntries,
        long RestoredSequence,
        double RestoreMilliseconds,
        double CleanReopenMilliseconds,
        double EndToEndMilliseconds,
        double ManagerRecoveryMilliseconds,
        bool Activated,
        string ActiveGenerationPath,
        int SnapshotDocuments,
        int IncrementalLogEntries,
        long ActivatedGenerationBytes);

    private sealed record NativeEnvironment(
        DateTimeOffset StartedAtUtc,
        string Runtime,
        string FrameworkDescription,
        string BuildConfiguration,
        string OperatingSystem,
        string Architecture,
        int ProcessorCount,
        long AvailableMemoryBytes,
        bool ServerGc,
        string GcLatencyMode,
        string? GitCommit,
        bool? GitDirty,
        string? ArtifactPath,
        string? ArtifactSha256)
    {
        public static NativeEnvironment Create()
        {
            var artifactPath = Assembly.GetExecutingAssembly().Location;
            var gitRoot = FindGitRoot();
            var commit = gitRoot is null ? null : RunGit(gitRoot, "rev-parse", "HEAD");
            bool? dirty = gitRoot is null ? null : RunGit(gitRoot, "status", "--porcelain") is { Length: > 0 };
            return new(
                DateTimeOffset.UtcNow,
                Environment.Version.ToString(),
                RuntimeInformation.FrameworkDescription,
                Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                GCSettings.IsServerGC,
                GCSettings.LatencyMode.ToString(),
                commit,
                dirty,
                string.IsNullOrWhiteSpace(artifactPath) ? null : artifactPath,
                HashFile(artifactPath));
        }

        private static string? FindGitRoot()
        {
            var current = new DirectoryInfo(Environment.CurrentDirectory);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                    return current.FullName;
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
                    startInfo.ArgumentList.Add(argument);
                using var process = Process.Start(startInfo);
                if (process is null)
                    return null;
                process.WaitForExit();
                return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? HashFile(string? path) =>
            path is { Length: > 0 } && File.Exists(path)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                : null;
    }

    private static class NativeMarkdown
    {
        public static string Create(NativeReceipt receipt)
        {
            var builder = new StringBuilder()
                .AppendLine("# TerraBase native filesystem backup qualification")
                .AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"Status: **{receipt.Status}**")
                .AppendLine();
            builder.AppendLine("## Measurement").AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Source commit: `{receipt.Environment.GitCommit ?? "unavailable"}`; dirty `{receipt.Environment.GitDirty?.ToString() ?? "unknown"}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Benchmark artifact SHA-256: `{receipt.Environment.ArtifactSha256 ?? "unavailable"}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Runtime: {receipt.Environment.FrameworkDescription}; build `{receipt.Environment.BuildConfiguration}`; {receipt.Environment.OperatingSystem}; {receipt.Environment.Architecture}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- CPU: {receipt.Environment.ProcessorCount} logical processors; available memory {receipt.Environment.AvailableMemoryBytes:N0} bytes");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- GC: server `{receipt.Environment.ServerGc}`; latency mode `{receipt.Environment.GcLatencyMode}`");
            if (receipt.Native is { } native)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Full boundary: `{native.FullBoundarySequence}`; incremental boundary: `{native.IncrementalBoundarySequence}`");
                builder.AppendLine(CultureInfo.InvariantCulture, $"- PITR target: `{native.PitrTargetUtc:O}`; restored sequence `{native.ExpectedPitrSequence}`; applied log entries `{native.ExpectedPitrLogEntries}`");
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Checksums: `{native.Checksums.Verified}` across `{native.Checksums.ArtifactCount}` artifacts plus committed manifests");
                builder.AppendLine();
                builder.AppendLine("| Samples | Restore p50 ms | Clean reopen p50 ms | End-to-end p50 ms | Restore p95 ms | End-to-end p95 ms | Activated |");
                builder.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | --- |");
                builder.Append("| ").Append(native.Samples.Count.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(Format(Median(native.Samples.Select(static sample => sample.RestoreMilliseconds)))).Append(" | ")
                    .Append(Format(Median(native.Samples.Select(static sample => sample.CleanReopenMilliseconds)))).Append(" | ")
                    .Append(Format(Median(native.Samples.Select(static sample => sample.EndToEndMilliseconds)))).Append(" | ")
                    .Append(Format(Percentile(native.Samples.Select(static sample => sample.RestoreMilliseconds), 0.95))).Append(" | ")
                    .Append(Format(Percentile(native.Samples.Select(static sample => sample.EndToEndMilliseconds), 0.95))).Append(" | ")
                    .Append(native.Samples.All(static sample => sample.Activated) ? "yes" : "no").AppendLine(" |");
                builder.AppendLine();
                builder.AppendLine(CultureInfo.InvariantCulture, $"Raw samples: `{native.RawSamplesPath}`").AppendLine();
            }
            if (receipt.Error is { } error)
                builder.AppendLine(CultureInfo.InvariantCulture, $"Error: `{error}`").AppendLine();
            builder.AppendLine("## Limits").AppendLine();
            builder.AppendLine("- The source engine, object store, and restore target are local filesystem directories on one host; this does not qualify S3/OCI/Tigris latency, remote durability, network transfer, or multi-region recovery.");
            builder.AppendLine("- The PITR cut replays the first incremental publication and verifies a new StorageEngine instance after activation. It does not simulate power loss during upload, manifest commit, generation materialization, or pointer replacement.");
            builder.AppendLine("- Clean reopen is a new StorageEngine instance in the benchmark process. It is evidence of durable files and activation metadata, not a host reboot or process-crash drill.");
            builder.AppendLine("- The observed local end-to-end time is a qualification fixture result; no production RTO/SLO is inferred from this receipt.");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Command: `{receipt.Command}`");
            return builder.ToString();
        }

        private static double? Median(IEnumerable<double> values) => Percentile(values, 0.5);

        private static double? Percentile(IEnumerable<double> values, double percentile)
        {
            var ordered = values.OrderBy(static value => value).ToArray();
            if (ordered.Length == 0)
                return null;
            var position = (ordered.Length - 1) * percentile;
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
        }

        private static string Format(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "—";
    }
}
