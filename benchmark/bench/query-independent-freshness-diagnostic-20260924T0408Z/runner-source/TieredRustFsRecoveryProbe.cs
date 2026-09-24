using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using TerraBase.Backup;
using TerraBase.StorageEngine;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>
/// Rebuilds a fresh local engine root from an already qualified RustFS journal.
/// This is deliberately separate from the ingest workload: it never creates or
/// deletes remote objects and it only removes the source engine root after the
/// source receipt and remote frontier have been checked.
/// </summary>
public static class TieredRustFsRecoveryProbe
{
    private const int MaximumSamples = 4_096;
    private const int EvenlySpacedSamples = 1_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool IsRequested(string[] args)
    {
        var workload = ReadWorkload(args);
        return workload is "tiered-recover-existing" or "rustfs-tiered-recover"
            || args.Any(static argument => argument.Equals("--recover-existing", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        Options? options = null;
        try
        {
            options = Options.Parse(args);
            if (options.Help)
            {
                Options.PrintHelp();
                return 0;
            }

            if (typeof(TieredRustFsRecoveryProbe).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), inherit: false)
                    .OfType<System.Reflection.AssemblyConfigurationAttribute>()
                    .FirstOrDefault()?.Configuration != "Release")
            {
                throw new InvalidOperationException("RustFS recovery qualification requires a Release artifact.");
            }

            if (!options.RecoverExisting)
                throw new ArgumentException("Recovery mode requires --recover-existing.");

            return await RunProbeAsync(options).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            if (options is not null)
                await MarkFailureAsync(options.Output, exception).ConfigureAwait(false);
            Console.Error.WriteLine($"Invalid RustFS recovery configuration: {exception.Message}");
            Options.PrintHelp();
            return 2;
        }
        catch (Exception exception)
        {
            if (options is not null)
                await MarkFailureAsync(options.Output, exception).ConfigureAwait(false);
            Console.Error.WriteLine($"RustFS recovery probe failed: {exception}");
            return 1;
        }
    }

    private static async Task MarkFailureAsync(string output, Exception exception)
    {
        var receiptPath = Path.Combine(Path.GetFullPath(output), "tiered-rustfs-recovery-receipt.json");
        if (!File.Exists(receiptPath))
            return;
        try
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath).ConfigureAwait(false)) as JsonObject;
            if (node is null)
                return;
            node["Status"] = "failed";
            node["Phase"] = "failed";
            node["FinishedAtUtc"] = DateTimeOffset.UtcNow;
            node["Error"] = exception.ToString();
            await File.WriteAllTextAsync(receiptPath, node.ToJsonString(JsonOptions)).ConfigureAwait(false);
        }
        catch (Exception receiptException)
        {
            Console.Error.WriteLine($"Could not mark recovery receipt failed: {receiptException.Message}");
        }
    }

    private static async Task<int> RunProbeAsync(Options options)
    {
        var sourceReceiptPath = RequireRegularFile(options.SourceReceipt!, "source receipt");
        var source = LoadSourceReceipt(sourceReceiptPath);
        ResolveAndValidateOptions(options, source, sourceReceiptPath);
        var sourceReceiptSha256 = ComputeSha256(sourceReceiptPath);
        if (!string.IsNullOrWhiteSpace(options.ExpectedSourceReceiptSha256)
            && !string.Equals(options.ExpectedSourceReceiptSha256, sourceReceiptSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"The source receipt hash {sourceReceiptSha256} does not match the expected hash {options.ExpectedSourceReceiptSha256}.");
        }

        var sourceOutput = Path.GetDirectoryName(sourceReceiptPath)
            ?? throw new IOException($"The source receipt has no parent directory: '{sourceReceiptPath}'.");
        sourceOutput = Path.GetFullPath(sourceOutput);
        var sourceRoot = Path.GetFullPath(source.EngineRoot!);
        var expectedSourceRoot = Path.Combine(sourceOutput, "engine");
        if (!PathsEqual(sourceRoot, expectedSourceRoot))
        {
            throw new IOException(
                $"The source receipt engine root must be its owned output root '{expectedSourceRoot}', not '{sourceRoot}'.");
        }

        RejectSymlinkAncestors(sourceOutput);
        RejectSymlinkAncestors(options.Output);
        var output = Path.GetFullPath(options.Output);
        if (PathsEqual(output, sourceOutput)
            || IsPathInside(output, sourceOutput)
            || IsPathInside(sourceOutput, output))
        {
            throw new IOException("The fresh recovery output must be a separate sibling root from the source run.");
        }

        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException($"The recovery output already exists; refusing to use a root not owned by this run: '{output}'.");

        var recoveryRoot = Path.Combine(output, "engine");
        var cache = Path.Combine(recoveryRoot, "ssd-cache");
        Directory.CreateDirectory(output);
        var outputMount = ValidateOutputMount(output, options.MinFreeSpaceBytes);
        var receiptPath = Path.Combine(output, "tiered-rustfs-recovery-receipt.json");
        var summaryPath = Path.Combine(output, "tiered-rustfs-recovery-summary.md");
        var progressPath = Path.Combine(output, "progress.jsonl");
        var samplesPath = Path.Combine(output, "resource-samples.jsonl");
        var envelopePath = Path.Combine(output, "resource-envelope.json");
        var currentSubject = RunReceipt.CaptureSubject();
        var assemblyPath = typeof(TieredRustFsRecoveryProbe).Assembly.Location;
        var assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant();
        if (!string.Equals(source.BenchmarkAssemblySha256, assemblySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"The recovery assembly hash {assemblySha256} does not match the source receipt hash {source.BenchmarkAssemblySha256}.");
        }

        var receipt = new RecoveryReceiptState
        {
            Status = "running",
            Phase = "validated-source",
            Mode = "recover-existing",
            StartedAtUtc = DateTimeOffset.UtcNow,
            SourceReceiptPath = sourceReceiptPath,
            SourceReceiptSha256 = sourceReceiptSha256,
            SourceOutput = sourceOutput,
            SourceEngineRoot = sourceRoot,
            RecoveryOutput = output,
            RecoveryEngineRoot = recoveryRoot,
            ExpectedRecords = options.Records!.Value,
            Prefix = source.Prefix,
            SegmentObjectPrefix = source.SegmentObjectPrefix,
            JournalObjectPrefix = source.JournalObjectPrefix,
            JournalFramePrefix = source.JournalFramePrefix,
            DatabaseId = source.DatabaseId,
            WriterId = source.WriterId,
            Region = source.Region,
            S3Endpoint = source.S3Endpoint,
            S3Bucket = options.Bucket!,
            Batch = options.Batch,
            PayloadMode = options.PayloadModeName,
            PayloadGenerator = PayloadGeneratorName(options.PayloadMode!.Value),
            PayloadBytes = options.PayloadBytes,
            PayloadSeed = options.Seed!.Value,
            SourceBenchmarkAssembly = source.BenchmarkAssembly,
            SourceBenchmarkAssemblySha256 = source.BenchmarkAssemblySha256,
            BenchmarkAssembly = assemblyPath,
            BenchmarkAssemblySha256 = assemblySha256,
            SubjectProject = currentSubject.Project,
            SubjectCommit = currentSubject.Commit,
            SubjectTreeDigest = currentSubject.TreeDigest,
            SubjectWorkingTreeDigest = currentSubject.WorkingTreeDigest,
            SubjectDirty = currentSubject.Dirty,
            SourceSubjectCommit = source.Subject?.Commit,
            SourceSubjectTreeDigest = source.Subject?.TreeDigest,
            SourceSubjectWorkingTreeDigest = source.Subject?.WorkingTreeDigest,
            SourceSubjectDirty = source.Subject?.Dirty ?? false,
            RequireIsolatedCgroup = options.RequireIsolatedCgroup,
            MaxLocalStorageBytes = options.MaxLocalStorageBytes,
            MinimumFreeSpaceBytes = options.MinFreeSpaceBytes,
            OutputMount = outputMount.Name,
            ResourceSamplesPath = samplesPath,
            ProgressPath = progressPath,
            SourceRemoteAckRecords = source.RemoteAck!.AcknowledgedRecords,
            SourceInternalAuthorityRecords = checked(source.RemoteAck!.AcknowledgedRecords - source.TargetRecords),
            ExactScan = "skipped",
            VerificationScope = options.ExactScan
                ? "exact-stream-root-loss-recovery"
                : "sampled-remote-journal-recovery",
        };
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);

        var initialCgroup = BenchmarkCgroupSnapshot.Capture();
        receipt.InitialCgroup = initialCgroup;
        receipt.IsolatedCgroup = IsUsefulIsolatedCgroup(initialCgroup);
        if (options.RequireIsolatedCgroup && receipt.IsolatedCgroup is not true)
            throw new InvalidOperationException("--require-isolated-cgroup requires a finite isolated cgroup.");
        await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(initialCgroup, JsonOptions)).ConfigureAwait(false);

        var tracker = new ResourceMonitor(recoveryRoot, cache, outputMount.Name, options.MaxLocalStorageBytes, options.MinFreeSpaceBytes, samplesPath);
        await using var resourceMonitor = tracker;
        await using var progress = new ProgressWriter(progressPath);
        await progress.WriteAsync("validated-source", 0, options.Records!.Value, null).ConfigureAwait(false);
        await resourceMonitor.StartAsync().ConfigureAwait(false);

        var endpoint = new Uri(options.Endpoint, UriKind.Absolute);
        using var client = new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = endpoint.ToString().TrimEnd('/'),
                ForcePathStyle = true,
                AuthenticationRegion = options.Region,
            });
        var objectStore = new S3NativeBackupObjectStore(client, options.Bucket!, maxAttempts: 3,
            checkpointStore: new FileBackupUploadCheckpointStore(Path.Combine(output, "uploads")));
        var databaseId = EntityId.Parse(source.DatabaseId!, CultureInfo.InvariantCulture);
        var writerId = EntityId.Parse(source.WriterId!, CultureInfo.InvariantCulture);
        var remoteJournal = new RemoteCommitJournal(
            objectStore,
            databaseId,
            source.Region!,
            writerId,
            source.JournalObjectPrefix!);
        if (!string.Equals(remoteJournal.FramePrefix, source.JournalFramePrefix, StringComparison.Ordinal))
            throw new IOException("The reconstructed journal does not have the exact frame prefix recorded by the source run.");

        var segmentObjects = await ListObjectsAsync(client, options.Bucket!, source.SegmentObjectPrefix!).ConfigureAwait(false);
        if (segmentObjects.ObjectCount != 0)
            throw new IOException(
                $"The source segment prefix is not empty ({segmentObjects.ObjectCount:N0} objects); recovery refuses to mix sealed segments with journal-only data.");

        var frontierTimer = Stopwatch.StartNew();
        var frontier = await remoteJournal.GetValidatedFrontierAsync().ConfigureAwait(false);
        frontierTimer.Stop();
        var journalObjects = await ListObjectsAsync(client, options.Bucket!, remoteJournal.FramePrefix).ConfigureAwait(false);
        ValidateRemoteSource(source, options.Records!.Value, frontier, journalObjects, segmentObjects);
        receipt.RemoteValidatedFrontier = frontier;
        receipt.RemoteJournalObjects = journalObjects;
        receipt.RemoteSegmentObjects = segmentObjects;
        receipt.RemoteFrontierValidationSeconds = frontierTimer.Elapsed.TotalSeconds;
        receipt.Phase = "remote-validated";
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
        await progress.WriteAsync("remote-validated", 0, options.Records!.Value, journalObjects).ConfigureAwait(false);

        var sourceWal = Path.Combine(sourceRoot, "wal");
        RequireOwnedSourceRoot(sourceRoot, sourceWal);
        receipt.SourceWalBytes = new FileInfo(sourceWal).Length;
        receipt.Phase = "source-root-loss";
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
        // Re-read the source receipt immediately before deleting its engine
        // root. The first hash admits the source run; this second read closes
        // the race where another process could alter the receipt between
        // admission and root loss.
        var sourceReceiptSha256BeforeDeletion = ComputeSha256(sourceReceiptPath);
        if (!string.Equals(sourceReceiptSha256, sourceReceiptSha256BeforeDeletion, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The source receipt changed before the source engine root could be removed.");
        var rootLossTimer = Stopwatch.StartNew();
        DeleteOwnedRoot(sourceRoot);
        rootLossTimer.Stop();
        receipt.SourceRootDeleted = !Directory.Exists(sourceRoot);
        if (!receipt.SourceRootDeleted)
            throw new IOException("The benchmark-owned source engine root still exists after deletion.");
        var sourceReceiptSha256AfterDeletion = ComputeSha256(sourceReceiptPath);
        receipt.SourceReceiptUnchanged = string.Equals(
            sourceReceiptSha256BeforeDeletion,
            sourceReceiptSha256AfterDeletion,
            StringComparison.OrdinalIgnoreCase);
        if (!receipt.SourceReceiptUnchanged)
            throw new IOException("The source receipt changed while the source engine root was being removed.");

        var recoveryTimer = Stopwatch.StartNew();
        receipt.Phase = "remote-journal-recovery";
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
        await RebuildWithHeartbeatAsync(remoteJournal, recoveryRoot, options.Batch, progress, tracker, options.Records!.Value).ConfigureAwait(false);
        recoveryTimer.Stop();
        tracker.ThrowIfExceeded();
        var recoveredWal = Path.Combine(recoveryRoot, "wal");
        if (!File.Exists(recoveredWal) || new FileInfo(recoveredWal).Length <= 0)
            throw new IOException("Remote recovery completed without a nonempty local WAL.");
        receipt.RecoveredWalBytes = new FileInfo(recoveredWal).Length;
        receipt.RecoveredFrontier = frontier;
        receipt.PhaseMeasurements.Add(new RecoveryPhaseMeasurement("source-root-loss", rootLossTimer.Elapsed.TotalSeconds));
        receipt.PhaseMeasurements.Add(new RecoveryPhaseMeasurement("remote-journal-recovery", recoveryTimer.Elapsed.TotalSeconds));

        var tenant = new TenantId(source.Tenant ?? "tiered-qualification");
        var collection = new CollectionId(source.Collection ?? "documents");
        var payload = new PayloadFactory(options.PayloadBytes, options.PayloadMode!.Value, options.Seed!.Value);
        var samples = BuildSamples(options.Seed.Value, options.Records.Value);
        receipt.SampleCount = samples.Length;
        receipt.SampleFirstId = samples[0];
        receipt.SampleLastId = samples[^1];
        receipt.EvenlySpacedSampleCount = Math.Min(EvenlySpacedSamples, samples.Length);
        var tiered = new TieredStorageSegmentStore(new TieredStorageSegmentStoreOptions(
            objectStore,
            cache,
            source.Prefix!,
            options.CacheBytes));

        await VerifyOpenAndSamplesAsync(
            recoveryRoot,
            tiered,
            remoteJournal,
            tenant,
            collection,
            payload,
            samples,
            tracker,
            receipt,
            progress,
            "first-reopen",
            receiptPath,
            options.Records!.Value).ConfigureAwait(false);
        if (options.ExactScan)
        {
            receipt.Phase = "exact-scan";
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            var scanTimer = Stopwatch.StartNew();
            long scanCount;
            await using (var scanEngine = new DurableStorageEngine(recoveryRoot, new LocalStorageTierPlacement(recoveryRoot), StorageWalDurability.Synced, tiered, remoteJournal))
            {
                scanCount = await VerifyExactScanAsync(scanEngine, tenant, collection, payload, options.Records!.Value, tracker).ConfigureAwait(false);
            }
            scanTimer.Stop();
            receipt.ExactScan = "complete";
            receipt.ExactScanCount = scanCount;
            receipt.PhaseMeasurements.Add(new RecoveryPhaseMeasurement("exact-scan", scanTimer.Elapsed.TotalSeconds));
        }

        await VerifyOpenAndSamplesAsync(
            recoveryRoot,
            tiered,
            remoteJournal,
            tenant,
            collection,
            payload,
            samples,
            tracker,
            receipt,
            progress,
            "second-reopen",
            receiptPath,
            options.Records!.Value).ConfigureAwait(false);

        var finalCgroup = BenchmarkCgroupSnapshot.Capture();
        receipt.FinalCgroup = finalCgroup;
        receipt.IsolatedCgroup = IsUsefulIsolatedCgroup(finalCgroup);
        receipt.LatestLocalStorageBytes = DirectoryBytes(recoveryRoot);
        receipt.LatestCacheBytes = DirectoryBytes(cache);
        receipt.SourceRemoteAckRecords = source.RemoteAck!.AcknowledgedRecords;
        receipt.FinishedAtUtc = DateTimeOffset.UtcNow;
        receipt.Status = "complete";
        receipt.Phase = "complete";
        receipt.RemoteValidatedFrontier = await remoteJournal.GetValidatedFrontierAsync().ConfigureAwait(false);
        receipt.RemoteFrontierUnchanged = receipt.RemoteValidatedFrontier == frontier;
        receipt.MinimumObservedFreeSpaceBytes = tracker.MinimumObservedFreeBytes;
        receipt.ResourceSamplerError = tracker.Error?.ToString();
        receipt.PeakLocalStorageBytes = tracker.PeakLocalBytes;
        receipt.PeakRssBytes = Process.GetCurrentProcess().PeakWorkingSet64;
        tracker.ThrowIfExceeded();
        if (!receipt.RemoteFrontierUnchanged)
            throw new IOException("The remote frontier changed during recovery; the source prefix was not immutable.");
        await resourceMonitor.StopAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(finalCgroup, JsonOptions)).ConfigureAwait(false);
        await progress.WriteAsync("complete", options.Records!.Value, options.Records.Value, journalObjects).ConfigureAwait(false);
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
        await File.WriteAllTextAsync(summaryPath, CreateSummary(receipt)).ConfigureAwait(false);
        Console.WriteLine($"RustFS existing-prefix recovery summary: {summaryPath}");
        return 0;
    }

    private static async Task VerifyOpenAndSamplesAsync(
        string recoveryRoot,
        TieredStorageSegmentStore tiered,
        RemoteCommitJournal remoteJournal,
        TenantId tenant,
        CollectionId collection,
        PayloadFactory payload,
        long[] samples,
        ResourceMonitor tracker,
        RecoveryReceiptState receipt,
        ProgressWriter progress,
        string phase,
        string receiptPath,
        long records)
    {
        receipt.Phase = phase;
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        await using (var engine = new DurableStorageEngine(
            recoveryRoot,
            new LocalStorageTierPlacement(recoveryRoot),
            StorageWalDurability.Synced,
            tiered,
            remoteJournal))
        {
            foreach (var number in samples)
            {
                var document = await engine.ReadAsync(new DocumentKey(tenant, collection, $"doc-{number:D10}")).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"{phase} could not recover document doc-{number:D10}.");
                VerifyBody(document, number, payload);
                tracker.ThrowIfExceeded();
            }
        }

        timer.Stop();
        receipt.PhaseMeasurements.Add(new RecoveryPhaseMeasurement(phase, timer.Elapsed.TotalSeconds));
        receipt.SampleReadsByPhase[phase] = samples.Length;
        receipt.LatestLocalStorageBytes = DirectoryBytes(recoveryRoot);
        receipt.LatestCacheBytes = DirectoryBytes(Path.Combine(recoveryRoot, "ssd-cache"));
        await progress.WriteAsync(phase, samples.Length, samples.Length, null).ConfigureAwait(false);
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
        if (samples.Length == 0 || records <= 0)
            throw new InvalidDataException("The recovery sample set is empty.");
    }

    private static async Task<long> VerifyExactScanAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId collection,
        PayloadFactory payload,
        long expected,
        ResourceMonitor tracker)
    {
        var count = 0L;
        await foreach (var document in engine.ScanAsync(tenant, collection).ConfigureAwait(false))
        {
            if (count >= expected)
                throw new InvalidDataException("The recovery exact scan returned more records than expected.");
            VerifyBody(document, count, payload);
            count++;
            if ((count & 0xFFFFF) == 0)
                tracker.ThrowIfExceeded();
        }

        if (count != expected)
            throw new InvalidDataException($"The recovery exact scan returned {count:N0} records, expected {expected:N0}.");
        return count;
    }

    private static async Task RebuildWithHeartbeatAsync(
        RemoteCommitJournal journal,
        string root,
        int batchSize,
        ProgressWriter progress,
        ResourceMonitor tracker,
        long expected)
    {
        var recovery = Task.Run(() => RemoteCommitJournalRecovery.RebuildWalFromRemote(journal, root, batchSize));
        using var heartbeatCancellation = new CancellationTokenSource();
        try
        {
            while (!recovery.IsCompleted)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(15), heartbeatCancellation.Token);
                await Task.WhenAny(recovery, delay).ConfigureAwait(false);
                if (recovery.IsCompleted)
                    break;
                tracker.ThrowIfExceeded();
                await progress.WriteAsync("remote-journal-recovery", 0, expected, null).ConfigureAwait(false);
            }

            await recovery.ConfigureAwait(false);
        }
        finally
        {
            heartbeatCancellation.Cancel();
        }
    }

    private static SourceReceipt LoadSourceReceipt(string path)
    {
        var source = JsonSerializer.Deserialize<SourceReceipt>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The source receipt is empty or invalid.");
        if (!string.Equals(source.Status, "complete", StringComparison.Ordinal)
            || source.TargetRecords <= 0
            || source.CompletedRecords != source.TargetRecords
            || source.IngestOnly is not true
            || source.RemoteAck is null
            || source.RemoteJournalObjects is null
            || source.RemoteValidatedFrontier is null
            || source.RemoteAck.AcknowledgedRecords < source.TargetRecords
            || source.RemoteValidatedFrontier.Value != checked((ulong)source.RemoteAck.AcknowledgedRecords)
            || source.RemoteJournalObjects.ObjectCount <= 0
            || source.RemoteJournalObjects.TotalBytes <= 0)
        {
            throw new InvalidDataException(
                "The source receipt is not a complete strict ingest-only receipt whose contiguous remote frontier covers the target records and all internal authority records.");
        }

        RequireText(source.EngineRoot, "EngineRoot");
        RequireText(source.Prefix, "Prefix");
        RequireText(source.SegmentObjectPrefix, "SegmentObjectPrefix");
        RequireText(source.JournalObjectPrefix, "JournalObjectPrefix");
        RequireText(source.JournalFramePrefix, "JournalFramePrefix");
        RequireText(source.DatabaseId, "DatabaseId");
        RequireText(source.WriterId, "WriterId");
        RequireText(source.Region, "Region");
        RequireText(source.S3Endpoint, "S3Endpoint");
        RequireText(source.S3Bucket, "S3Bucket");
        RequireText(source.BenchmarkAssemblySha256, "BenchmarkAssemblySha256");
        if (!EntityId.TryParse(source.DatabaseId, out var databaseId) || databaseId.IsEmpty
            || !EntityId.TryParse(source.WriterId, out var writerId) || writerId.IsEmpty)
            throw new InvalidDataException("The source receipt has invalid database or writer identity metadata.");
        if (source.RemoteObjects is null
            || source.RemoteObjects.ObjectCount != 0
            || source.RemoteObjects.TotalBytes != 0)
            throw new InvalidDataException("The source receipt contains sealed segment objects; this probe requires journal-only ingest data.");
        if (source.Batch <= 0 || source.PayloadBytes <= 0 || string.IsNullOrWhiteSpace(source.PayloadMode))
            throw new InvalidDataException("The source receipt has incomplete payload or batch metadata.");
        return source;
    }

    private static void ResolveAndValidateOptions(Options options, SourceReceipt source, string sourceReceiptPath)
    {
        if (options.Records is null)
            options.Records = source.TargetRecords;
        if (options.Records != source.TargetRecords)
            throw new ArgumentException($"--records {options.Records} does not match source receipt target {source.TargetRecords}.");
        if (options.Prefix is null)
            options.Prefix = source.Prefix;
        if (!string.Equals(options.Prefix!.Trim('/'), source.Prefix!.Trim('/'), StringComparison.Ordinal))
            throw new ArgumentException("--prefix does not match the source receipt prefix.");
        if (options.Batch == 0)
            options.Batch = source.Batch;
        if (options.Batch != source.Batch)
            throw new ArgumentException("--batch does not match the source receipt batch size.");
        if (options.PayloadBytes == 0)
            options.PayloadBytes = source.PayloadBytes;
        if (options.PayloadBytes != source.PayloadBytes)
            throw new ArgumentException("--payload-bytes does not match the source receipt.");
        if (options.PayloadMode is null)
            options.PayloadMode = ParsePayloadMode(source.PayloadMode!);
        if (!string.Equals(PayloadModeNameOf(options.PayloadMode.Value), source.PayloadMode, StringComparison.Ordinal))
            throw new ArgumentException("--payload-mode does not match the source receipt.");
        if (options.Seed is null)
            options.Seed = source.PayloadSeed;
        if (options.Seed != source.PayloadSeed)
            throw new ArgumentException("--seed does not match the source receipt.");
        if (options.Region is null)
            options.Region = source.Region;
        if (!string.Equals(options.Region, source.Region, StringComparison.Ordinal))
            throw new ArgumentException("--s3-region does not match the source receipt region.");
        if (!string.Equals(
                NormalizeEndpoint(options.Endpoint),
                NormalizeEndpoint(source.S3Endpoint!),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--s3-endpoint does not match the source receipt endpoint.");
        if (options.Bucket is null)
            options.Bucket = source.S3Bucket!;
        if (!string.Equals(options.Bucket, source.S3Bucket, StringComparison.Ordinal))
            throw new ArgumentException("--s3-bucket does not match the source receipt bucket.");
        if (string.IsNullOrWhiteSpace(options.SourceReceipt))
            throw new ArgumentException("--source-receipt is required.");
        if (string.Equals(Path.GetFullPath(options.Output), Path.GetFullPath(sourceReceiptPath), StringComparison.Ordinal))
            throw new ArgumentException("--output must be a fresh output directory, not the source receipt path.");
    }

    private static void ValidateRemoteSource(
        SourceReceipt source,
        long expectedRecords,
        ulong frontier,
        SegmentTotals journalObjects,
        SegmentTotals segmentObjects)
    {
        if (frontier < checked((ulong)expectedRecords)
            || frontier != source.RemoteValidatedFrontier
            || frontier != checked((ulong)source.RemoteAck!.AcknowledgedRecords)
            || journalObjects.ObjectCount <= 0
            || journalObjects.TotalBytes <= 0
            || journalObjects.ObjectCount != source.RemoteJournalObjects!.ObjectCount
            || journalObjects.TotalBytes != source.RemoteJournalObjects.TotalBytes
            || segmentObjects.ObjectCount != source.RemoteObjects!.ObjectCount
            || segmentObjects.TotalBytes != source.RemoteObjects.TotalBytes)
        {
            throw new InvalidDataException(
                $"The remote journal changed or is incomplete: frontier={frontier:N0}, recordedFrontier={source.RemoteValidatedFrontier!.Value:N0}, recordedAck={source.RemoteAck!.AcknowledgedRecords:N0}, objects={journalObjects.ObjectCount:N0}, bytes={journalObjects.TotalBytes:N0}.");
        }
    }

    private static void RequireOwnedSourceRoot(string root, string wal)
    {
        RejectSymlinkAncestors(root);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The benchmark-owned source engine root is missing or unsafe: '{root}'.");
        if (!File.Exists(wal) || (File.GetAttributes(wal) & FileAttributes.ReparsePoint) != 0 || new FileInfo(wal).Length <= 0)
            throw new IOException($"The benchmark-owned source WAL is missing or empty: '{wal}'.");
    }

    private static void DeleteOwnedRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var parent = Directory.GetParent(fullRoot)
            ?? throw new IOException($"The source root has no parent: '{fullRoot}'.");
        if (PathsEqual(parent.FullName, fullRoot))
            throw new IOException($"Refusing to delete filesystem root '{fullRoot}'.");
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to delete a reparse source root: '{fullRoot}'.");
        Directory.Delete(fullRoot, recursive: true);
    }

    private static async Task<SegmentTotals> ListObjectsAsync(AmazonS3Client client, string bucket, string prefix)
    {
        long count = 0;
        long bytes = 0;
        string? continuation = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = continuation,
            }).ConfigureAwait(false);
            foreach (var item in response.S3Objects)
            {
                count = checked(count + 1);
                bytes = checked(bytes + (item.Size ?? 0));
            }
            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuation is not null);
        return new SegmentTotals(count, bytes);
    }

    private static long[] BuildSamples(int seed, long records)
    {
        var target = checked((int)Math.Min(records, MaximumSamples));
        var values = new HashSet<long>();
        values.Add(0);
        if (records > 1)
            values.Add(records - 1);
        for (var index = 0; index < EvenlySpacedSamples && values.Count < target; index++)
        {
            var number = records == 1
                ? 0
                : checked((records - 1) * index / (EvenlySpacedSamples - 1));
            values.Add(number);
        }

        for (var sample = 0; values.Count < target; sample++)
            values.Add(SampleIndex(seed, sample, records));
        return values.OrderBy(static value => value).ToArray();
    }

    private static long SampleIndex(int seed, int sample, long records)
    {
        var value = unchecked((ulong)(uint)seed) ^ (unchecked((ulong)(uint)sample) * 0x9E3779B97F4A7C15UL);
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return (long)(value % (ulong)records);
    }

    private static void VerifyBody(Document document, long number, PayloadFactory payload)
    {
        if (document.Version != 1UL
            || document.Key.DocumentId != $"doc-{number:D10}"
            || document.Body.GetProperty("number").GetInt64() != number
            || document.Body.GetProperty("payload").GetString() != payload.For(number)
            || document.Body.GetProperty("updated").GetBoolean())
        {
            throw new InvalidDataException($"Document doc-{number:D10} does not match the expected recovered body.");
        }
    }

    private static JsonElement CreateBody(long number, PayloadFactory payload) =>
        JsonSerializer.SerializeToElement(new { number, payload = payload.For(number), updated = false });

    private static string CreateSummary(RecoveryReceiptState receipt) => $"""
# RustFS existing-prefix recovery probe

| Measurement | Result |
| --- | --- |
| Status | `{receipt.Status}` |
| Mode | `{receipt.Mode}` |
| Source receipt | `{receipt.SourceReceiptPath}` |
| Source receipt SHA-256 | `{receipt.SourceReceiptSha256}` |
| Source receipt unchanged through root loss | `{receipt.SourceReceiptUnchanged}` |
| Source root deleted | `{receipt.SourceRootDeleted}` |
| Expected records | {receipt.ExpectedRecords:N0} |
| Source remote acknowledged records | {receipt.SourceRemoteAckRecords:N0} |
| Source internal authority records | {receipt.SourceInternalAuthorityRecords:N0} |
| Remote validated frontier | {receipt.RemoteValidatedFrontier:N0} |
| Recovered WAL bytes | {receipt.RecoveredWalBytes:N0} |
| Deterministic samples | {receipt.SampleCount:N0} |
| First/last sampled ids | {receipt.SampleFirstId}/{receipt.SampleLastId} |
| Exact scan | `{receipt.ExactScan}` |
| Verification scope | `{receipt.VerificationScope}` |
| First reopen sample reads | {receipt.SampleReadsByPhase.GetValueOrDefault("first-reopen")} |
| Second reopen sample reads | {receipt.SampleReadsByPhase.GetValueOrDefault("second-reopen")} |
| Peak local bytes | {receipt.PeakLocalStorageBytes:N0} |
| Peak process RSS | {receipt.PeakRssBytes:N0} |
| Cgroup memory max | `{receipt.FinalCgroup?.MemoryMax ?? "unavailable"}` |
| Cgroup swap max | `{receipt.FinalCgroup?.SwapMax ?? "unavailable"}` |
| Cgroup memory events | `{receipt.FinalCgroup?.MemoryEvents?.Replace("\n", "; ") ?? "unavailable"}` |
| Remote journal objects | {receipt.RemoteJournalObjects?.ObjectCount:N0} ({receipt.RemoteJournalObjects?.TotalBytes:N0} bytes) |

The probe verifies a source-root loss, bounded remote journal replay, two fresh engine opens, and exact deterministic point-read bodies. Verification scope is `{receipt.VerificationScope}`; full exact scan remains `{receipt.ExactScan}`.
""";

    private static string RequireRegularFile(string path, string label)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The {label} must be a regular file: '{full}'.");
        return full;
    }

    private static string ComputeSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string NormalizeEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new ArgumentException($"The S3 endpoint is not an absolute URI: '{value}'.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static void RejectSymlinkAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"The path or one of its ancestors is a reparse point: '{current}'.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            var parent = Directory.GetParent(current);
            if (parent is null || PathsEqual(parent.FullName, current))
                return;
            current = parent.FullName;
        }
    }

    private static bool IsPathInside(string path, string parent)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedParent, GetPathComparison());
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            GetPathComparison());

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static long DirectoryBytes(string root)
    {
        if (!Directory.Exists(root))
            return 0;
        long total = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { total = checked(total + new FileInfo(path).Length); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        return total;
    }

    private static OutputMount ValidateOutputMount(string output, long minimumFreeSpace)
    {
        var mount = Path.GetPathRoot(Path.GetFullPath(output))
            ?? throw new IOException($"Could not determine the output mount for '{output}'.");
        var drive = new DriveInfo(mount);
        if (!drive.IsReady || drive.TotalSize <= 0 || minimumFreeSpace > drive.TotalSize)
            throw new IOException($"The output mount '{mount}' cannot satisfy the requested free-space floor.");
        return new OutputMount(mount, drive.TotalSize);
    }

    private static Task SaveReceiptAsync(string path, RecoveryReceiptState receipt) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(receipt, JsonOptions));

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"The source receipt is missing {name}.");
    }

    private static string ReadWorkload(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--workload=", StringComparison.OrdinalIgnoreCase))
                return args[index]["--workload=".Length..].Trim().ToLowerInvariant();
            if (args[index].Equals("--workload", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return args[index + 1].Trim().ToLowerInvariant();
        }
        return string.Empty;
    }

    private static string PayloadGeneratorName(PayloadMode mode) => mode switch
    {
        PayloadMode.Repeated => "repeated-x",
        PayloadMode.PerKeyIncompressible => "sha256-base64url-per-key",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static PayloadMode ParsePayloadMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "repeated" => PayloadMode.Repeated,
        "per-key-incompressible" => PayloadMode.PerKeyIncompressible,
        _ => throw new InvalidDataException($"Unknown source payload mode '{value}'."),
    };

    private static bool IsUsefulIsolatedCgroup(BenchmarkCgroupSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.Path)
        && long.TryParse(snapshot.MemoryMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
        && limit > 0;

    private enum PayloadMode { Repeated, PerKeyIncompressible }

    private sealed class PayloadFactory
    {
        private readonly int payloadBytes;
        private readonly PayloadMode mode;
        private readonly int seed;
        private readonly string repeatedPayload;

        public PayloadFactory(int payloadBytes, PayloadMode mode, int seed)
        {
            this.payloadBytes = payloadBytes;
            this.mode = mode;
            this.seed = seed;
            repeatedPayload = new string('x', payloadBytes);
        }

        public string For(long number) => mode switch
        {
            PayloadMode.Repeated => repeatedPayload,
            PayloadMode.PerKeyIncompressible => CreateIncompressibleBody(payloadBytes, seed, number),
            _ => throw new InvalidOperationException(),
        };

        private static string CreateIncompressibleBody(int payloadBytes, int seed, long number)
        {
            var keyBytes = Encoding.UTF8.GetBytes($"doc-{number:D10}");
            Span<byte> seedBytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(seedBytes, seed);
            Span<byte> blockBytes = stackalloc byte[sizeof(int)];
            var body = new StringBuilder(payloadBytes);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var block = 0; body.Length < payloadBytes; block++)
            {
                hash.AppendData(seedBytes);
                hash.AppendData(keyBytes);
                BinaryPrimitives.WriteInt32LittleEndian(blockBytes, block);
                hash.AppendData(blockBytes);
                var encoded = Convert.ToBase64String(hash.GetHashAndReset());
                for (var index = 0; index < encoded.Length && body.Length < payloadBytes; index++)
                    body.Append(encoded[index] switch { '+' => '-', '/' => '_', '=' => '.', var value => value });
            }
            return body.ToString();
        }
    }

    private sealed class ProgressWriter : IAsyncDisposable
    {
        private readonly StreamWriter writer;

        public ProgressWriter(string path) => writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous));

        public async Task WriteAsync(string phase, long completed, long total, SegmentTotals? remote)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow,
                phase,
                completed,
                total,
                remoteJournalObjectCount = remote?.ObjectCount,
                remoteJournalBytes = remote?.TotalBytes,
                cgroup = BenchmarkCgroupSnapshot.Capture(),
            })).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() { writer.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ResourceMonitor : IAsyncDisposable
    {
        private readonly string root;
        private readonly string cache;
        private readonly string mount;
        private readonly long? maxLocalStorageBytes;
        private readonly long minimumFreeSpaceBytes;
        private readonly string path;
        private readonly CancellationTokenSource cancellation = new();
        private Task? worker;
        private StreamWriter? writer;
        private int stopped;

        public ResourceMonitor(string root, string cache, string mount, long? maxLocalStorageBytes, long minimumFreeSpaceBytes, string path)
        {
            this.root = root;
            this.cache = cache;
            this.mount = mount;
            this.maxLocalStorageBytes = maxLocalStorageBytes;
            this.minimumFreeSpaceBytes = minimumFreeSpaceBytes;
            this.path = path;
        }

        public long PeakLocalBytes { get; private set; }
        public long PeakRssBytes { get; private set; }
        public long? MinimumObservedFreeBytes { get; private set; }
        public Exception? Error { get; private set; }

        public async Task StartAsync()
        {
            writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous));
            worker = RunAsync();
            await Task.Yield();
        }

        public void ThrowIfExceeded()
        {
            if (Error is not null)
                throw new IOException("The recovery resource sampler failed.", Error);
            var local = DirectoryBytes(root);
            if (maxLocalStorageBytes is long limit && local > limit)
                throw new IOException($"Recovery local storage exceeded {limit:N0} bytes: {local:N0}.");
            var free = new DriveInfo(mount).AvailableFreeSpace;
            if (minimumFreeSpaceBytes > 0 && free < minimumFreeSpaceBytes)
                throw new IOException($"Recovery free space fell below {minimumFreeSpaceBytes:N0} bytes: {free:N0}.");
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
                return;
            cancellation.Cancel();
            if (worker is not null)
                await worker.ConfigureAwait(false);
            if (writer is not null)
                await writer.DisposeAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }

        public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

        private async Task RunAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var local = DirectoryBytes(root);
                    var cacheBytes = DirectoryBytes(cache);
                    var free = new DriveInfo(mount).AvailableFreeSpace;
                    PeakLocalBytes = Math.Max(PeakLocalBytes, local);
                    PeakRssBytes = Math.Max(PeakRssBytes, Process.GetCurrentProcess().PeakWorkingSet64);
                    MinimumObservedFreeBytes = MinimumObservedFreeBytes is null ? free : Math.Min(MinimumObservedFreeBytes.Value, free);
                    await writer!.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        utc = DateTimeOffset.UtcNow,
                        localStorageBytes = local,
                        ssdCacheBytes = cacheBytes,
                        availableFreeSpaceBytes = free,
                        workingSetBytes = Process.GetCurrentProcess().WorkingSet64,
                        peakRssBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                        cgroup = BenchmarkCgroupSnapshot.Capture(),
                    })).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception) { Error = exception; }
        }
    }

    private sealed record OutputMount(string Name, long TotalSize);
    private sealed record SegmentTotals(long ObjectCount, long TotalBytes);
    private sealed record RecoveryPhaseMeasurement(string Name, double DurationSeconds);

    private sealed class SourceReceipt
    {
        public string? Status { get; set; }
        public long TargetRecords { get; set; }
        public long CompletedRecords { get; set; }
        public bool IngestOnly { get; set; }
        public string? EngineRoot { get; set; }
        public string? Prefix { get; set; }
        public string? SegmentObjectPrefix { get; set; }
        public string? JournalObjectPrefix { get; set; }
        public string? JournalFramePrefix { get; set; }
        public string? DatabaseId { get; set; }
        public string? WriterId { get; set; }
        public string? Region { get; set; }
        public string? S3Endpoint { get; set; }
        public string? S3Bucket { get; set; }
        public int Batch { get; set; }
        public int PayloadBytes { get; set; }
        public string? PayloadMode { get; set; }
        public int PayloadSeed { get; set; }
        public string? Tenant { get; set; }
        public string? Collection { get; set; }
        public string? BenchmarkAssembly { get; set; }
        public string? BenchmarkAssemblySha256 { get; set; }
        public SourceSubject? Subject { get; set; }
        public RemoteAck? RemoteAck { get; set; }
        public SegmentTotals? RemoteJournalObjects { get; set; }
        public SegmentTotals? RemoteObjects { get; set; }
        public ulong? RemoteValidatedFrontier { get; set; }
    }

    private sealed record RemoteAck(long AcknowledgedBatches, long AcknowledgedRecords, long AcknowledgedRecordBytes);
    private sealed record SourceSubject(string? Commit, string? TreeDigest, bool Dirty, string? WorkingTreeDigest);

    private sealed class RecoveryReceiptState
    {
        public string Status { get; set; } = "running";
        public string Phase { get; set; } = "initializing";
        public string Mode { get; set; } = "recover-existing";
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public string? SourceReceiptPath { get; set; }
        public string? SourceReceiptSha256 { get; set; }
        public bool SourceReceiptUnchanged { get; set; }
        public string? SourceOutput { get; set; }
        public string? SourceEngineRoot { get; set; }
        public string? RecoveryOutput { get; set; }
        public string? RecoveryEngineRoot { get; set; }
        public long ExpectedRecords { get; set; }
        public string? Prefix { get; set; }
        public string? SegmentObjectPrefix { get; set; }
        public string? JournalObjectPrefix { get; set; }
        public string? JournalFramePrefix { get; set; }
        public string? DatabaseId { get; set; }
        public string? WriterId { get; set; }
        public string? Region { get; set; }
        public string? S3Endpoint { get; set; }
        public string? S3Bucket { get; set; }
        public int Batch { get; set; }
        public string? PayloadMode { get; set; }
        public string? PayloadGenerator { get; set; }
        public int PayloadBytes { get; set; }
        public int PayloadSeed { get; set; }
        public string? BenchmarkAssembly { get; set; }
        public string? BenchmarkAssemblySha256 { get; set; }
        public string? SourceBenchmarkAssembly { get; set; }
        public string? SourceBenchmarkAssemblySha256 { get; set; }
        public string? SubjectProject { get; set; }
        public string? SubjectCommit { get; set; }
        public string? SubjectTreeDigest { get; set; }
        public string? SubjectWorkingTreeDigest { get; set; }
        public bool SubjectDirty { get; set; }
        public string? SourceSubjectCommit { get; set; }
        public string? SourceSubjectTreeDigest { get; set; }
        public string? SourceSubjectWorkingTreeDigest { get; set; }
        public bool SourceSubjectDirty { get; set; }
        public bool RequireIsolatedCgroup { get; set; }
        public bool? IsolatedCgroup { get; set; }
        public long? MaxLocalStorageBytes { get; set; }
        public long MinimumFreeSpaceBytes { get; set; }
        public string? OutputMount { get; set; }
        public string? ResourceSamplesPath { get; set; }
        public string? ProgressPath { get; set; }
        public string ExactScan { get; set; } = "skipped";
        public string VerificationScope { get; set; } = "sampled-remote-journal-recovery";
        public long? ExactScanCount { get; set; }
        public BenchmarkCgroupSnapshot? InitialCgroup { get; set; }
        public BenchmarkCgroupSnapshot? FinalCgroup { get; set; }
        public bool SourceRootDeleted { get; set; }
        public long SourceWalBytes { get; set; }
        public long RecoveredWalBytes { get; set; }
        public ulong RecoveredFrontier { get; set; }
        public ulong RemoteValidatedFrontier { get; set; }
        public double RemoteFrontierValidationSeconds { get; set; }
        public bool RemoteFrontierUnchanged { get; set; }
        public SegmentTotals? RemoteJournalObjects { get; set; }
        public SegmentTotals? RemoteSegmentObjects { get; set; }
        public long SourceRemoteAckRecords { get; set; }
        public long SourceInternalAuthorityRecords { get; set; }
        public int SampleCount { get; set; }
        public long SampleFirstId { get; set; }
        public long SampleLastId { get; set; }
        public int EvenlySpacedSampleCount { get; set; }
        public Dictionary<string, int> SampleReadsByPhase { get; set; } = new(StringComparer.Ordinal);
        public long LatestLocalStorageBytes { get; set; }
        public long LatestCacheBytes { get; set; }
        public long PeakLocalStorageBytes { get; set; }
        public long PeakRssBytes { get; set; }
        public long? MinimumObservedFreeSpaceBytes { get; set; }
        public string? ResourceSamplerError { get; set; }
        public List<RecoveryPhaseMeasurement> PhaseMeasurements { get; set; } = [];
        public string? Error { get; set; }
    }

    private sealed class Options
    {
        public string Endpoint { get; private set; } = Environment.GetEnvironmentVariable("TERRABASE_S3_ENDPOINT") ?? "";
        public string? Bucket { get; set; } = Environment.GetEnvironmentVariable("TERRABASE_S3_BUCKET");
        public string AccessKey { get; private set; } = Environment.GetEnvironmentVariable("TERRABASE_S3_ACCESS_KEY") ?? "";
        public string SecretKey { get; private set; } = Environment.GetEnvironmentVariable("TERRABASE_S3_SECRET_KEY") ?? "";
        public string? Region { get; set; } = Environment.GetEnvironmentVariable("TERRABASE_S3_REGION");
        public string? Prefix { get; set; }
        public string Output { get; private set; } = Path.Combine("bench", "receipts", $"tiered-rustfs-recovery-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");
        public string? SourceReceipt { get; private set; }
        public string? ExpectedSourceReceiptSha256 { get; private set; }
        public long? Records { get; set; }
        public int Batch { get; set; }
        public int PayloadBytes { get; set; }
        public PayloadMode? PayloadMode { get; set; }
        public int? Seed { get; set; }
        public long CacheBytes { get; private set; } = 16L * 1024 * 1024;
        public long? MaxLocalStorageBytes { get; private set; }
        public long MinFreeSpaceBytes { get; private set; }
        public int ResourceSampleIntervalMilliseconds { get; private set; } = 500;
        public bool RequireIsolatedCgroup { get; private set; }
        public bool ExactScan { get; private set; }
        public bool RecoverExisting { get; private set; }
        public bool Help { get; private set; }
        public string PayloadModeName => PayloadModeNameOf(PayloadMode ?? throw new InvalidOperationException("Payload mode has not been resolved."));

        public static Options Parse(string[] args)
        {
            var result = new Options();
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument is "--help" or "-h") { result.Help = true; continue; }
                if (!argument.StartsWith("--", StringComparison.Ordinal)) continue;
                var separator = argument.IndexOf('=');
                var name = separator > 2 ? argument[2..separator] : argument[2..];
                if (separator < 0 && name.Equals("recover-existing", StringComparison.OrdinalIgnoreCase)) { result.RecoverExisting = true; continue; }
                if (separator < 0 && name.Equals("exact-scan", StringComparison.OrdinalIgnoreCase)) { result.ExactScan = true; continue; }
                if (separator < 0 && name.Equals("require-isolated-cgroup", StringComparison.OrdinalIgnoreCase)) { result.RequireIsolatedCgroup = true; continue; }
                var value = separator > 2 ? argument[(separator + 1)..] : index + 1 < args.Length ? args[++index] : throw new ArgumentException($"--{name} requires a value.");
                switch (name.ToLowerInvariant())
                {
                    case "workload": break;
                    case "recover-existing": result.RecoverExisting = ParseBool(value, name); break;
                    case "source-receipt": result.SourceReceipt = value; break;
                    case "source-receipt-sha256": result.ExpectedSourceReceiptSha256 = value; break;
                    case "output": result.Output = value; break;
                    case "records": result.Records = ParsePositiveLong(value, name); break;
                    case "batch": result.Batch = ParsePositive(value, name); break;
                    case "payload-bytes": result.PayloadBytes = ParsePositive(value, name); break;
                    case "payload-mode": result.PayloadMode = ParsePayloadMode(value); break;
                    case "seed": result.Seed = ParseInt(value, name); break;
                    case "prefix": result.Prefix = value; break;
                    case "s3-endpoint": result.Endpoint = value; break;
                    case "s3-bucket": result.Bucket = value; break;
                    case "s3-region": result.Region = value; break;
                    case "cache-bytes": result.CacheBytes = ParseNonNegativeLong(value, name); break;
                    case "max-local-storage-bytes": result.MaxLocalStorageBytes = ParseNonNegativeLong(value, name); break;
                    case "min-free-space-bytes": result.MinFreeSpaceBytes = ParseNonNegativeLong(value, name); break;
                    case "resource-sample-ms": result.ResourceSampleIntervalMilliseconds = ParsePositive(value, name); break;
                    case "require-isolated-cgroup": result.RequireIsolatedCgroup = ParseBool(value, name); break;
                    case "exact-scan": result.ExactScan = ParseBool(value, name); break;
                    case "s3-access-key":
                    case "s3-secret-key": throw new ArgumentException("Provide S3 credentials through TERRABASE_S3_ACCESS_KEY and TERRABASE_S3_SECRET_KEY.");
                    default: throw new ArgumentException($"Unknown option '--{name}'.");
                }
            }
            if (!result.Help)
            {
                if (string.IsNullOrWhiteSpace(result.SourceReceipt)) throw new ArgumentException("--source-receipt is required.");
                if (result.ExpectedSourceReceiptSha256 is not null
                    && (result.ExpectedSourceReceiptSha256.Length != 64
                        || result.ExpectedSourceReceiptSha256.Any(static character => !Uri.IsHexDigit(character))))
                    throw new ArgumentException("--source-receipt-sha256 must be a 64-character hexadecimal SHA-256 digest.");
                if (!Uri.TryCreate(result.Endpoint, UriKind.Absolute, out _)
                    || string.IsNullOrWhiteSpace(result.AccessKey)
                    || string.IsNullOrWhiteSpace(result.SecretKey))
                    throw new ArgumentException("Recovery requires S3 endpoint and credentials through TERRABASE_S3_* environment variables.");
                if (string.IsNullOrWhiteSpace(result.Bucket)) throw new ArgumentException("S3 bucket is required.");
            }
            return result;
        }

        public static void PrintHelp() => Console.WriteLine("--workload tiered-segment --recover-existing --source-receipt PATH [--source-receipt-sha256 HEX] --records N --output DIR [--prefix P] [--batch N] [--payload-bytes N] [--payload-mode repeated|per-key-incompressible] [--seed N] [--exact-scan] --s3-endpoint URL --s3-bucket NAME (credentials: TERRABASE_S3_ACCESS_KEY / TERRABASE_S3_SECRET_KEY)");

        private static bool ParseBool(string value, string name) => bool.TryParse(value, out var result) ? result : throw new ArgumentException($"--{name} must be true or false.");
        private static int ParsePositive(string value, string name) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0 ? result : throw new ArgumentException($"--{name} must be positive.");
        private static int ParseInt(string value, string name) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new ArgumentException($"--{name} must be a 32-bit integer.");
        private static long ParsePositiveLong(string value, string name) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0 ? result : throw new ArgumentException($"--{name} must be positive.");
        private static long ParseNonNegativeLong(string value, string name) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : throw new ArgumentException($"--{name} must be non-negative.");
    }

    private static string PayloadModeNameOf(PayloadMode mode) => mode == PayloadMode.Repeated ? "repeated" : "per-key-incompressible";
}
