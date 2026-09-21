using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TerraBase.Backup;
using TerraBase.Storage.Backup;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>
/// Decomposed backup-phase benchmark: instead of one end-to-end number, it
/// times every layer that a full backup, an incremental backup, and a restore
/// actually spend time in — engine replay on open, capture file cuts, file
/// verification, the raw physical authority scan, the document snapshot walk,
/// the manager's full/incremental backups, and a full-boundary restore — so
/// an optimization can be aimed at the measured hot layer and re-measured
/// after each change.
/// </summary>
public static class BackupMicrobenchWorkload
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static bool IsRequested(string[] args) =>
        ReadWorkload(args) is "backup-microbench";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = MicrobenchOptions.Parse(args);
        if (options.ShowHelp)
        {
            MicrobenchOptions.PrintHelp();
            return 0;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var (receipt, table) = await RunAsync(options, outputDirectory).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "backup-microbench-receipt.json"),
                JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "backup-microbench-summary.md"),
                table).ConfigureAwait(false);
            Console.WriteLine(table);
            Console.WriteLine($"Backup microbench receipt: {outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<(MicrobenchReceipt Receipt, string Table)> RunAsync(
        MicrobenchOptions options,
        string outputDirectory)
    {
        var sourcePath = Path.Combine(outputDirectory, "source");
        var objectStorePath = Path.Combine(outputDirectory, "object-store");
        var targetRoot = Path.Combine(outputDirectory, "targets");
        foreach (var path in new[] { sourcePath, objectStorePath, targetRoot })
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        var tenant = new TenantId("backup-microbench");
        var collection = new CollectionId("documents");
        var rows = new List<(string Phase, TimeSpan Elapsed, string Note)>();

        var writeStarted = Stopwatch.GetTimestamp();
        await using (var engine = new DurableStorageEngine(sourcePath))
        {
            await engine.InitializeJournalIdentityAsync().ConfigureAwait(false);
            for (var batchStart = 0; batchStart < options.Documents; batchStart += options.BatchSize)
            {
                var count = Math.Min(options.BatchSize, options.Documents - batchStart);
                var mutations = new StorageMutation[count];
                for (var index = 0; index < count; index++)
                {
                    var documentIndex = batchStart + index;
                    mutations[index] = StorageMutation.Upsert(
                        new DocumentKey(tenant, collection, DocumentId(documentIndex)),
                        CreateRandomBody(options.Seed, documentIndex),
                        DocumentPrecondition.Missing);
                }

                var result = await engine.ApplyBatchAsync(mutations).ConfigureAwait(false);
                if (result.Outcome != StorageBatchOutcome.Committed)
                    throw new InvalidDataException($"Batch at {batchStart} was not committed: {result.Outcome}.");
            }
        }

        rows.Add(("write (context)", Stopwatch.GetElapsedTime(writeStarted), $"{options.Documents:N0} docs"));

        // A fresh open replays the whole durable log; the incremental path on a
        // fresh source instance pays this, so it gets its own row.
        var openStarted = Stopwatch.GetTimestamp();
        await using (var engine = new DurableStorageEngine(sourcePath))
        {
            var openElapsed = Stopwatch.GetElapsedTime(openStarted);
            rows.Add(("engine open (replay)", openElapsed, $"{options.Documents:N0} docs rebuilt"));

            var captureStarted = Stopwatch.GetTimestamp();
            await using var capture = await engine
                .BeginBackupCaptureAsync(0).ConfigureAwait(false);
            rows.Add(("BeginBackupCaptureAsync", Stopwatch.GetElapsedTime(captureStarted), "file cuts + read lease"));

            var verifyStarted = Stopwatch.GetTimestamp();
            capture.VerifyFiles();
            rows.Add(("VerifyFiles", Stopwatch.GetElapsedTime(verifyStarted), "re-hash capture files"));

            var scanStarted = Stopwatch.GetTimestamp();
            var scanned = 0L;
            var scannedBytes = 0L;
            await foreach (var envelope in capture.ReadRawPhysicalRecordsAsync().ConfigureAwait(false))
            {
                scanned = checked(scanned + 1);
                scannedBytes = checked(scannedBytes + envelope.RecordBytes.Length);
            }

            rows.Add(("authority raw scan", Stopwatch.GetElapsedTime(scanStarted), $"{scanned:N0} records, {scannedBytes:N0} B re-encoded + hashed"));

            var walkStarted = Stopwatch.GetTimestamp();
            var walked = 0L;
            var walkedBytes = 0L;
            string? after = null;
            while (true)
            {
                var page = new List<Document>(1000);
                await foreach (var document in engine.ScanSnapshotAsync(
                        capture.DocumentSnapshot,
                        tenant,
                        collection,
                        after,
                        1000).ConfigureAwait(false))
                {
                    page.Add(document);
                }

                if (page.Count == 0)
                    break;
                foreach (var document in page)
                {
                    walked = checked(walked + 1);
                    walkedBytes = checked(walkedBytes +
                        System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(document.Body).Length);
                }

                after = page[^1].Key.DocumentId;
                if (page.Count < 1000)
                    break;
            }

            rows.Add(("document snapshot walk", Stopwatch.GetElapsedTime(walkStarted), $"{walked:N0} docs, {walkedBytes:N0} B raw bodies"));
        }

        var store = new FileSystemBackupObjectStore(objectStorePath);
        using var signer = new HmacSha256BackupManifestSignatureProvider(
            "terrabase-backup-microbench-trust-root-2026"u8,
            "backup-microbench-root-v1");
        var encryption = new NoBackupEncryption();
        var manager = new BackupManager(
            store,
            encryption,
            signer,
            new BackupEncryptionKeyResolver([new BackupEncryptionKeyResolverEntry("backup-microbench", null, encryption)]),
            new BackupManifestSignatureKeyResolver([new BackupManifestSignatureKeyResolverEntry("backup-microbench", null, signer)]),
            maximumArtifactReadBytes: 2_000_000_000);

        BackupManifest full;
        var fullBackupStarted = Stopwatch.GetTimestamp();
        await using (var engine = new DurableStorageEngine(sourcePath))
        {
            using (var source = new StorageEngineBackupSource(
                "backup-microbench",
                engine,
                new StaticMetadataProvider(tenant.Value, collection.Value)))
            {
                full = await manager.CreateFullBackupAsync(source).ConfigureAwait(false);
            }

            rows.Add(("full backup (manager)", Stopwatch.GetElapsedTime(fullBackupStarted), $"artifact {full.Artifacts[0].Length:N0} B"));

            // The same capture without the manager splits source-side cost
            // (authority + artifact write) from manager overhead (staging,
            // digest, upload, manifest).
            var captureOnlyStarted = Stopwatch.GetTimestamp();
            using (var captureSource = new StorageEngineBackupSource(
                "backup-microbench",
                engine,
                new StaticMetadataProvider(tenant.Value, collection.Value)))
            {
                var direct = await captureSource.CaptureSnapshotAsync().ConfigureAwait(false);
                rows.Add(("capture only (no manager)", Stopwatch.GetElapsedTime(captureOnlyStarted), $"content {direct.Content.Length:N0} B"));
            }

            var updatesStarted = Stopwatch.GetTimestamp();
            for (var update = 0; update < options.Updates; update++)
            {
                var mutation = StorageMutation.Upsert(
                    new DocumentKey(tenant, collection, DocumentId(update)),
                    CreateRandomBody(options.Seed, update, updated: true),
                    DocumentPrecondition.Version(1));
                var result = await engine.ApplyBatchAsync([mutation]).ConfigureAwait(false);
                if (result.Outcome != StorageBatchOutcome.Committed)
                    throw new InvalidDataException($"Update {update} was not committed: {result.Outcome}.");
            }

            rows.Add(($"updates x{options.Updates}", Stopwatch.GetElapsedTime(updatesStarted), "warm engine"));

            var incrementalStarted = Stopwatch.GetTimestamp();
            using (var freshSource = new StorageEngineBackupSource(
                "backup-microbench",
                engine,
                new StaticMetadataProvider(tenant.Value, collection.Value)))
            {
                var incremental = await manager
                    .CreateIncrementalBackupAsync(freshSource, full).ConfigureAwait(false);
                rows.Add(("incremental backup (manager)", Stopwatch.GetElapsedTime(incrementalStarted), $"artifact {incremental.Artifacts[0].Length:N0} B"));
            }
        }

        var restoreStarted = Stopwatch.GetTimestamp();
        var target = new StorageEngineRestoreTarget(Path.Combine(targetRoot, "full"));
        var restore = await manager.RestoreToTimeAsync(
            "backup-microbench",
            full.Boundary.Timestamp,
            target).ConfigureAwait(false);
        if (restore.AppliedLogEntries != 0)
            throw new InvalidDataException($"The full-boundary restore applied {restore.AppliedLogEntries} log entries.");
        rows.Add(("full-boundary restore", Stopwatch.GetElapsedTime(restoreStarted), $"{options.Documents:N0} docs materialized"));

        var receipt = new MicrobenchReceipt(
            "complete",
            DateTimeOffset.UtcNow,
            options,
            [.. rows.Select(row => new MicrobenchPhase(row.Phase, row.Elapsed, row.Note))],
            null);
        return (receipt, CreateTable(receipt));
    }

    private static string CreateTable(MicrobenchReceipt receipt)
    {
        var culture = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            "# Backup microbench",
            string.Empty,
            $"Status: **{receipt.Status}** — {receipt.FinishedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", culture)} UTC, {receipt.Options.Documents:N0} documents",
            string.Empty,
            "| Phase | Elapsed | Note |",
            "| --- | ---: | --- |",
        };
        foreach (var phase in receipt.Phases)
        {
            lines.Add($"| {phase.Phase} | {phase.Elapsed} | {phase.Note} |");
        }

        return string.Join('\n', lines);
    }

    private static string DocumentId(int index) => $"doc-{index:D8}";

    private static JsonElement CreateRandomBody(int seed, int index, bool updated = false)
    {
        var random = new Random(updated ? seed ^ (index << 16) : seed ^ index);
        var tags = new string[1 + random.Next(3)];
        for (var tag = 0; tag < tags.Length; tag++)
        {
            tags[tag] = "tag-" + RandomText(random, 2, 5);
        }

        return JsonSerializer.SerializeToElement(new
        {
            name = RandomText(random, 4, 10),
            tags,
            payload = RandomText(random, 8, 24),
            note = random.Next(4) == 0 ? null : "unicode-é中文-" + random.Next(1000),
            nested = new { value = random.Next(1_000_000) },
            updated,
        });
    }

    private static string RandomText(Random random, int minimumLength, int maximumLength)
    {
        var length = random.Next(minimumLength, maximumLength + 1);
        var characters = new char[length];
        for (var index = 0; index < length; index++)
        {
            characters[index] = Alphabet[random.Next(Alphabet.Length)];
        }

        return new string(characters);
    }

    private static string? ReadWorkload(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--workload=", StringComparison.OrdinalIgnoreCase))
                return args[index]["--workload=".Length..].ToLowerInvariant();
            if (args[index].Equals("--workload", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return args[index + 1].ToLowerInvariant();
        }

        return null;
    }

    private sealed record MicrobenchReceipt(
        string Status,
        DateTimeOffset FinishedAtUtc,
        MicrobenchOptions Options,
        MicrobenchPhase[] Phases,
        string? Error);

    private sealed record MicrobenchPhase(string Phase, TimeSpan Elapsed, string Note);

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

    private sealed record MicrobenchOptions
    {
        public int Documents { get; private init; } = 100_000;

        public int BatchSize { get; private init; } = 5_000;

        public int Updates { get; private init; } = 8;

        public int Seed { get; private init; } = 1337;

        public string OutputDirectory { get; private init; } = "bench/receipts/backup-microbench";

        public bool ShowHelp { get; private init; }
        public static MicrobenchOptions Parse(string[] args)
        {
            var options = new MicrobenchOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument is "--help" or "-h")
                {
                    options = options with { ShowHelp = true };
                    continue;
                }

                var (name, value) = ReadOption(args, ref index, argument);
                options = name switch
                {
                    "workload" => options,
                    "documents" => options with { Documents = ParseInteger(value, name, 1) },
                    "batch-size" => options with { BatchSize = ParseInteger(value, name, 1) },
                    "updates" => options with { Updates = ParseInteger(value, name, 1) },
                    "seed" => options with { Seed = ParseInteger(value, name, int.MinValue) },
                    "output" => options with { OutputDirectory = value },
                    _ => throw new ArgumentException($"Unknown option '--{name}'."),
                };
            }

            if (options.Updates > options.Documents)
                throw new ArgumentException("--updates must not exceed --documents (updates touch distinct documents).");
            return options;
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

        public static void PrintHelp() => Console.WriteLine(
            """
            Backup microbench options:
              --documents <n>      document count (default 100000)
              --batch-size <n>     committed batch size (default 5000)
              --updates <n>        incremental publications (default 8)
              --seed <n>           deterministic seed (default 1337)
              --output <path>      receipt directory
            """);
    }
}
