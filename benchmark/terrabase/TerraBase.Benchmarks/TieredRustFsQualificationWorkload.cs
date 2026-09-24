using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TerraBase.Backup;
using TerraBase.Storage.ChangeFeed;
using TerraBase.StorageEngine;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>
/// Exercises the live StorageEngine segment path with RustFS as the immutable
/// object tier and a bounded local SSD cache.
/// </summary>
public static class TieredRustFsQualificationWorkload
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    public static bool IsRequested(string[] args) => ReadWorkload(args) is "tiered-segment" or "rustfs-tiered";

    public static async Task<int> RunAsync(string[] args)
    {
        Options? options = null;
        QualificationReceiptState? receipt = null;
        string? receiptPath = null;
        DiskUsageTracker? diskUsage = null;
        StorageStateMapFlushTrace? stateMapFlushTrace = null;
        Action<StorageStateMapFlushMeasurement>? previousStateMapFlushObserver = null;
        var stateMapFlushObserverInstalled = false;
        try
        {
            options = Options.Parse(args);
            if (options.Help)
            {
                Options.PrintHelp();
                return 0;
            }
            if (typeof(TieredRustFsQualificationWorkload).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration != "Release")
                throw new InvalidOperationException("Tiered qualification requires a Release artifact.");

            RejectSymlinkAncestors(options.Output);
            Directory.CreateDirectory(options.Output);
            if (options.StateMapFlushTrace)
            {
                stateMapFlushTrace = new StorageStateMapFlushTrace(
                    Path.Combine(options.Output, "state-map-flush-trace.json"));
                previousStateMapFlushObserver = StorageStateMapDiagnostics.FlushObserver;
                StorageStateMapDiagnostics.FlushObserver = stateMapFlushTrace.Observe;
                stateMapFlushObserverInstalled = true;
            }
            receiptPath = Path.Combine(options.Output, "tiered-rustfs-receipt.json");
            var root = Path.Combine(options.Output, "engine");
            var cache = Path.Combine(root, "ssd-cache");
            var outputMount = ValidateOutputMount(options.Output, options.MinFreeSpaceBytes);
            diskUsage = new DiskUsageTracker(root, cache, options.MaxLocalStorageBytes, options.MinFreeSpaceBytes, outputMount);
            var envelopePath = Path.Combine(options.Output, "resource-envelope.json");
            var resourceSamplesPath = Path.Combine(options.Output, "resource-samples.jsonl");
            var subject = RunReceipt.CaptureSubject();
            var toolchain = ToolchainInfo.Capture();
            var benchmarkAssembly = typeof(TieredRustFsQualificationWorkload).Assembly.Location;
            var benchmarkAssemblySha256 = File.Exists(benchmarkAssembly)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(benchmarkAssembly))).ToLowerInvariant()
                : null;
            receipt = new QualificationReceiptState
            {
                Status = "running",
                Phase = "initializing",
                StartedAtUtc = DateTimeOffset.UtcNow,
                TargetRecords = options.Records,
                SubjectProject = subject.Project,
                Subject = subject,
                Toolchain = toolchain,
                BenchmarkAssembly = benchmarkAssembly,
                BenchmarkAssemblySha256 = benchmarkAssemblySha256,
                ResourceEnvelopePath = Path.GetFullPath(envelopePath),
                ResourceSamplesPath = Path.GetFullPath(resourceSamplesPath),
                ProgressPath = Path.GetFullPath(Path.Combine(options.Output, "progress.jsonl")),
                PayloadMode = options.PayloadModeName,
                PayloadGenerator = PayloadFactory.GeneratorName(options.PayloadMode),
                PayloadSeed = options.Seed,
                IngestOnly = options.IngestOnly,
                RequireIsolatedCgroup = options.RequireIsolatedCgroup,
                MaxLocalStorageBytes = options.MaxLocalStorageBytes,
                MinimumFreeSpaceBytes = options.MinFreeSpaceBytes,
                OutputMount = outputMount.Name,
                EngineRoot = Path.GetFullPath(root),
                Prefix = options.Prefix.Trim('/'),
                SegmentObjectPrefix = RemoteObjectPrefix(options),
                S3Endpoint = options.Endpoint,
                S3Bucket = options.Bucket,
                Region = options.Region,
                Batch = options.Batch,
                PayloadBytes = options.PayloadBytes,
                Tenant = "tiered-qualification",
                Collection = "documents",
            };
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            var initialEnvelope = BenchmarkCgroupSnapshot.Capture();
            receipt.InitialCgroup = initialEnvelope;
            receipt.IsolatedCgroup = IsUsefulIsolatedCgroup(initialEnvelope);
            if (options.RequireIsolatedCgroup && receipt.IsolatedCgroup is not true)
            {
                throw new InvalidOperationException(
                    "--require-isolated-cgroup requires a finite cgroup memory.max and a readable cgroup path; run the benchmark in an isolated cgroup.");
            }
            await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(initialEnvelope)).ConfigureAwait(false);
            await using var sampler = diskUsage.StartPeriodicSampling(
                resourceSamplesPath,
                TimeSpan.FromMilliseconds(options.ResourceSampleIntervalMilliseconds));
            receipt.LatestResources = ResourceSample.Capture("initializing", 0, initialEnvelope, diskUsage.Sample("initializing", 0), null);
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            diskUsage.ThrowIfExceeded();
            await using var progress = new ProgressLog(receipt.ProgressPath);
            await progress.WriteAsync("initializing", 0, options.Records, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp()), diskUsage.Current, null).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            var endpoint = new Uri(options.Endpoint, UriKind.Absolute);
            using var client = new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey),
                new AmazonS3Config
                {
                    ServiceURL = endpoint.ToString().TrimEnd('/'),
                    ForcePathStyle = true,
                    AuthenticationRegion = options.Region,
                });
            try
            {
                await client.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }).ConfigureAwait(false);
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode is System.Net.HttpStatusCode.Conflict)
            {
            }

            var existingRemote = await ListRemoteObjectsAsync(client, options).ConfigureAwait(false);
            if (existingRemote.ObjectCount != 0)
            {
                throw new IOException(
                    $"RustFS object prefix '{RemoteObjectPrefix(options)}' is not empty ({existingRemote.ObjectCount:N0} objects); use a fresh --prefix.");
            }

            var objectStore = new S3NativeBackupObjectStore(client, options.Bucket, maxAttempts: 3,
                checkpointStore: new FileBackupUploadCheckpointStore(Path.Combine(options.Output, "uploads")));
            if (Directory.Exists(root)) throw new IOException("Use a fresh output directory for each qualification run.");
            var databaseId = EntityId.New();
            var writerId = EntityId.New();
            var remoteJournal = new MeasuredRemoteCommitJournal(
                new RemoteCommitJournal(objectStore, databaseId, options.Region, writerId, JournalObjectPrefix(options)));
            receipt.DatabaseId = databaseId.ToString();
            receipt.WriterId = writerId.ToString();
            receipt.JournalFramePrefix = remoteJournal.FramePrefix;
            receipt.JournalObjectPrefix = JournalObjectPrefix(options);
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            var tiered = CreateTieredStore(objectStore, cache, options);
            var payload = new PayloadFactory(options.PayloadBytes, options.PayloadMode, options.Seed);
            var tenant = new TenantId("tiered-qualification");
            var collection = new CollectionId("documents");
            var writeTimer = Stopwatch.StartNew();
            var compactionTimer = new Stopwatch();
            var phaseMetrics = new List<PhaseMeasurement>();
            StorageCompactionResult initialCompaction;
            StorageCommitRequest? seedRetryRequest = null;
            var nextCheckpoint = options.CheckpointRecords;
            var nextPeriodicCompaction = options.CompactEveryRecords;
            Console.WriteLine($"phase: seed {options.Records:N0} records");
            diskUsage.SetContext("seed", 0);
            receipt.Phase = "seed";
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            await using (var engine = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered, remoteJournal))
            {
                for (long offset = 0; offset < options.Records; offset += options.Batch)
                {
                    var count = (int)Math.Min((long)options.Batch, options.Records - offset);
                    var mutations = new StorageMutation[count];
                    for (var index = 0; index < count; index++)
                    {
                        var number = offset + (long)index;
                        mutations[index] = StorageMutation.Upsert(
                            new DocumentKey(tenant, collection, $"doc-{number:D10}"),
                            CreateBody(number, payload, updated: false),
                            DocumentPrecondition.Missing);
                    }

                    var completed = offset + count;
                    var batchRequest = completed == options.Records
                        ? seedRetryRequest ??= CreateRetryRequest("seed", offset, count, options.Seed)
                        : null;
                    var result = batchRequest is null
                        ? await engine.ApplyBatchAsync(mutations, diskUsage.FailureCancellationToken).ConfigureAwait(false)
                        : await engine.ApplyBatchAsync(mutations, batchRequest, diskUsage.FailureCancellationToken).ConfigureAwait(false);
                    if (result.Outcome != StorageBatchOutcome.Committed)
                    {
                        throw new InvalidDataException($"Batch at {offset} did not commit: {result.Outcome}.");
                    }

                    diskUsage.ThrowIfExceeded();

                    if (completed >= nextCheckpoint || completed == options.Records)
                    {
                        diskUsage.Sample("seed", completed);
                        await WriteCheckpointAsync(diskUsage, progress, receiptPath, receipt, "seed", completed, options.Records, started).ConfigureAwait(false);
                        nextCheckpoint = completed > long.MaxValue - options.CheckpointRecords
                            ? long.MaxValue
                            : completed + options.CheckpointRecords;
                    }

                    if (!options.IngestOnly
                        && options.CompactEveryRecords > 0
                        && completed >= nextPeriodicCompaction
                        && completed < options.Records)
                    {
                        var periodicPhase = $"periodic-compaction-{completed}";
                        diskUsage.SetContext(periodicPhase, completed);
                        receipt.Phase = periodicPhase;
                        receipt.CompletedRecords = completed;
                        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
                        var periodicTimer = Stopwatch.StartNew();
                        await MeasureOperationAsync(
                            () => engine.CompactAsync(diskUsage.FailureCancellationToken).AsTask(), diskUsage, periodicPhase).ConfigureAwait(false);
                        periodicTimer.Stop();
                        phaseMetrics.Add(CapturePhase(periodicPhase, completed, periodicTimer.Elapsed, diskUsage, null));
                        nextPeriodicCompaction = completed > long.MaxValue - options.CompactEveryRecords
                            ? long.MaxValue
                            : completed + options.CompactEveryRecords;
                    }
                }

                writeTimer.Stop();
                phaseMetrics.Add(CapturePhase("seed", options.Records, writeTimer.Elapsed, diskUsage, null));
            }

            if (options.IngestOnly)
            {
                return await CompleteIngestOnlyAsync(
                    options,
                    receipt,
                    receiptPath,
                    root,
                    cache,
                    endpoint,
                    client,
                    remoteJournal,
                    sampler,
                    progress,
                    diskUsage,
                    phaseMetrics,
                    started,
                    writeTimer,
                    envelopePath,
                    subject,
                    toolchain,
                    benchmarkAssembly,
                    benchmarkAssemblySha256,
                    stateMapFlushTrace).ConfigureAwait(false);
            }

            // Exercise the acknowledged-write durability contract while the
            // data exists only in the remote commit journal. A complete local
            // root loss must be recoverable before the first compaction.
            Console.WriteLine("phase: full local-root loss recovery before initial compaction");
            var seedRootLossTimer = Stopwatch.StartNew();
            DeleteLocalRoot(root);
            RemoteCommitJournalRecovery.RebuildWalFromRemote(
                remoteJournal,
                root,
                options.Batch);
            tiered = CreateTieredStore(objectStore, cache, options);
            diskUsage.SetContext("seed-root-loss-recovery", 0);
            receipt.Phase = "seed-root-loss-recovery";
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            await using (var recovered = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered, remoteJournal))
            {
                await VerifyExactStateAsync(
                    recovered,
                    tenant,
                    collection,
                    options,
                    payload,
                    updates: 0,
                    deletes: 0,
                    diskUsage: diskUsage,
                    progress: progress,
                    receiptPath: receiptPath,
                    receipt: receipt,
                    phase: "seed-root-loss-scan",
                    started: started).ConfigureAwait(false);
                await VerifyCommittedRetryStatusAsync(recovered, seedRetryRequest, "seed-root-loss-recovery").ConfigureAwait(false);
                seedRootLossTimer.Stop();
                phaseMetrics.Add(CapturePhase("seed-root-loss-recovery", options.Records, seedRootLossTimer.Elapsed, diskUsage, null));
                receipt.SeedRootLossRecoverySeconds = seedRootLossTimer.Elapsed.TotalSeconds;
                Console.WriteLine("phase: compact and publish to S3");
                diskUsage.SetContext("initial-compaction", 0);
                receipt.Phase = "initial-compaction";
                await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
                compactionTimer.Start();
                initialCompaction = await MeasureOperationAsync(
                    () => recovered.CompactAsync(diskUsage.FailureCancellationToken).AsTask(), diskUsage, "initial-compaction").ConfigureAwait(false);
                compactionTimer.Stop();
            }
            var initialRemote = await ListRemoteObjectsAsync(client, options).ConfigureAwait(false);
            phaseMetrics.Add(CapturePhase("initial-compaction-upload", options.Records, compactionTimer.Elapsed, diskUsage, initialRemote));
            receipt.PhaseMetrics = phaseMetrics;
            receipt.RemoteObjects = initialRemote;
            var initialCache = DeleteCacheFiles(cache);
            receipt.Phase = "initial-cache-evicted";
            receipt.RemoteObjects = initialRemote;
            receipt.InitialCacheEviction = initialCache;
            receipt.CacheEviction = initialCache;
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);

            var reopenTimer = Stopwatch.StartNew();
            Console.WriteLine("phase: remote reopen and exact scan");
            diskUsage.SetContext("initial-remote-reopen-scan", 0);
            var first = new DocumentKey(tenant, collection, "doc-0000000000");
            var last = new DocumentKey(tenant, collection, $"doc-{options.Records - 1:D10}");
            var updateTimer = new Stopwatch();
            var deleteTimer = new Stopwatch();
            var finalCompactionTimer = new Stopwatch();
            StorageCompactionResult? finalCompaction = null;
            var hasMutations = options.Updates > 0 || options.Deletes > 0;
            StorageCommitRequest? updateRetryRequest = options.Updates > 0
                ? CreateRetryRequest("updates", options.Updates - Math.Min((long)options.Batch, options.Updates), (int)Math.Min((long)options.Batch, options.Updates), options.Seed)
                : null;
            StorageCommitRequest? deleteRetryRequest = options.Deletes > 0
                ? CreateRetryRequest("deletes", options.Records - Math.Min((long)options.Batch, options.Deletes), (int)Math.Min((long)options.Batch, options.Deletes), options.Seed)
                : null;
            var initialOpenTimer = Stopwatch.StartNew();
            var initialReopened = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered, remoteJournal);
            initialOpenTimer.Stop();
            phaseMetrics.Add(CapturePhase("initial-remote-open", options.Records, initialOpenTimer.Elapsed, diskUsage, initialRemote));
            await using (var reopened = initialReopened)
            {
                var firstDocument = await reopened.ReadAsync(first).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The first document was not recovered from RustFS.");
                var lastDocument = await reopened.ReadAsync(last).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The last document was not recovered from RustFS.");
                VerifyBody(firstDocument, 0, payload, updated: false);
                VerifyBody(lastDocument, options.Records - 1, payload, updated: false);
                var initialScanTimer = Stopwatch.StartNew();
                var scanCount = await VerifyScanAsync(reopened, tenant, collection, options.Records, payload, 0, 0, options.CheckpointRecords, diskUsage, progress, receiptPath, receipt, "initial-scan", started).ConfigureAwait(false);
                initialScanTimer.Stop();
                phaseMetrics.Add(CapturePhase("initial-exact-scan", scanCount, initialScanTimer.Elapsed, diskUsage, initialRemote));
                await VerifyPointReadsAsync(reopened, tenant, collection, options, payload, 0, 0, diskUsage, progress, receiptPath, receipt, "initial-point-reads", started).ConfigureAwait(false);
                if (scanCount != options.Records)
                    throw new InvalidDataException("Remote reopen did not recover the complete corpus.");

                reopenTimer.Stop();
                phaseMetrics.Add(CapturePhase("initial-remote-reopen-scan", options.Records, reopenTimer.Elapsed, diskUsage, initialRemote));
                receipt.CacheBytesAfterInitialVerification = diskUsage.Current.CacheBytes;
                _ = DirectoryBytes(cache);
                var initialEnvelopeAfterRead = BenchmarkCgroupSnapshot.Capture();
                await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(initialEnvelopeAfterRead)).ConfigureAwait(false);
                receipt.Phase = "initial-verification-complete";
                receipt.LatestResources = ResourceSample.Capture(receipt.Phase, options.Records, initialEnvelopeAfterRead, diskUsage.Current, receipt.RemoteObjects);
                await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
                diskUsage.ThrowIfExceeded();

                if (hasMutations)
                {
                    Console.WriteLine($"phase: mutate {options.Updates:N0} updates and {options.Deletes:N0} deletes");
                    diskUsage.SetContext("mutations", 0);
                    receipt.Phase = "mutations";
                    await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
                    nextCheckpoint = options.CheckpointRecords;
                    updateTimer.Start();
                    await ApplyRangeAsync(reopened, tenant, collection, options.Updates, options.Batch, payload, true, options.CheckpointRecords, diskUsage, progress, receiptPath, receipt, "updates", started, updateRetryRequest).ConfigureAwait(false);
                    updateTimer.Stop();
                    phaseMetrics.Add(CapturePhase("updates", options.Updates, updateTimer.Elapsed, diskUsage, receipt.RemoteObjects));
                    deleteTimer.Start();
                    nextCheckpoint = options.CheckpointRecords;
                    await ApplyDeletesAsync(reopened, tenant, collection, options, options.Deletes, options.CheckpointRecords, diskUsage, progress, receiptPath, receipt, started, deleteRetryRequest).ConfigureAwait(false);
                    deleteTimer.Stop();
                    phaseMetrics.Add(CapturePhase("deletes", options.Deletes, deleteTimer.Elapsed, diskUsage, receipt.RemoteObjects));
                }
            }

            if (hasMutations)
            {
                // Repeat the root-loss drill after updates and tombstones have
                // been acknowledged, before any final segment compaction.
                Console.WriteLine("phase: full local-root loss recovery after mutations");
                var mutationRootLossTimer = Stopwatch.StartNew();
                DeleteLocalRoot(root);
                RemoteCommitJournalRecovery.RebuildWalFromRemote(
                    remoteJournal,
                    root,
                    options.Batch);
                tiered = CreateTieredStore(objectStore, cache, options);
                diskUsage.SetContext("mutation-root-loss-recovery", 0);
                receipt.Phase = "mutation-root-loss-recovery";
                await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
                await using (var recovered = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered, remoteJournal))
                {
                    await VerifyExactStateAsync(
                        recovered,
                        tenant,
                        collection,
                        options,
                        payload,
                        options.Updates,
                        options.Deletes,
                        diskUsage,
                        progress,
                        receiptPath,
                        receipt,
                        "mutation-root-loss-scan",
                        started).ConfigureAwait(false);
                    await VerifyCommittedRetryStatusAsync(recovered, updateRetryRequest, "mutation-root-loss-updates").ConfigureAwait(false);
                    await VerifyCommittedRetryStatusAsync(recovered, deleteRetryRequest, "mutation-root-loss-deletes").ConfigureAwait(false);
                    mutationRootLossTimer.Stop();
                    phaseMetrics.Add(CapturePhase("mutation-root-loss-recovery", options.Records - options.Deletes, mutationRootLossTimer.Elapsed, diskUsage, null));
                    receipt.MutationRootLossRecoverySeconds = mutationRootLossTimer.Elapsed.TotalSeconds;
                    receipt.Phase = "final-compaction";
                    await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
                    finalCompactionTimer.Start();
                    finalCompaction = await MeasureOperationAsync(
                        () => recovered.CompactAsync(diskUsage.FailureCancellationToken).AsTask(), diskUsage, "final-compaction").ConfigureAwait(false);
                    finalCompactionTimer.Stop();
                }
                var finalRemote = await ListRemoteObjectsAsync(client, options).ConfigureAwait(false);
                phaseMetrics.Add(CapturePhase("final-compaction-upload", options.Records, finalCompactionTimer.Elapsed, diskUsage, finalRemote));
                var finalCacheEviction = DeleteCacheFiles(cache);
                receipt.Phase = "final-cache-evicted";
                receipt.RemoteObjects = finalRemote;
                receipt.PhaseMetrics = phaseMetrics;
                receipt.FinalCacheEviction = finalCacheEviction;
                receipt.CacheEviction = finalCacheEviction;
                await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            }

            var finalReopenTimer = new Stopwatch();
            if (hasMutations)
            {
                finalReopenTimer.Start();
                Console.WriteLine("phase: final remote reopen and exact scan");
                diskUsage.SetContext("final-remote-reopen-scan", 0);
                var finalOpenTimer = Stopwatch.StartNew();
                var finalReopened = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered, remoteJournal);
                finalOpenTimer.Stop();
                phaseMetrics.Add(CapturePhase("final-remote-open", options.Records, finalOpenTimer.Elapsed, diskUsage, receipt.RemoteObjects));
                await using (var reopened = finalReopened)
                {
                    var finalScanTimer = Stopwatch.StartNew();
                    var finalScanCount = await VerifyScanAsync(reopened, tenant, collection, options.Records - options.Deletes, payload, options.Updates, options.Deletes, options.CheckpointRecords, diskUsage, progress, receiptPath, receipt, "final-scan", started).ConfigureAwait(false);
                    finalScanTimer.Stop();
                    phaseMetrics.Add(CapturePhase("final-exact-scan", finalScanCount, finalScanTimer.Elapsed, diskUsage, receipt.RemoteObjects));
                    await VerifyPointReadsAsync(reopened, tenant, collection, options, payload, options.Updates, options.Deletes, diskUsage, progress, receiptPath, receipt, "final-point-reads", started).ConfigureAwait(false);
                    if (finalScanCount != options.Records - options.Deletes)
                        throw new InvalidDataException($"Final remote reopen returned {finalScanCount:N0} live records, expected {options.Records - options.Deletes:N0}.");
                    finalReopenTimer.Stop();
                }
                phaseMetrics.Add(CapturePhase("final-remote-reopen-scan", options.Records - options.Deletes, finalReopenTimer.Elapsed, diskUsage, receipt.RemoteObjects));
            }
            await sampler.StopAsync().ConfigureAwait(false);
            if (sampler.Error is { } samplingFailure)
                throw new IOException("The resource sampler failed before qualification completed.", samplingFailure);
            diskUsage.ThrowIfExceeded();
            var envelope = BenchmarkCgroupSnapshot.Capture();
            await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(envelope)).ConfigureAwait(false);
            var elapsedWrite = Stopwatch.GetElapsedTime(started);
            var remote = await ListRemoteObjectsAsync(client, options).ConfigureAwait(false);
            var remoteJournalObjects = await ListRemoteObjectsAsync(client, options.Bucket, remoteJournal.FramePrefix).ConfigureAwait(false);
            var cacheBytes = DirectoryBytes(cache);
            var finalStorage = diskUsage.Sample("complete", options.Records);
            diskUsage.ThrowIfExceeded();
            receipt.PhaseMetrics = phaseMetrics;
            receipt.RemoteJournalObjects = remoteJournalObjects;
            receipt.RemoteAck = remoteJournal.Snapshot();
            if (stateMapFlushTrace is not null)
            {
                await stateMapFlushTrace.WriteAsync().ConfigureAwait(false);
                receipt.StateMapFlushTracePath = Path.GetFullPath(stateMapFlushTrace.Path);
            }
            var finalScanCountForSummary = options.Records - options.Deletes;
            var finalReopenSummary = hasMutations
                ? $"{finalReopenTimer.Elapsed.TotalSeconds:F3} s"
                : "skipped (no mutations; initial remote reopen and exact scan is final verification)";
            var phaseRows = string.Join(Environment.NewLine, phaseMetrics.Select(static phase => FormattableString.Invariant(
                $"| {phase.Name} | {phase.CompletedRecords:N0} | {phase.DurationSeconds:F3} | {phase.WorkingSetBytes:N0} | {phase.PeakRssBytes:N0} | {phase.LocalStorageBytes:N0} | {phase.SsdCacheBytes:N0} | {phase.StagingBytes:N0} | {phase.RemoteObjectCount?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown"} | {phase.RemoteObjectBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown"} |")));
            var summary = Path.Combine(options.Output, "tiered-rustfs-summary.md");
            await File.WriteAllTextAsync(summary, $"""
# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `{endpoint}` |
| Bucket | `{options.Bucket}` |
| Subject project | `{subject.Project}` |
| Subject commit | `{subject.Commit}` (tree `{subject.TreeDigest}`, dirty `{subject.Dirty}`, worktree `{subject.WorkingTreeDigest ?? "unknown"}`) |
| Benchmark build | `{toolchain.BuildConfiguration}`; `{toolchain.DotnetVersion}`; `{toolchain.FrameworkDescription}` |
| Benchmark assembly | `{benchmarkAssembly}` (sha256 `{benchmarkAssemblySha256 ?? "unknown"}`) |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Remote commit journal before synced WAL, one device barrier per batch of {options.Batch} |
| Remote acknowledged commit journal | {remoteJournalObjects.ObjectCount:N0} objects ({remoteJournalObjects.TotalBytes:N0} bytes) under `{remoteJournal.FramePrefix}` |
| Remote acknowledgement batches | {remoteJournal.AcknowledgedBatches:N0} ({remoteJournal.AcknowledgedRecords:N0} records; {remoteJournal.AcknowledgedRecordBytes:N0} encoded record bytes) |
| Remote acknowledgement latency | {remoteJournal.TotalAckMilliseconds:F3} ms total; {remoteJournal.AverageAckMilliseconds:F3} ms/batch |
| Remote acknowledgement throughput | {remoteJournal.AcknowledgedRecords / Math.Max(remoteJournal.TotalAckSeconds, 0.000001):F0} records/s; {remoteJournal.AcknowledgedRecordBytes / Math.Max(remoteJournal.TotalAckSeconds, 0.000001):F0} encoded bytes/s |
| Full local-root loss before initial compaction | verified exact scan, point reads, tombstone boundaries, and retry status in {receipt.SeedRootLossRecoverySeconds:F3} s |
| Full local-root loss after mutations | {(hasMutations ? $"verified exact scan, point reads, tombstone boundaries, and retry status in {receipt.MutationRootLossRecoverySeconds:F3} s" : "skipped (no mutations)")} |
| Seed records (exact bodies and versions verified) | {options.Records:N0} |
| Final live records (exact bodies and versions verified) | {finalScanCountForSummary:N0} |
| Payload mode | `{options.PayloadModeName}` |
| Payload generator | `{PayloadFactory.GeneratorName(options.PayloadMode)}` |
| Payload seed | {options.Seed} |
| Payload bytes per record | {options.PayloadBytes:N0} |
| Minimum logical seed payload bytes | {options.Records * (long)options.PayloadBytes:N0} |
| Observed cgroup memory.max | {envelope.MemoryMax ?? "unavailable"} |
| Observed cgroup memory.peak | {envelope.MemoryPeak ?? "unavailable"} |
| Observed cgroup memory.swap.max | {envelope.SwapMax ?? "unavailable"} |
| Isolated cgroup required | {options.RequireIsolatedCgroup} |
| Useful isolated cgroup observed | {IsUsefulIsolatedCgroup(envelope)} |
| Initial published segments | {initialCompaction.PublishedSegments.Count} |
| Initial compaction records | {initialCompaction.RecordsWritten:N0} |
| Optional periodic compaction interval | {(options.CompactEveryRecords == 0 ? "disabled" : options.CompactEveryRecords.ToString("N0", CultureInfo.InvariantCulture))} |
| Updates applied | {options.Updates:N0} |
| Deletes applied and verified absent | {options.Deletes:N0} |
| Final published segments | {finalCompaction?.PublishedSegments.Count ?? initialCompaction.PublishedSegments.Count} |
| Final compaction records | {finalCompaction?.RecordsWritten ?? 0:N0} |
| Synced seed time | {writeTimer.Elapsed.TotalSeconds:F3} s |
| Synced writes/sec | {options.Records / writeTimer.Elapsed.TotalSeconds:F0} |
| Initial compaction and object publication | {compactionTimer.Elapsed.TotalSeconds:F3} s |
| Initial remote reopen and exact scan after SSD cache deletion (engine disposal excluded) | {reopenTimer.Elapsed.TotalSeconds:F3} s |
| Final remote reopen and exact scan after final cache deletion (engine disposal excluded when mutations run) | {finalReopenSummary} |
| Update time | {updateTimer.Elapsed.TotalSeconds:F3} s |
| Delete time | {deleteTimer.Elapsed.TotalSeconds:F3} s |
| Final compaction and object publication | {finalCompactionTimer.Elapsed.TotalSeconds:F3} s |
| Remote objects under prefix | {remote.ObjectCount:N0} ({remote.TotalBytes:N0} bytes) |
| Peak local engine bytes sampled during compaction/reopen | {diskUsage.PeakLocalBytes:N0} |
| Maximum local engine storage cap | {(options.MaxLocalStorageBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "unlimited")} |
| Output filesystem mount | `{outputMount.Name}` ({outputMount.TotalSize:N0} bytes total) |
| Minimum free-space floor | {options.MinFreeSpaceBytes:N0} bytes |
| Minimum free space observed | {(diskUsage.MinimumObservedFreeBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable")} bytes |
| Free-space floor violation | {(diskUsage.FreeSpaceViolation?.ToString() ?? "none")} |
| Peak SSD cache bytes sampled during compaction/reopen | {diskUsage.PeakCacheBytes:N0} |
| Peak staging bytes sampled during compaction/reopen | {diskUsage.PeakStagingBytes:N0} |
| Periodic resource samples | {diskUsage.SampleCount:N0} at {options.ResourceSampleIntervalMilliseconds} ms; `resource-samples.jsonl` |
| State-map flush trace | {(stateMapFlushTrace is null ? "disabled" : Path.GetFileName(stateMapFlushTrace.Path))} |
| SSD cache bytes after final verification | {cacheBytes:N0} |
| SSD cache bytes after initial remote verification | {receipt.CacheBytesAfterInitialVerification:N0} |
| SSD cache bytes evicted before initial reopen | {initialCache.DeletedBytes:N0} |
| Total interval | {elapsedWrite.TotalSeconds:F3} s |
| Peak RSS (process working set) | {Process.GetCurrentProcess().PeakWorkingSet64:N0} bytes |
| Idle SSD cache budget (active leases require working space) | {options.CacheBytes:N0} bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
{phaseRows}

Every acknowledged batch was published to the immutable remote commit journal
before its local WAL append. The two root-loss drills rebuilt the WAL from
that journal and verified exact reads, scans, tombstone boundaries, and retry
receipts before compaction. Segment manifests were committed only after the
RustFS object upload, then the local SSD copies were removed. Reopen therefore
verified the read-through cache refill and segment checksum validation against
RustFS.
The direct StorageEngine surface exercised here provides point reads and
collection scans; indexed query qualification remains in E2eVolumeWorkload,
which owns the automatic-index lifecycle.
Current document, physical sequence, tombstone, and receipt indexes use
disk-backed derived state in this source. Snapshot and backup/restore paths
still have their own working buffers and artifact limits, so this segment run
does not claim arbitrary bounded memory for those paths. The resource envelope
and checkpoint stream are recorded separately. Backup and restore are
qualified by E2eVolumeWorkload; this segment workload intentionally does not
materialize a second corpus-sized backup artifact.
""").ConfigureAwait(false);
            receipt.Status = "complete";
            receipt.Phase = "complete";
            receipt.FinishedAtUtc = DateTimeOffset.UtcNow;
            receipt.CompletedRecords = options.Records;
            receipt.LatestResources = ResourceSample.Capture(receipt.Phase, options.Records, envelope, finalStorage, remote);
            receipt.RemoteObjects = remote;
            receipt.CacheBytesAfterVerification = cacheBytes;
            receipt.PeakLocalStorageBytes = diskUsage.PeakLocalBytes;
            receipt.PeakCacheBytes = diskUsage.PeakCacheBytes;
            receipt.PeakStagingBytes = diskUsage.PeakStagingBytes;
            receipt.PeakRssBytes = Process.GetCurrentProcess().PeakWorkingSet64;
            receipt.ResourceSampleCount = diskUsage.SampleCount;
            receipt.ResourceSamplerError = sampler.Error?.ToString();
            receipt.FinalCgroup = envelope;
            receipt.IsolatedCgroup = IsUsefulIsolatedCgroup(envelope);
            receipt.MinimumObservedFreeSpaceBytes = diskUsage.MinimumObservedFreeBytes;
            receipt.FreeSpaceLimitViolation = diskUsage.FreeSpaceViolation?.ToString();
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            Console.WriteLine($"Tiered RustFS summary: {summary}");
            return 0;
        }
        catch (Exception exception)
        {
            if (receipt is not null && receiptPath is not null)
            {
                receipt.Status = "failed";
                receipt.Phase = "failed";
                receipt.Error = exception.ToString();
                receipt.FinishedAtUtc = DateTimeOffset.UtcNow;
                if (diskUsage is not null)
                {
                    receipt.PeakLocalStorageBytes = diskUsage.PeakLocalBytes;
                    receipt.PeakCacheBytes = diskUsage.PeakCacheBytes;
                    receipt.PeakStagingBytes = diskUsage.PeakStagingBytes;
                    receipt.ResourceSampleCount = diskUsage.SampleCount;
                    if (diskUsage.LimitViolation is LocalStorageLimitExceededException limit)
                    {
                        receipt.LocalStorageLimitExceededBytes = limit.ObservedBytes;
                        receipt.LocalStorageLimitExceededPhase = limit.Phase;
                        receipt.LocalStorageLimitExceededRecords = limit.CompletedRecords;
                    }
                    receipt.MinimumObservedFreeSpaceBytes = diskUsage.MinimumObservedFreeBytes;
                    receipt.FreeSpaceLimitViolation = diskUsage.FreeSpaceViolation?.ToString();
                    receipt.ResourceSamplerError = diskUsage.SamplerError?.ToString();
                }
                if (stateMapFlushTrace is not null)
                {
                    try
                    {
                        await stateMapFlushTrace.WriteAsync().ConfigureAwait(false);
                        receipt.StateMapFlushTracePath = Path.GetFullPath(stateMapFlushTrace.Path);
                    }
                    catch (Exception traceException)
                    {
                        Console.Error.WriteLine($"Could not write state-map flush trace: {traceException.Message}");
                    }
                }
                try { await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false); }
                catch (Exception receiptException) { Console.Error.WriteLine($"Could not write qualification receipt: {receiptException.Message}"); }
            }
            else if (receiptPath is not null && options is not null)
            {
                var preflightReceipt = new QualificationReceiptState
                {
                    Status = "failed",
                    Phase = "preflight",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    TargetRecords = options.Records,
                    PayloadMode = options.PayloadModeName,
                    PayloadGenerator = PayloadFactory.GeneratorName(options.PayloadMode),
                    PayloadSeed = options.Seed,
                    IngestOnly = options.IngestOnly,
                    RequireIsolatedCgroup = options.RequireIsolatedCgroup,
                    MaxLocalStorageBytes = options.MaxLocalStorageBytes,
                    MinimumFreeSpaceBytes = options.MinFreeSpaceBytes,
                    ResourceEnvelopePath = Path.GetFullPath(Path.Combine(options.Output, "resource-envelope.json")),
                    ResourceSamplesPath = Path.GetFullPath(Path.Combine(options.Output, "resource-samples.jsonl")),
                    ProgressPath = Path.GetFullPath(Path.Combine(options.Output, "progress.jsonl")),
                    Error = exception.ToString(),
                };
                if (stateMapFlushTrace is not null)
                {
                    try
                    {
                        await stateMapFlushTrace.WriteAsync().ConfigureAwait(false);
                        preflightReceipt.StateMapFlushTracePath = Path.GetFullPath(stateMapFlushTrace.Path);
                    }
                    catch (Exception traceException)
                    {
                        Console.Error.WriteLine($"Could not write state-map flush trace: {traceException.Message}");
                    }
                }
                try { await SaveReceiptAsync(receiptPath, preflightReceipt).ConfigureAwait(false); }
                catch (Exception receiptException) { Console.Error.WriteLine($"Could not write preflight qualification receipt: {receiptException.Message}"); }
            }
            Console.Error.WriteLine($"Tiered RustFS qualification failed: {exception}");
            return 1;
        }
        finally
        {
            if (stateMapFlushObserverInstalled)
            {
                StorageStateMapDiagnostics.FlushObserver = previousStateMapFlushObserver;
            }
            diskUsage?.Dispose();
        }
    }

    private static async Task<int> CompleteIngestOnlyAsync(
        Options options,
        QualificationReceiptState receipt,
        string receiptPath,
        string root,
        string cache,
        Uri endpoint,
        AmazonS3Client client,
        MeasuredRemoteCommitJournal remoteJournal,
        PeriodicDiskUsageSampler sampler,
        ProgressLog progress,
        DiskUsageTracker diskUsage,
        List<PhaseMeasurement> phaseMetrics,
        long started,
        Stopwatch writeTimer,
        string envelopePath,
        SubjectProvenance subject,
        ToolchainInfo toolchain,
        string benchmarkAssembly,
        string? benchmarkAssemblySha256,
        StorageStateMapFlushTrace? stateMapFlushTrace)
    {
        receipt.Phase = "ingest-only-remote-validation";
        receipt.CompletedRecords = options.Records;
        receipt.SkippedPhases = IngestOnlySkippedPhases();
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);

        var acknowledgedBeforeValidation = remoteJournal.Snapshot();
        if (acknowledgedBeforeValidation.AcknowledgedRecords < options.Records)
        {
            throw new InvalidDataException(
                $"The remote commit journal acknowledged {acknowledgedBeforeValidation.AcknowledgedRecords:N0} physical records, "
                + $"but the ingest requested {options.Records:N0} seed records.");
        }

        var frontierTimer = Stopwatch.StartNew();
        var frontier = await remoteJournal.GetValidatedFrontierAsync().ConfigureAwait(false);
        frontierTimer.Stop();
        var acknowledged = remoteJournal.Snapshot();
        var expectedFrontier = checked((ulong)acknowledged.AcknowledgedRecords);
        if (frontier != expectedFrontier)
        {
            throw new InvalidDataException(
                $"The validated remote journal frontier was {frontier:N0}, expected {expectedFrontier:N0} "
                + $"from {acknowledged.AcknowledgedRecords:N0} acknowledged physical records.");
        }

        var remote = await ListRemoteObjectsAsync(client, options).ConfigureAwait(false);
        var remoteJournalObjects = await ListRemoteObjectsAsync(client, options.Bucket, remoteJournal.FramePrefix).ConfigureAwait(false);
        var walPath = Path.Combine(root, "wal");
        if (!File.Exists(walPath))
        {
            throw new IOException($"The ingest-only engine root does not contain its WAL after the engine closed: '{walPath}'.");
        }

        var localWalBytes = new FileInfo(walPath).Length;
        if (localWalBytes <= 0)
        {
            throw new IOException("The ingest-only engine WAL is empty after acknowledged seed writes.");
        }

        phaseMetrics.Add(CapturePhase(
            "ingest-only-remote-validation",
            options.Records,
            frontierTimer.Elapsed,
            diskUsage,
            remoteJournalObjects));
        diskUsage.SetContext("ingest-only-complete", options.Records);
        var finalStorage = diskUsage.Sample("ingest-only-complete", options.Records);
        diskUsage.ThrowIfExceeded();
        var envelope = BenchmarkCgroupSnapshot.Capture();
        await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(envelope)).ConfigureAwait(false);
        await progress.WriteAsync(
            "ingest-only-complete",
            options.Records,
            options.Records,
            Stopwatch.GetElapsedTime(started),
            finalStorage,
            remoteJournalObjects).ConfigureAwait(false);
        await sampler.StopAsync().ConfigureAwait(false);
        if (sampler.Error is { } samplingFailure)
        {
            throw new IOException("The resource sampler failed before ingest-only qualification completed.", samplingFailure);
        }

        diskUsage.ThrowIfExceeded();
        var cacheBytes = DirectoryBytes(cache);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var summary = Path.Combine(options.Output, "tiered-rustfs-summary.md");
        var phaseRows = string.Join(Environment.NewLine, phaseMetrics.Select(static phase => FormattableString.Invariant(
            $"| {phase.Name} | {phase.CompletedRecords:N0} | {phase.DurationSeconds:F3} | {phase.WorkingSetBytes:N0} | {phase.PeakRssBytes:N0} | {phase.LocalStorageBytes:N0} | {phase.SsdCacheBytes:N0} | {phase.StagingBytes:N0} | {phase.RemoteObjectCount?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown"} | {phase.RemoteObjectBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown"} |")));
        await File.WriteAllTextAsync(summary, $"""
# RustFS tiered segment ingest-only qualification

| Measurement | Result |
| --- | --- |
| Qualification mode | `ingest-only` (seed writes and remote durability validation) |
| Endpoint | `{endpoint}` |
| Bucket | `{options.Bucket}` |
| Segment prefix | `{RemoteObjectPrefix(options)}` |
| Journal prefix | `{remoteJournal.FramePrefix}` |
| Subject project | `{subject.Project}` |
| Subject commit | `{subject.Commit}` (tree `{subject.TreeDigest}`, dirty `{subject.Dirty}`, worktree `{subject.WorkingTreeDigest ?? "unknown"}`) |
| Benchmark build | `{toolchain.BuildConfiguration}`; `{toolchain.DotnetVersion}`; `{toolchain.FrameworkDescription}` |
| Benchmark assembly | `{benchmarkAssembly}` (sha256 `{benchmarkAssemblySha256 ?? "unknown"}`) |
| Engine root | `{root}` |
| Local WAL bytes after engine close | {localWalBytes:N0} |
| Durability ordering | Every acknowledged batch was verified in the remote commit journal before its synced local WAL append |
| Seed records requested | {options.Records:N0} |
| Remote acknowledged physical records | {acknowledged.AcknowledgedRecords:N0} |
| Internal authority records | {(acknowledged.AcknowledgedRecords - options.Records):N0} |
| Remote acknowledged batches | {acknowledged.AcknowledgedBatches:N0} |
| Remote acknowledged encoded record bytes | {acknowledged.AcknowledgedRecordBytes:N0} |
| Remote acknowledgement latency | {acknowledged.TotalAckMilliseconds:F3} ms total; {acknowledged.AverageAckMilliseconds:F3} ms/batch |
| Remote acknowledgement throughput | {acknowledged.AcknowledgedRecords / Math.Max(acknowledged.TotalAckSeconds, 0.000001):F0} physical records/s |
| Validated remote frontier | {frontier:N0} |
| Frontier equals acknowledged physical record count | {frontier == expectedFrontier} |
| Remote frontier validation | {frontierTimer.Elapsed.TotalSeconds:F3} s |
| Seed write time | {writeTimer.Elapsed.TotalSeconds:F3} s |
| Seed writes/sec | {options.Records / Math.Max(writeTimer.Elapsed.TotalSeconds, 0.000001):F0} |
| Segment objects under segment prefix | {remote.ObjectCount:N0} ({remote.TotalBytes:N0} bytes) |
| Journal objects under journal prefix | {remoteJournalObjects.ObjectCount:N0} ({remoteJournalObjects.TotalBytes:N0} bytes) |
| Payload mode | `{options.PayloadModeName}` |
| Payload generator | `{PayloadFactory.GeneratorName(options.PayloadMode)}` |
| Payload seed | {options.Seed} |
| Payload bytes per record | {options.PayloadBytes:N0} |
| Minimum logical seed payload bytes | {options.Records * (long)options.PayloadBytes:N0} |
| Observed cgroup memory.max | {envelope.MemoryMax ?? "unavailable"} |
| Observed cgroup memory.peak | {envelope.MemoryPeak ?? "unavailable"} |
| Observed cgroup memory.swap.max | {envelope.SwapMax ?? "unavailable"} |
| Isolated cgroup required | {options.RequireIsolatedCgroup} |
| Useful isolated cgroup observed | {IsUsefulIsolatedCgroup(envelope)} |
| Peak local engine bytes sampled | {diskUsage.PeakLocalBytes:N0} |
| Local engine bytes at completion | {finalStorage.LocalStorageBytes:N0} |
| Peak SSD cache bytes sampled | {diskUsage.PeakCacheBytes:N0} |
| SSD cache bytes at completion | {cacheBytes:N0} |
| Peak staging bytes sampled | {diskUsage.PeakStagingBytes:N0} |
| Minimum free space observed | {(diskUsage.MinimumObservedFreeBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable")} bytes |
| Free-space floor violation | {(diskUsage.FreeSpaceViolation?.ToString() ?? "none")} |
| Periodic resource samples | {diskUsage.SampleCount:N0} at {options.ResourceSampleIntervalMilliseconds} ms; `resource-samples.jsonl` |
| State-map flush trace | {(stateMapFlushTrace is null ? "disabled" : Path.GetFileName(stateMapFlushTrace.Path))} |
| Total interval | {elapsed.TotalSeconds:F3} s |
| Peak RSS (process working set) | {Process.GetCurrentProcess().PeakWorkingSet64:N0} bytes |
| Idle SSD cache budget | {options.CacheBytes:N0} bytes |

## Phase measurements

| Phase | Completed records | Duration (s) | RSS at end (B) | Peak RSS (B) | Local SSD at sample (B) | SSD cache at sample (B) | Staging at sample (B) | RustFS objects | RustFS bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
{phaseRows}

This receipt is scoped to the seed ingestion path. It proves the requested seed writes reached the synced local WAL only after remote commit journal publication and that the complete remote journal chain validated through its final physical frontier after the engine closed.

The following phases were skipped: {string.Join(", ", receipt.SkippedPhases.Select(static phase => $"`{phase}`"))}.
Indexed queries and backup/restore require the separate E2eVolumeWorkload qualification.
""").ConfigureAwait(false);

        receipt.Status = "complete";
        receipt.Phase = "complete";
        receipt.FinishedAtUtc = DateTimeOffset.UtcNow;
        receipt.CompletedRecords = options.Records;
        receipt.LatestResources = ResourceSample.Capture("complete", options.Records, envelope, finalStorage, remoteJournalObjects);
        receipt.RemoteObjects = remote;
        receipt.RemoteJournalObjects = remoteJournalObjects;
        receipt.RemoteAck = acknowledged;
        receipt.InternalAuthorityRecords = checked(acknowledged.AcknowledgedRecords - options.Records);
        receipt.RemoteValidatedFrontier = frontier;
        receipt.RemoteFrontierValidationSeconds = frontierTimer.Elapsed.TotalSeconds;
        receipt.LocalWalBytes = localWalBytes;
        receipt.CacheBytesAfterVerification = cacheBytes;
        receipt.PeakLocalStorageBytes = diskUsage.PeakLocalBytes;
        receipt.PeakCacheBytes = diskUsage.PeakCacheBytes;
        receipt.PeakStagingBytes = diskUsage.PeakStagingBytes;
        receipt.PeakRssBytes = Process.GetCurrentProcess().PeakWorkingSet64;
        receipt.ResourceSampleCount = diskUsage.SampleCount;
        receipt.ResourceSamplerError = sampler.Error?.ToString();
        receipt.FinalCgroup = envelope;
        receipt.IsolatedCgroup = IsUsefulIsolatedCgroup(envelope);
        receipt.MinimumObservedFreeSpaceBytes = diskUsage.MinimumObservedFreeBytes;
        receipt.FreeSpaceLimitViolation = diskUsage.FreeSpaceViolation?.ToString();
        receipt.PhaseMetrics = phaseMetrics;
        if (stateMapFlushTrace is not null)
        {
            await stateMapFlushTrace.WriteAsync().ConfigureAwait(false);
            receipt.StateMapFlushTracePath = Path.GetFullPath(stateMapFlushTrace.Path);
        }
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
        Console.WriteLine($"Tiered RustFS ingest-only summary: {summary}");
        return 0;
    }

    private static List<string> IngestOnlySkippedPhases() =>
    [
        "seed-root-loss-recovery",
        "initial-compaction",
        "initial-cache-eviction",
        "initial-remote-reopen-scan",
        "point-reads",
        "updates",
        "deletes",
        "mutation-root-loss-recovery",
        "final-compaction",
        "final-cache-eviction",
        "final-remote-reopen-scan",
        "indexed-queries",
        "backup",
        "restore",
    ];

    private static JsonElement CreateBody(long number, PayloadFactory payload, bool updated) =>
        JsonSerializer.SerializeToElement(new { number, payload = payload.For(number), updated });

    private static void VerifyBody(Document document, long number, PayloadFactory payload, bool updated)
    {
        if (document.Version != (updated ? 2UL : 1UL)
            || document.Key.DocumentId != $"doc-{number:D10}"
            || document.Body.GetProperty("number").GetInt64() != number
            || document.Body.GetProperty("payload").GetString() != payload.For(number)
            || document.Body.GetProperty("updated").GetBoolean() != updated)
        {
            throw new InvalidDataException($"Document doc-{number:D10} does not match the expected version or body.");
        }
    }

    private static TieredStorageSegmentStore CreateTieredStore(
        S3NativeBackupObjectStore objectStore,
        string cache,
        Options options) =>
        new(new TieredStorageSegmentStoreOptions(
            objectStore,
            cache,
            options.Prefix,
            options.CacheBytes));

    private static StorageCommitRequest CreateRetryRequest(string phase, long offset, int count, int seed)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"tiered-rustfs:{phase}:{offset}:{count}:{seed}"));
        return new StorageCommitRequest(EntityId.New(), digest);
    }

    private static void DeleteLocalRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            return;

        var attributes = File.GetAttributes(fullRoot);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to remove the benchmark root because it is a reparse point: '{fullRoot}'.");

        var parent = Directory.GetParent(fullRoot)
            ?? throw new IOException($"The benchmark root has no parent: '{fullRoot}'.");
        if (string.Equals(parent.FullName, fullRoot, GetPathComparison()))
            throw new IOException($"Refusing to remove filesystem root '{fullRoot}'.");

        Directory.Delete(fullRoot, recursive: true);
    }

    private static async Task VerifyExactStateAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId collection,
        Options options,
        PayloadFactory payload,
        long updates,
        long deletes,
        DiskUsageTracker diskUsage,
        ProgressLog progress,
        string receiptPath,
        QualificationReceiptState receipt,
        string phase,
        long started)
    {
        var expectedLive = options.Records - deletes;
        if (expectedLive > 0)
        {
            var first = await engine.ReadAsync(new DocumentKey(tenant, collection, "doc-0000000000")).ConfigureAwait(false)
                ?? throw new InvalidDataException($"{phase} could not recover the first document.");
            VerifyBody(first, 0, payload, updated: updates > 0);

            var lastLive = await engine.ReadAsync(new DocumentKey(tenant, collection, $"doc-{expectedLive - 1:D10}")).ConfigureAwait(false)
                ?? throw new InvalidDataException($"{phase} could not recover the last live document.");
            VerifyBody(lastLive, expectedLive - 1, payload, updated: expectedLive - 1 < updates);
        }

        if (deletes > 0)
        {
            var firstDeleted = await engine.ReadAsync(new DocumentKey(tenant, collection, $"doc-{expectedLive:D10}")).ConfigureAwait(false);
            var lastDeleted = await engine.ReadAsync(new DocumentKey(tenant, collection, $"doc-{options.Records - 1:D10}")).ConfigureAwait(false);
            if (firstDeleted is not null || lastDeleted is not null)
                throw new InvalidDataException($"{phase} exposed a deleted document instead of its tombstone.");
        }

        var scanCount = await VerifyScanAsync(
            engine,
            tenant,
            collection,
            expectedLive,
            payload,
            updates,
            deletes,
            options.CheckpointRecords,
            diskUsage,
            progress,
            receiptPath,
            receipt,
            phase,
            started).ConfigureAwait(false);
        if (scanCount != expectedLive)
            throw new InvalidDataException($"{phase} returned {scanCount:N0} records, expected {expectedLive:N0}.");

        await VerifyPointReadsAsync(
            engine,
            tenant,
            collection,
            options,
            payload,
            updates,
            deletes,
            diskUsage,
            progress,
            receiptPath,
            receipt,
            $"{phase}-point-reads",
            started).ConfigureAwait(false);
}

    private static async Task VerifyCommittedRetryStatusAsync(
        DurableStorageEngine engine,
        StorageCommitRequest? request,
        string phase)
    {
        if (request is null)
            return;

        var status = await engine.GetCommitStatusAsync(request).ConfigureAwait(false);
        if (status.Status != StorageCommitStatus.Committed
            || status.Result?.Outcome != StorageBatchOutcome.Committed)
        {
            throw new InvalidDataException(
                $"{phase} returned retry status {status.Status} instead of Committed.");
        }
    }

    private static async Task<long> VerifyScanAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId collection,
        long expectedLive,
        PayloadFactory payload,
        long updates,
        long deletes,
        long checkpointInterval,
        DiskUsageTracker diskUsage,
        ProgressLog progress,
        string receiptPath,
        QualificationReceiptState receipt,
        string phase,
        long started,
        long checkpointStart = 0)
    {
        diskUsage.SetContext(phase, 0);
        var nextCheckpoint = checkpointStart > 0 ? checkpointStart : checkpointInterval;
        var scanCount = 0L;
        await foreach (var document in engine.ScanAsync(tenant, collection).ConfigureAwait(false))
        {
            if (!TryParseDocumentNumber(document.Key.DocumentId, out var number)
                || number < 0
                || number >= expectedLive + deletes)
            {
                throw new InvalidDataException($"Scan returned an unexpected document id '{document.Key.DocumentId}'.");
            }

            var deleted = number >= expectedLive;
            if (deleted)
            {
                throw new InvalidDataException($"Deleted document doc-{number:D10} survived the {phase} scan.");
            }

            if (number != scanCount)
                throw new InvalidDataException($"{phase} returned doc-{number:D10} at position {scanCount:N0}.");

            VerifyBody(document, number, payload, number < updates);
            diskUsage.ThrowIfExceeded();
            scanCount++;
            if (scanCount >= nextCheckpoint || scanCount == expectedLive)
            {
                await WriteCheckpointAsync(diskUsage, progress, receiptPath, receipt, phase, scanCount, expectedLive, started).ConfigureAwait(false);
                nextCheckpoint = scanCount > long.MaxValue - checkpointInterval ? long.MaxValue : scanCount + checkpointInterval;
            }
        }

        if (scanCount != expectedLive)
        {
            throw new InvalidDataException($"{phase} returned {scanCount:N0} records, expected {expectedLive:N0}.");
        }

        return scanCount;
    }

    private static async Task<TimeSpan> VerifyPointReadsAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId collection,
        Options options,
        PayloadFactory payload,
        long updates,
        long deletes,
        DiskUsageTracker diskUsage,
        ProgressLog progress,
        string receiptPath,
        QualificationReceiptState receipt,
        string phase,
        long started)
    {
        diskUsage.SetContext(phase, 0);
        var startedReads = Stopwatch.GetTimestamp();
        for (var sample = 0; sample < options.PointReads; sample++)
        {
            var number = sample switch
            {
                0 => 0,
                1 when options.Records > 1 => options.Records - 1,
                _ => SampleIndex(options.Seed, sample, options.Records),
            };
            var document = await engine.ReadAsync(
                new DocumentKey(tenant, collection, $"doc-{number:D10}")).ConfigureAwait(false);
            if (number >= options.Records - deletes)
            {
                if (document is not null)
                    throw new InvalidDataException($"Point read found deleted document doc-{number:D10}.");
            }
            else
            {
                if (document is null)
                    throw new InvalidDataException($"Point read could not recover document doc-{number:D10}.");
                VerifyBody(document, number, payload, number < updates);
            }
            diskUsage.ThrowIfExceeded();
        }

        var elapsed = Stopwatch.GetElapsedTime(startedReads);
        await WriteCheckpointAsync(diskUsage, progress, receiptPath, receipt, phase, options.PointReads, options.PointReads, started).ConfigureAwait(false);
        return elapsed;
    }

    private static async Task ApplyRangeAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId collection,
        long count,
        int batchSize,
        PayloadFactory payload,
        bool updated,
        long checkpointInterval,
        DiskUsageTracker diskUsage,
        ProgressLog progress,
        string receiptPath,
        QualificationReceiptState receipt,
        string phase,
        long started,
        StorageCommitRequest? retryRequest = null)
    {
        diskUsage.SetContext(phase, 0);
        var nextCheckpoint = checkpointInterval;
        for (long offset = 0; offset < count; offset += batchSize)
        {
            var batchCount = (int)Math.Min((long)batchSize, count - offset);
            var mutations = new StorageMutation[batchCount];
            for (var index = 0; index < batchCount; index++)
            {
                var number = offset + index;
                mutations[index] = StorageMutation.Upsert(
                    new DocumentKey(tenant, collection, $"doc-{number:D10}"),
                    CreateBody(number, payload, updated),
                    DocumentPrecondition.Version(1));
            }

            var completed = offset + batchCount;
            var batchRequest = retryRequest is not null && completed == count
                ? retryRequest
                : null;
            var result = batchRequest is null
                ? await engine.ApplyBatchAsync(mutations, diskUsage.FailureCancellationToken).ConfigureAwait(false)
                : await engine.ApplyBatchAsync(mutations, batchRequest, diskUsage.FailureCancellationToken).ConfigureAwait(false);
            if (result.Outcome != StorageBatchOutcome.Committed)
                throw new InvalidDataException($"{phase} batch at {offset} did not commit: {result.Outcome}.");

            diskUsage.ThrowIfExceeded();

            if (completed >= nextCheckpoint || completed == count)
            {
                await WriteCheckpointAsync(diskUsage, progress, receiptPath, receipt, phase, completed, count, started).ConfigureAwait(false);
                nextCheckpoint = completed + checkpointInterval;
            }
        }
    }

    private static async Task ApplyDeletesAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId collection,
        Options options,
        long deletes,
        long checkpointInterval,
        DiskUsageTracker diskUsage,
        ProgressLog progress,
        string receiptPath,
        QualificationReceiptState receipt,
        long started,
        StorageCommitRequest? retryRequest = null)
    {
        diskUsage.SetContext("deletes", 0);
        var nextCheckpoint = checkpointInterval;
        var firstDeleted = options.Records - deletes;
        for (long offset = 0; offset < deletes; offset += options.Batch)
        {
            var batchCount = (int)Math.Min((long)options.Batch, deletes - offset);
            var mutations = new StorageMutation[batchCount];
            for (var index = 0; index < batchCount; index++)
            {
                var number = firstDeleted + offset + index;
                mutations[index] = StorageMutation.Delete(
                    new DocumentKey(tenant, collection, $"doc-{number:D10}"),
                    DocumentPrecondition.Version(1));
            }

            var completed = offset + batchCount;
            var batchRequest = retryRequest is not null && completed == deletes
                ? retryRequest
                : null;
            var result = batchRequest is null
                ? await engine.ApplyBatchAsync(mutations, diskUsage.FailureCancellationToken).ConfigureAwait(false)
                : await engine.ApplyBatchAsync(mutations, batchRequest, diskUsage.FailureCancellationToken).ConfigureAwait(false);
            if (result.Outcome != StorageBatchOutcome.Committed)
                throw new InvalidDataException($"deletes batch at {offset} did not commit: {result.Outcome}.");

            diskUsage.ThrowIfExceeded();

            if (completed >= nextCheckpoint || completed == deletes)
            {
                await WriteCheckpointAsync(diskUsage, progress, receiptPath, receipt, "deletes", completed, deletes, started).ConfigureAwait(false);
                nextCheckpoint = completed + checkpointInterval;
            }
        }
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

    private static bool TryParseDocumentNumber(string id, out long number)
    {
        number = 0;
        return id.StartsWith("doc-", StringComparison.Ordinal)
            && long.TryParse(id.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static async Task WriteCheckpointAsync(
        DiskUsageTracker diskUsage,
        ProgressLog progress,
        string receiptPath,
        QualificationReceiptState receipt,
        string phase,
        long completed,
        long total,
        long started)
    {
        receipt.Phase = phase;
        receipt.CompletedRecords = completed;
        var storage = diskUsage.Sample(phase, completed);
        diskUsage.ThrowIfExceeded();
        receipt.PeakRssBytes = Process.GetCurrentProcess().PeakWorkingSet64;
        receipt.LatestResources = ResourceSample.Capture(phase, completed, BenchmarkCgroupSnapshot.Capture(), storage, receipt.RemoteObjects);
        await progress.WriteAsync(phase, completed, total, Stopwatch.GetElapsedTime(started), storage, receipt.RemoteObjects).ConfigureAwait(false);
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
    }

    private static PhaseMeasurement CapturePhase(
        string phase,
        long completed,
        TimeSpan elapsed,
        DiskUsageTracker diskUsage,
        SegmentObjectTotals? remote)
    {
        diskUsage.SetContext(phase, completed);
        var storage = diskUsage.Sample(phase, completed);
        diskUsage.ThrowIfExceeded();
        var process = Process.GetCurrentProcess();
        return new PhaseMeasurement(
            phase,
            completed,
            elapsed.TotalSeconds,
            process.WorkingSet64,
            process.PeakWorkingSet64,
            storage.LocalStorageBytes,
            storage.CacheBytes,
            storage.StagingBytes,
            remote?.ObjectCount,
            remote?.TotalBytes);
    }

    private static async Task<SegmentObjectTotals> ListRemoteObjectsAsync(AmazonS3Client client, Options options)
        => await ListRemoteObjectsAsync(client, options.Bucket, RemoteObjectPrefix(options)).ConfigureAwait(false);

    private static async Task<SegmentObjectTotals> ListRemoteObjectsAsync(AmazonS3Client client, string bucket, string prefix)
    {
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        long count = 0;
        long bytes = 0;
        do
        {
            var response = await client.ListObjectsV2Async(request).ConfigureAwait(false);
            if (response.S3Objects is null && response.IsTruncated == true)
                throw new InvalidDataException("RustFS returned a truncated object listing without an object collection.");

            foreach (var item in response.S3Objects ?? [])
            {
                if (string.IsNullOrEmpty(item.Key)
                    || !item.Key.StartsWith(prefix, StringComparison.Ordinal)
                    || item.Key.Length == prefix.Length)
                {
                    throw new InvalidDataException(
                        $"RustFS returned object '{item.Key ?? "<null>"}' outside the exact segment prefix '{prefix}'.");
                }

                if (item.Size is not long size || size < 0)
                    throw new InvalidDataException($"RustFS returned an invalid size for object '{item.Key}'.");

                count++;
                bytes = checked(bytes + size);
            }

            if (response.IsTruncated != true)
            {
                if (!string.IsNullOrEmpty(response.NextContinuationToken))
                    throw new InvalidDataException("RustFS returned a continuation token for a non-truncated object listing.");
                request.ContinuationToken = null;
                break;
            }

            if (string.IsNullOrEmpty(response.NextContinuationToken)
                || string.Equals(response.NextContinuationToken, request.ContinuationToken, StringComparison.Ordinal))
            {
                throw new InvalidDataException("RustFS returned a truncated object listing without an advancing continuation token.");
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (request.ContinuationToken is not null);

        return new SegmentObjectTotals(count, bytes);
    }

    private static string RemoteObjectPrefix(Options options)
    {
        var prefix = options.Prefix.Trim('/');
        if (prefix.Length == 0
            || prefix.Contains("..", StringComparison.Ordinal)
            || prefix.Contains('\\')
            || prefix.Contains('\0'))
        {
            throw new ArgumentException("The segment prefix must be nonempty and contain no traversal characters.", nameof(options));
        }

        return prefix + "/";
    }

    private static string JournalObjectPrefix(Options options)
    {
        var prefix = options.Prefix.Trim('/');
        if (prefix.Length == 0
            || prefix.Contains("..", StringComparison.Ordinal)
            || prefix.Contains('\\')
            || prefix.Contains('\0'))
        {
            throw new ArgumentException("The journal prefix must be nonempty and contain no traversal characters.", nameof(options));
        }

        // Keep the journal namespace outside the segment prefix. The engine's
        // missing-root admission check may inspect the segment prefix before
        // the journal can rebuild the local authority.
        return prefix + "-commits";
    }

    private static CacheEvictionStats DeleteCacheFiles(string cache)
    {
        if (!Directory.Exists(cache))
            return new CacheEvictionStats(0, 0);

        long files = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(cache, "*.seg", SearchOption.AllDirectories))
        {
            var length = new FileInfo(file).Length;
            File.Delete(file);
            files++;
            bytes += length;
        }

        return new CacheEvictionStats(files, bytes);
    }

    private static long DirectoryBytes(string root)
        => DirectoryBytes(root, "*");

    private static long DirectoryBytes(string root, string pattern)
    {
        if (!Directory.Exists(root))
            return 0;

        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }

        return bytes;
    }

    private static async Task<T> MeasureOperationAsync<T>(
        Func<Task<T>> operation,
        DiskUsageTracker diskUsage,
        string phase)
    {
        diskUsage.SetContext(phase, 0);
        diskUsage.Sample(phase, 0);
        diskUsage.ThrowIfExceeded();
        var task = operation();
        while (!task.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            diskUsage.Sample(phase, 0);
            if (diskUsage.LimitViolation is not null)
            {
                await task.ConfigureAwait(false);
                diskUsage.ThrowIfExceeded();
            }
        }

        var result = await task.ConfigureAwait(false);
        diskUsage.Sample(phase, 0);
        diskUsage.ThrowIfExceeded();
        return result;
    }

    private static Task SaveReceiptAsync(string path, QualificationReceiptState receipt) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(receipt, PrettyJsonOptions));

    private sealed class ProgressLog : IAsyncDisposable
    {
        private readonly StreamWriter writer;

        public ProgressLog(string path)
        {
            writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous));
        }

        public async Task WriteAsync(
            string phase,
            long completed,
            long total,
            TimeSpan elapsed,
            DiskUsageSample? storage,
            SegmentObjectTotals? remote)
        {
            var process = Process.GetCurrentProcess();
            var checkpoint = new
            {
                utc = DateTimeOffset.UtcNow,
                phase,
                completed,
                total,
                elapsedSeconds = elapsed.TotalSeconds,
                workingSetBytes = process.WorkingSet64,
                peakRssBytes = process.PeakWorkingSet64,
                managedBytes = GC.GetTotalMemory(forceFullCollection: false),
                localStorageBytes = storage?.LocalStorageBytes,
                ssdCacheBytes = storage?.CacheBytes,
                stagingBytes = storage?.StagingBytes,
                availableFreeSpaceBytes = storage?.AvailableFreeSpaceBytes,
                rustFsObjectCount = remote?.ObjectCount,
                rustFsBytes = remote?.TotalBytes,
                cgroup = BenchmarkCgroupSnapshot.Capture(),
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(checkpoint)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            writer.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Bounded aggregate for the opt in state-map flush observer. One row is
    /// retained per map, so a large ingest cannot create an unbounded trace
    /// file or managed event list.
    /// </summary>
    private sealed class StorageStateMapFlushTrace(string path)
    {
        private const int MaximumMapCount = 64;
        private readonly object gate = new();
        private readonly Dictionary<string, Aggregate> aggregates = new(StringComparer.Ordinal);
        private long measurementCount;
        private long droppedMeasurementCount;

        internal string Path { get; } = path;

        internal void Observe(StorageStateMapFlushMeasurement measurement)
        {
            lock (gate)
            {
                measurementCount = checked(measurementCount + 1);
                if (!aggregates.TryGetValue(measurement.MapName, out var aggregate))
                {
                    if (aggregates.Count >= MaximumMapCount)
                    {
                        droppedMeasurementCount = checked(droppedMeasurementCount + 1);
                        return;
                    }

                    aggregate = new Aggregate(measurement.MapName);
                    aggregates.Add(measurement.MapName, aggregate);
                }

                aggregate.Observe(measurement);
            }
        }

        internal Task WriteAsync()
        {
            TraceSnapshot snapshot;
            lock (gate)
            {
                snapshot = new TraceSnapshot(
                    Version: 1,
                    GeneratedAtUtc: DateTimeOffset.UtcNow,
                    MeasurementCount: measurementCount,
                    DroppedMeasurementCount: droppedMeasurementCount,
                    Maps: aggregates.Values
                        .OrderBy(static aggregate => aggregate.MapName, StringComparer.Ordinal)
                        .Select(static aggregate => aggregate.Snapshot())
                        .ToArray());
            }

            return File.WriteAllTextAsync(Path, JsonSerializer.Serialize(snapshot, PrettyJsonOptions));
        }

        private sealed class Aggregate(string mapName)
        {
            private long flushCount;
            private long successfulFlushCount;
            private long failedFlushCount;
            private long bufferedBytes;
            private long evictTicks;
            private long mergeTicks;
            private long releaseTicks;
            private long totalTicks;
            private int maximumReadOnlySegmentsBefore;
            private int maximumReadOnlySegmentsAfter;
            private int maximumBottomSegmentCountBefore;
            private int maximumBottomSegmentCountAfter;

            internal string MapName { get; } = mapName;

            internal void Observe(StorageStateMapFlushMeasurement measurement)
            {
                flushCount = checked(flushCount + 1);
                if (measurement.Succeeded)
                    successfulFlushCount = checked(successfulFlushCount + 1);
                else
                    failedFlushCount = checked(failedFlushCount + 1);
                bufferedBytes = checked(bufferedBytes + measurement.BufferedBytes);
                evictTicks = checked(evictTicks + measurement.EvictToDiskDuration.Ticks);
                mergeTicks = checked(mergeTicks + measurement.MergeWaitDuration.Ticks);
                releaseTicks = checked(releaseTicks + measurement.ReadBufferReleaseDuration.Ticks);
                totalTicks = checked(totalTicks + measurement.TotalDuration.Ticks);
                maximumReadOnlySegmentsBefore = Math.Max(
                    maximumReadOnlySegmentsBefore,
                    measurement.ReadOnlySegmentsBefore);
                maximumReadOnlySegmentsAfter = Math.Max(
                    maximumReadOnlySegmentsAfter,
                    measurement.ReadOnlySegmentsAfter);
                maximumBottomSegmentCountBefore = Math.Max(
                    maximumBottomSegmentCountBefore,
                    measurement.BottomSegmentCountBefore);
                maximumBottomSegmentCountAfter = Math.Max(
                    maximumBottomSegmentCountAfter,
                    measurement.BottomSegmentCountAfter);
            }

            internal MapSnapshot Snapshot() => new(
                MapName,
                flushCount,
                successfulFlushCount,
                failedFlushCount,
                bufferedBytes,
                ToMilliseconds(evictTicks),
                ToMilliseconds(mergeTicks),
                ToMilliseconds(releaseTicks),
                ToMilliseconds(totalTicks),
                maximumReadOnlySegmentsBefore,
                maximumReadOnlySegmentsAfter,
                maximumBottomSegmentCountBefore,
                maximumBottomSegmentCountAfter);

            private static double ToMilliseconds(long ticks) =>
                TimeSpan.FromTicks(ticks).TotalMilliseconds;
        }

        private sealed record TraceSnapshot(
            int Version,
            DateTimeOffset GeneratedAtUtc,
            long MeasurementCount,
            long DroppedMeasurementCount,
            MapSnapshot[] Maps);

        private sealed record MapSnapshot(
            string MapName,
            long FlushCount,
            long SuccessfulFlushCount,
            long FailedFlushCount,
            long BufferedBytes,
            double EvictToDiskMilliseconds,
            double MergeWaitMilliseconds,
            double ReadBufferReleaseMilliseconds,
            double TotalMilliseconds,
            int MaximumReadOnlySegmentsBefore,
            int MaximumReadOnlySegmentsAfter,
            int MaximumBottomSegmentCountBefore,
            int MaximumBottomSegmentCountAfter);
    }

    private sealed class QualificationReceiptState
    {
        public string Status { get; set; } = "running";
        public string Phase { get; set; } = "initializing";
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public long TargetRecords { get; set; }
        public long CompletedRecords { get; set; }
        public string? SubjectProject { get; set; }
        public SubjectProvenance? Subject { get; set; }
        public ToolchainInfo? Toolchain { get; set; }
        public string? BenchmarkAssembly { get; set; }
        public string? BenchmarkAssemblySha256 { get; set; }
        public string? ResourceEnvelopePath { get; set; }
        public string? ResourceSamplesPath { get; set; }
        public string? ProgressPath { get; set; }
        public string? StateMapFlushTracePath { get; set; }
        public string? PayloadMode { get; set; }
        public string? PayloadGenerator { get; set; }
        public int PayloadSeed { get; set; }
        public bool IngestOnly { get; set; }
        public ulong? RemoteValidatedFrontier { get; set; }
        public double RemoteFrontierValidationSeconds { get; set; }
        public long LocalWalBytes { get; set; }
        public List<string> SkippedPhases { get; set; } = [];
        public bool RequireIsolatedCgroup { get; set; }
        public bool? IsolatedCgroup { get; set; }
        public long? MaxLocalStorageBytes { get; set; }
        public long MinimumFreeSpaceBytes { get; set; }
        public string? OutputMount { get; set; }
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
        public string? Tenant { get; set; }
        public string? Collection { get; set; }
        public long? MinimumObservedFreeSpaceBytes { get; set; }
        public string? FreeSpaceLimitViolation { get; set; }
        public long? LocalStorageLimitExceededBytes { get; set; }
        public string? LocalStorageLimitExceededPhase { get; set; }
        public long? LocalStorageLimitExceededRecords { get; set; }
        public BenchmarkCgroupSnapshot? InitialCgroup { get; set; }
        public BenchmarkCgroupSnapshot? FinalCgroup { get; set; }
        public long ResourceSampleCount { get; set; }
        public string? ResourceSamplerError { get; set; }
        public ResourceSample? LatestResources { get; set; }
        public List<PhaseMeasurement> PhaseMetrics { get; set; } = [];
        public SegmentObjectTotals? RemoteObjects { get; set; }
        public SegmentObjectTotals? RemoteJournalObjects { get; set; }
        public RemoteAckMetrics? RemoteAck { get; set; }
        public long InternalAuthorityRecords { get; set; }
        public double SeedRootLossRecoverySeconds { get; set; }
        public double MutationRootLossRecoverySeconds { get; set; }
        public CacheEvictionStats? CacheEviction { get; set; }
        public CacheEvictionStats? InitialCacheEviction { get; set; }
        public CacheEvictionStats? FinalCacheEviction { get; set; }
        public long CacheBytesAfterInitialVerification { get; set; }
        public long CacheBytesAfterVerification { get; set; }
        public long PeakLocalStorageBytes { get; set; }
        public long PeakCacheBytes { get; set; }
        public long PeakStagingBytes { get; set; }
        public long PeakRssBytes { get; set; }
        public string? Error { get; set; }
    }

    private sealed record ResourceSample(
        string Phase,
        long CompletedRecords,
        long WorkingSetBytes,
        long PeakWorkingSetBytes,
        long ManagedBytes,
        long LocalStorageBytes,
        long SsdCacheBytes,
        long StagingBytes,
        long AvailableFreeSpaceBytes,
        long MinimumFreeSpaceBytes,
        long? MinimumObservedFreeSpaceBytes,
        string? FreeSpaceLimitViolation,
        long? RustFsObjectCount,
        long? RustFsBytes,
        BenchmarkCgroupSnapshot Cgroup)
    {
        public static ResourceSample Capture(
            string phase,
            long completed,
            BenchmarkCgroupSnapshot cgroup,
            DiskUsageSample storage,
            SegmentObjectTotals? remote)
        {
            var process = Process.GetCurrentProcess();
            return new(
                phase,
                completed,
                process.WorkingSet64,
                process.PeakWorkingSet64,
                GC.GetTotalMemory(forceFullCollection: false),
                storage.LocalStorageBytes,
                storage.CacheBytes,
                storage.StagingBytes,
                storage.AvailableFreeSpaceBytes,
                storage.MinimumFreeSpaceBytes,
                storage.MinimumObservedFreeSpaceBytes,
                storage.FreeSpaceLimitViolation,
                remote?.ObjectCount,
                remote?.TotalBytes,
                cgroup);
        }
    }

    private sealed record SegmentObjectTotals(long ObjectCount, long TotalBytes);

    private sealed record RemoteAckMetrics(
        long AcknowledgedBatches,
        long AcknowledgedRecords,
        long AcknowledgedRecordBytes,
        double TotalAckMilliseconds,
        double AverageAckMilliseconds,
        double TotalAckSeconds);

    private sealed class MeasuredRemoteCommitJournal(RemoteCommitJournal inner) : IRemoteCommitJournal, IRemoteCommitJournalFrameLimits
    {
        private readonly object gate = new();
        private long acknowledgedBatches;
        private long acknowledgedRecords;
        private long acknowledgedRecordBytes;
        private double totalAckMilliseconds;

        public string FramePrefix => inner.FramePrefix;

        // StorageEngine uses this internal capability to admit a local WAL
        // frame before publishing it remotely. Keep the measurement decorator
        // transparent so strict ingest retains RemoteCommitJournal's frame
        // bound instead of falling back to the durable log default.
        int IRemoteCommitJournalFrameLimits.MaximumLocalPayloadBytes =>
            ((IRemoteCommitJournalFrameLimits)inner).MaximumLocalPayloadBytes;

        public async ValueTask PublishAsync(
            IReadOnlyList<StorageLogRecord> records,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(records);
            long encodedBytes = 0;
            foreach (var record in records)
                encodedBytes = checked(encodedBytes + StorageRecordCodec.EncodedSize(record));

            var timer = Stopwatch.StartNew();
            await inner.PublishAsync(records, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            lock (gate)
            {
                acknowledgedBatches++;
                acknowledgedRecords = checked(acknowledgedRecords + records.Count);
                acknowledgedRecordBytes = checked(acknowledgedRecordBytes + encodedBytes);
                totalAckMilliseconds += timer.Elapsed.TotalMilliseconds;
            }
        }

        public IAsyncEnumerable<StorageLogRecord> ReplayAsync(
            ulong afterSequence = 0,
            CancellationToken cancellationToken = default) =>
            inner.ReplayAsync(afterSequence, cancellationToken);

        public ValueTask<bool> HasCommitsAsync(CancellationToken cancellationToken = default) =>
            inner.HasCommitsAsync(cancellationToken);

        public ValueTask ValidateThroughAsync(
            ulong localSequence,
            CancellationToken cancellationToken = default) =>
            inner.ValidateThroughAsync(localSequence, cancellationToken);

        public ValueTask<ulong> GetValidatedFrontierAsync(CancellationToken cancellationToken = default) =>
            inner.GetValidatedFrontierAsync(cancellationToken);

        public RemoteAckMetrics Snapshot()
        {
            lock (gate)
            {
                var average = acknowledgedBatches == 0 ? 0 : totalAckMilliseconds / acknowledgedBatches;
                var seconds = totalAckMilliseconds / 1000d;
                return new RemoteAckMetrics(
                    acknowledgedBatches,
                    acknowledgedRecords,
                    acknowledgedRecordBytes,
                    totalAckMilliseconds,
                    average,
                    seconds);
            }
        }

        public long AcknowledgedBatches => Snapshot().AcknowledgedBatches;
        public long AcknowledgedRecords => Snapshot().AcknowledgedRecords;
        public long AcknowledgedRecordBytes => Snapshot().AcknowledgedRecordBytes;
        public double TotalAckMilliseconds => Snapshot().TotalAckMilliseconds;
        public double AverageAckMilliseconds => Snapshot().AverageAckMilliseconds;
        public double TotalAckSeconds => Snapshot().TotalAckSeconds;
    }

    private sealed record CacheEvictionStats(long DeletedFiles, long DeletedBytes);

    private sealed record PhaseMeasurement(
        string Name,
        long CompletedRecords,
        double DurationSeconds,
        long WorkingSetBytes,
        long PeakRssBytes,
        long LocalStorageBytes,
        long SsdCacheBytes,
        long StagingBytes,
        long? RemoteObjectCount,
        long? RemoteObjectBytes);

    private sealed record DiskUsageSample(
        long LocalStorageBytes,
        long CacheBytes,
        long StagingBytes,
        long AvailableFreeSpaceBytes,
        long MinimumFreeSpaceBytes,
        long? MinimumObservedFreeSpaceBytes,
        string? FreeSpaceLimitViolation)
    {
        public static DiskUsageSample Empty { get; } = new(0, 0, 0, -1, 0, null, null);
    }

    private sealed class LocalStorageLimitExceededException(
        long limitBytes,
        long observedBytes,
        string phase,
        long completedRecords)
        : IOException($"Local engine storage exceeded the configured cap of {limitBytes:N0} bytes: observed {observedBytes:N0} bytes during {phase} at {completedRecords:N0} records.")
    {
        public long LimitBytes { get; } = limitBytes;
        public long ObservedBytes { get; } = observedBytes;
        public string Phase { get; } = phase;
        public long CompletedRecords { get; } = completedRecords;
    }

    private sealed class FreeSpaceLimitExceededException(
        long minimumBytes,
        long observedBytes,
        string phase,
        long completedRecords)
        : IOException($"Available free space fell below the configured floor of {minimumBytes:N0} bytes: observed {observedBytes:N0} bytes during {phase} at {completedRecords:N0} records.")
    {
        public long MinimumBytes { get; } = minimumBytes;
        public long ObservedBytes { get; } = observedBytes;
        public string Phase { get; } = phase;
        public long CompletedRecords { get; } = completedRecords;
    }

    private sealed class FreeSpaceProbeException(string mount, string phase, long completedRecords)
        : IOException($"Could not read available free space for output mount '{mount}' during {phase} at {completedRecords:N0} records.")
    {
        public string Mount { get; } = mount;
        public string Phase { get; } = phase;
        public long CompletedRecords { get; } = completedRecords;
    }

    private sealed record OutputMount(string Name, long TotalSize);

    private sealed class DiskUsageTracker : IDisposable
    {
        private readonly string root;
        private readonly string cache;
        private readonly long? maxLocalStorageBytes;
        private readonly long minimumFreeSpaceBytes;
        private readonly OutputMount outputMount;
        private readonly object gate = new();
        private DiskUsageSample current = DiskUsageSample.Empty;
        private string phase = "initializing";
        private long completed;
        private long minimumObservedFreeSpaceBytes = long.MaxValue;
        private Exception? limitViolation;
        private Exception? freeSpaceViolation;
        private Exception? samplerError;
        private readonly CancellationTokenSource failureCancellation = new();

        public DiskUsageTracker(
            string root,
            string cache,
            long? maxLocalStorageBytes,
            long minimumFreeSpaceBytes,
            OutputMount outputMount)
        {
            this.root = root;
            this.cache = cache;
            this.maxLocalStorageBytes = maxLocalStorageBytes;
            this.minimumFreeSpaceBytes = minimumFreeSpaceBytes;
            this.outputMount = outputMount;
        }

        public long PeakLocalBytes { get; private set; }
        public long PeakCacheBytes { get; private set; }
        public long PeakStagingBytes { get; private set; }
        public long SampleCount { get; private set; }
        public Exception? LimitViolation => Volatile.Read(ref limitViolation);
        public Exception? FreeSpaceViolation => Volatile.Read(ref freeSpaceViolation);
        public Exception? SamplerError => Volatile.Read(ref samplerError);
        public CancellationToken FailureCancellationToken => failureCancellation.Token;
        public bool FailureCancellationRequested => failureCancellation.IsCancellationRequested;
        public string OutputMount => outputMount.Name;
        public long MinimumFreeSpaceBytes => minimumFreeSpaceBytes;
        public long? MinimumObservedFreeBytes
        {
            get
            {
                lock (gate)
                    return minimumObservedFreeSpaceBytes == long.MaxValue ? null : minimumObservedFreeSpaceBytes;
            }
        }
        public DiskUsageSample Current
        {
            get
            {
                lock (gate)
                    return current;
            }
        }

        public void SetContext(string phase, long completed)
        {
            lock (gate)
            {
                this.phase = phase;
                this.completed = completed;
            }
        }

        public DiskUsageSample Sample(string phase, long completed)
        {
            SetContext(phase, completed);
            var availableFreeSpace = ReadAvailableFreeBytes(outputMount.Name);
            if (minimumFreeSpaceBytes > 0
                && (availableFreeSpace < 0 || availableFreeSpace < minimumFreeSpaceBytes))
            {
                Exception violation = availableFreeSpace < 0
                    ? new FreeSpaceProbeException(outputMount.Name, phase, completed)
                    : new FreeSpaceLimitExceededException(minimumFreeSpaceBytes, availableFreeSpace, phase, completed);
                DiskUsageSample failedSample;
                lock (gate)
                {
                    if (availableFreeSpace >= 0)
                        minimumObservedFreeSpaceBytes = Math.Min(minimumObservedFreeSpaceBytes, availableFreeSpace);
                    Interlocked.CompareExchange(ref freeSpaceViolation, violation, null);
                    RegisterLimitViolation(violation);
                    failedSample = new DiskUsageSample(
                        current.LocalStorageBytes,
                        current.CacheBytes,
                        current.StagingBytes,
                        availableFreeSpace,
                        minimumFreeSpaceBytes,
                        minimumObservedFreeSpaceBytes == long.MaxValue ? null : minimumObservedFreeSpaceBytes,
                        freeSpaceViolation?.ToString());
                    current = failedSample;
                    SampleCount++;
                }

                return failedSample;
            }

            // Large runs can leave tens of thousands of local state-map parts.
            // Walk the tree once per sample instead of repeating the same
            // directory traversal for total, cache, and staging bytes.
            var (local, cached, staging) = MeasureStorageBytes(FailureCancellationToken);
            lock (gate)
            {
                if (availableFreeSpace >= 0)
                    minimumObservedFreeSpaceBytes = Math.Min(minimumObservedFreeSpaceBytes, availableFreeSpace);

                current = new DiskUsageSample(
                    local,
                    cached,
                    staging,
                    availableFreeSpace,
                    minimumFreeSpaceBytes,
                    minimumObservedFreeSpaceBytes == long.MaxValue ? null : minimumObservedFreeSpaceBytes,
                    freeSpaceViolation?.ToString());
                PeakLocalBytes = Math.Max(PeakLocalBytes, local);
                PeakCacheBytes = Math.Max(PeakCacheBytes, cached);
                PeakStagingBytes = Math.Max(PeakStagingBytes, staging);
                SampleCount++;
                if (maxLocalStorageBytes is long limit && local > limit)
                {
                    var violation = new LocalStorageLimitExceededException(limit, local, phase, completed);
                    RegisterLimitViolation(violation);
                }

                return current;
            }
        }

        private (long Local, long Cache, long Staging) MeasureStorageBytes(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(root))
                return (0, 0, 0);

            var cachePrefix = Path.TrimEndingDirectorySeparator(cache) + Path.DirectorySeparatorChar;
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            long local = 0;
            long cached = 0;
            long staging = 0;
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long length;
                    try
                    {
                        length = new FileInfo(path).Length;
                    }
                    catch (FileNotFoundException)
                    {
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        continue;
                    }

                    local = checked(local + length);
                    if (path.StartsWith(cachePrefix, pathComparison))
                        cached = checked(cached + length);
                    if (path.EndsWith(".tmp", pathComparison))
                        staging = checked(staging + length);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // A concurrent compaction may retire one of the directories.
                // The next sample will observe its replacement or absence.
            }

            return (local, cached, staging);
        }

        public void ThrowIfExceeded()
        {
            if (LimitViolation is { } exception)
                throw exception;
            if (SamplerError is { } samplerFailure)
                throw new IOException("The resource sampler failed during qualification.", samplerFailure);
        }

        public void RecordSamplerError(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            Interlocked.CompareExchange(ref samplerError, exception, null);
            RegisterLimitViolation(new IOException("The resource sampler failed during qualification.", exception));
        }

        private void RegisterLimitViolation(Exception exception)
        {
            if (Interlocked.CompareExchange(ref limitViolation, exception, null) is null)
                failureCancellation.Cancel();
        }

        public void Dispose() => failureCancellation.Dispose();

        public PeriodicDiskUsageSampler StartPeriodicSampling(string path, TimeSpan interval)
            => new(this, path, interval);

        private static long ReadAvailableFreeBytes(string mount)
        {
            try { return new DriveInfo(mount).AvailableFreeSpace; }
            catch (IOException) { return -1; }
            catch (UnauthorizedAccessException) { return -1; }
            catch (ArgumentException) { return -1; }
        }

        internal (string Phase, long Completed) Context
        {
            get
            {
                lock (gate)
                    return (phase, completed);
            }
        }
    }

    private sealed class PeriodicDiskUsageSampler : IAsyncDisposable
    {
        private readonly DiskUsageTracker tracker;
        private readonly string path;
        private readonly TimeSpan interval;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task worker;
        private int stopped;

        public PeriodicDiskUsageSampler(DiskUsageTracker tracker, string path, TimeSpan interval)
        {
            this.tracker = tracker;
            this.path = path;
            this.interval = interval;
            worker = RunAsync();
        }

        public Exception? Error { get; private set; }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref stopped, 1) == 0)
                cancellation.Cancel();

            try
            {
                await worker.ConfigureAwait(false);
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

        private async Task RunAsync()
        {
            try
            {
                await using var writer = new StreamWriter(
                    new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous),
                    new UTF8Encoding(false));
                while (true)
                {
                    var context = tracker.Context;
                    var storage = tracker.Sample(context.Phase, context.Completed);
                    var process = Process.GetCurrentProcess();
                    var sample = new
                    {
                        utc = DateTimeOffset.UtcNow,
                        phase = context.Phase,
                        completed = context.Completed,
                        workingSetBytes = process.WorkingSet64,
                        peakWorkingSetBytes = process.PeakWorkingSet64,
                        openHandles = process.HandleCount,
                        managedBytes = GC.GetTotalMemory(forceFullCollection: false),
                        localStorageBytes = storage.LocalStorageBytes,
                        ssdCacheBytes = storage.CacheBytes,
                        stagingBytes = storage.StagingBytes,
                        availableFreeSpaceBytes = storage.AvailableFreeSpaceBytes,
                        outputMount = tracker.OutputMount,
                        minimumFreeSpaceBytes = storage.MinimumFreeSpaceBytes,
                        minimumObservedFreeSpaceBytes = storage.MinimumObservedFreeSpaceBytes,
                        freeSpaceLimitViolated = storage.FreeSpaceLimitViolation is not null,
                        freeSpaceLimitViolation = storage.FreeSpaceLimitViolation,
                        cgroup = BenchmarkCgroupSnapshot.Capture(),
                    };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(sample)).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                    if (tracker.LimitViolation is not null || tracker.SamplerError is not null)
                        return;
                    await Task.Delay(interval, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested || tracker.FailureCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                tracker.RecordSamplerError(exception);
                Error = exception;
            }
        }
    }

    private enum PayloadMode
    {
        Repeated,
        PerKeyIncompressible,
    }

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

        public static string GeneratorName(PayloadMode mode) => mode switch
        {
            PayloadMode.Repeated => "repeated-x",
            PayloadMode.PerKeyIncompressible => "sha256-base64url-per-key",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        public string For(long number) => mode switch
        {
            PayloadMode.Repeated => repeatedPayload,
            PayloadMode.PerKeyIncompressible => CreateIncompressibleBody(payloadBytes, seed, number),
            _ => throw new InvalidOperationException($"Unsupported payload mode '{mode}'."),
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
                {
                    body.Append(encoded[index] switch
                    {
                        '+' => '-',
                        '/' => '_',
                        '=' => '.',
                        var value => value,
                    });
                }
            }

            return body.ToString();
        }
    }

    private static bool IsUsefulIsolatedCgroup(BenchmarkCgroupSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Path)
            || string.IsNullOrWhiteSpace(snapshot.MemoryMax)
            || string.Equals(snapshot.MemoryMax, "max", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(snapshot.MemoryMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
            && limit > 0;
    }

    private static OutputMount ValidateOutputMount(string output, long minimumFreeSpaceBytes)
    {
        var fullOutputPath = Path.GetFullPath(output);
        var mount = ResolveFilesystemMount(fullOutputPath);
        if (string.IsNullOrWhiteSpace(mount))
            throw new ArgumentException($"Could not determine the filesystem mount for output path '{fullOutputPath}'.");

        try
        {
            var drive = new DriveInfo(mount);
            if (!drive.IsReady)
                throw new IOException($"The output filesystem mount '{mount}' is not ready.");

            var totalSize = drive.TotalSize;
            if (totalSize <= 0)
                throw new IOException($"The output filesystem mount '{mount}' reported no usable capacity.");

            _ = drive.AvailableFreeSpace;
            if (minimumFreeSpaceBytes > totalSize)
            {
                throw new ArgumentException(
                    $"--min-free-space-bytes ({minimumFreeSpaceBytes:N0}) exceeds the output mount '{mount}' capacity ({totalSize:N0}).");
            }

            return new OutputMount(mount, totalSize);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new IOException($"Could not validate the output filesystem mount '{mount}'.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException($"Could not access the output filesystem mount '{mount}'.", exception);
        }
    }

    private static void RejectSymlinkAncestors(string output)
    {
        var fullOutputPath = Path.GetFullPath(output);
        var current = fullOutputPath;
        while (true)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"The output path or one of its ancestors is a symbolic link or reparse point: '{current}'.");
            }
            catch (FileNotFoundException)
            {
                // The final output directory may not exist yet.
            }
            catch (DirectoryNotFoundException)
            {
                // A new output ancestor may not exist yet.
            }
            catch (IOException exception)
            {
                throw new IOException($"Could not inspect output path ancestor '{current}'.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new IOException($"Could not inspect output path ancestor '{current}'.", exception);
            }

            var parent = Directory.GetParent(current);
            if (parent is null || string.Equals(parent.FullName, current, GetPathComparison()))
                return;
            current = parent.FullName;
        }
    }

    private static string? ResolveFilesystemMount(string fullPath)
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/self/mountinfo"))
        {
            string? longestMount = null;
            try
            {
                foreach (var line in File.ReadLines("/proc/self/mountinfo"))
                {
                    var separator = line.IndexOf(" - ", StringComparison.Ordinal);
                    if (separator <= 0)
                        continue;

                    var fields = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 5)
                        continue;

                    var candidate = DecodeMountInfoPath(fields[4]);
                    if (IsPathWithinMount(fullPath, candidate)
                        && (longestMount is null || candidate.Length > longestMount.Length))
                    {
                        longestMount = candidate;
                    }
                }
            }
            catch (IOException)
            {
                // Fall through to DriveInfo, which is sufficient on systems
                // where mountinfo is unavailable or transiently unreadable.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall through to DriveInfo, which is sufficient on systems
                // where mountinfo is not readable.
            }

            if (longestMount is not null)
                return longestMount;
        }

        var candidates = DriveInfo.GetDrives()
            .Where(static drive => drive.IsReady)
            .Select(static drive => Path.GetFullPath(drive.Name))
            .Where(candidate => IsPathWithinMount(fullPath, candidate))
            .OrderByDescending(static candidate => candidate.Length)
            .ToArray();
        return candidates.FirstOrDefault() ?? Path.GetPathRoot(fullPath);
    }

    private static bool IsPathWithinMount(string fullPath, string mount)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(fullPath);
        var normalizedMount = Path.TrimEndingDirectorySeparator(mount);
        if (normalizedMount.Length == 0)
            normalizedMount = Path.DirectorySeparatorChar.ToString();

        if (string.Equals(normalizedPath, normalizedMount, GetPathComparison()))
            return true;

        var prefix = normalizedMount.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedMount
            : normalizedMount + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, GetPathComparison());
    }

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string DecodeMountInfoPath(string value)
    {
        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\'
                && index + 3 < value.Length
                && IsOctal(value[index + 1])
                && IsOctal(value[index + 2])
                && IsOctal(value[index + 3]))
            {
                decoded.Append((char)((value[index + 1] - '0') * 64
                    + (value[index + 2] - '0') * 8
                    + value[index + 3] - '0'));
                index += 3;
            }
            else
            {
                decoded.Append(value[index]);
            }
        }

        return decoded.ToString();
    }

    private static bool IsOctal(char value) => value is >= '0' and <= '7';

    private static string ReadWorkload(string[] args)
    {
        foreach (var argument in args)
        {
            if (argument.StartsWith("--workload=", StringComparison.OrdinalIgnoreCase))
            {
                return argument["--workload=".Length..].Trim().ToLowerInvariant();
            }
        }

        var index = Array.IndexOf(args, "--workload");
        return index >= 0 && index + 1 < args.Length ? args[index + 1].Trim().ToLowerInvariant() : string.Empty;
    }

    private sealed class Options
    {
        public string Endpoint { get; private set; } = Environment.GetEnvironmentVariable("TERRABASE_S3_ENDPOINT") ?? "";
        public string Bucket { get; private set; } = Environment.GetEnvironmentVariable("TERRABASE_S3_BUCKET") ?? "terrabase-tiered";
        public string AccessKey { get; private set; } = Environment.GetEnvironmentVariable("TERRABASE_S3_ACCESS_KEY") ?? "";
        public string SecretKey { get; private set; } = Environment.GetEnvironmentVariable("TERRABASE_S3_SECRET_KEY") ?? "";
        public string Region { get; private set; } = Environment.GetEnvironmentVariable("TERRABASE_S3_REGION") ?? "us-east-1";
        public string Prefix { get; private set; } = $"terrabase-tiered/{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        public string Output { get; private set; } = Path.Combine("bench", "receipts", $"tiered-rustfs-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");
        public long Records { get; private set; } = 10_000;
        public int Batch { get; private set; } = 256;
        public int PayloadBytes { get; private set; } = 128;
        public PayloadMode PayloadMode { get; private set; } = PayloadMode.Repeated;
        public long CacheBytes { get; private set; } = 256L * 1024 * 1024;
        public long? MaxLocalStorageBytes { get; private set; }
        public long MinFreeSpaceBytes { get; private set; }
        public long Updates { get; private set; }
        public long Deletes { get; private set; }
        public int PointReads { get; private set; } = 512;
        public long CheckpointRecords { get; private set; } = 1_000_000;
        public long CompactEveryRecords { get; private set; }
        public int Seed { get; private set; } = 1337;
        public int ResourceSampleIntervalMilliseconds { get; private set; } = 500;
        public bool IngestOnly { get; private set; }
        public bool RequireIsolatedCgroup { get; private set; }
        public bool StateMapFlushTrace { get; private set; }
        public bool Help { get; private set; }
        public string PayloadModeName => PayloadMode switch
        {
            PayloadMode.Repeated => "repeated",
            PayloadMode.PerKeyIncompressible => "per-key-incompressible",
            _ => throw new ArgumentOutOfRangeException(),
        };

        public static Options Parse(string[] args)
        {
            var result = new Options();
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument is "--help" or "-h")
                {
                    result.Help = true;
                    continue;
                }

                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = argument.IndexOf('=');
                var name = separator > 2 ? argument[2..separator] : argument[2..];
                if (separator < 0 && string.Equals(name, "ingest-only", StringComparison.OrdinalIgnoreCase))
                {
                    result.IngestOnly = true;
                    continue;
                }

                if (separator < 0 && string.Equals(name, "require-isolated-cgroup", StringComparison.OrdinalIgnoreCase))
                {
                    result.RequireIsolatedCgroup = true;
                    continue;
                }

                if (separator < 0 && string.Equals(name, "state-map-flush-trace", StringComparison.OrdinalIgnoreCase))
                {
                    result.StateMapFlushTrace = true;
                    continue;
                }

                var value = separator > 2
                    ? argument[(separator + 1)..]
                    : index + 1 < args.Length ? args[++index] : throw new ArgumentException($"--{name} requires a value.");
                switch (name.ToLowerInvariant())
                {
                    case "workload": break;
                    case "s3-endpoint": result.Endpoint = value; break;
                    case "s3-bucket": result.Bucket = value; break;
                    case "s3-access-key":
                    case "s3-secret-key": throw new ArgumentException("Provide S3 credentials through TERRABASE_S3_ACCESS_KEY and TERRABASE_S3_SECRET_KEY environment variables.");
                    case "s3-region": result.Region = value; break;
                    case "prefix": result.Prefix = value; break;
                    case "output": result.Output = value; break;
                    case "records": result.Records = ParsePositiveLong(value, name); break;
                    case "batch": result.Batch = ParsePositive(value, name); break;
                    case "payload-bytes": result.PayloadBytes = ParsePositive(value, name); break;
                    case "payload-mode": result.PayloadMode = ParsePayloadMode(value, name); break;
                    case "cache-bytes": result.CacheBytes = ParseLong(value, name); break;
                    case "max-local-storage-bytes": result.MaxLocalStorageBytes = ParseNonNegativeLong(value, name); break;
                    case "min-free-space-bytes": result.MinFreeSpaceBytes = ParseNonNegativeLong(value, name); break;
                    case "updates": result.Updates = ParseNonNegativeLong(value, name); break;
                    case "deletes": result.Deletes = ParseNonNegativeLong(value, name); break;
                    case "point-reads": result.PointReads = ParseNonNegativeInt(value, name); break;
                    case "checkpoint-records": result.CheckpointRecords = ParsePositiveLong(value, name); break;
                    case "compact-every-records": result.CompactEveryRecords = ParseNonNegativeLong(value, name); break;
                    case "seed": result.Seed = ParseInt(value, name); break;
                    case "resource-sample-ms": result.ResourceSampleIntervalMilliseconds = ParsePositive(value, name); break;
                    case "ingest-only": result.IngestOnly = ParseBool(value, name); break;
                    case "require-isolated-cgroup": result.RequireIsolatedCgroup = ParseBool(value, name); break;
                    case "state-map-flush-trace": result.StateMapFlushTrace = ParseBool(value, name); break;
                    default: throw new ArgumentException($"Unknown option '--{name}'.");
                }
            }

            if (result.Help)
            {
                return result;
            }

            if (!Uri.TryCreate(result.Endpoint, UriKind.Absolute, out _)
                || string.IsNullOrWhiteSpace(result.AccessKey)
                || string.IsNullOrWhiteSpace(result.SecretKey))
            {
                throw new ArgumentException("RustFS mode requires S3 endpoint, access key, and secret key (arguments or TERRABASE_S3_* environment variables).");
            }

            if (result.Records > 9_999_999_999L)
                throw new ArgumentException("--records must be <= 9,999,999,999 because the qualification document ids use D10 formatting.");
            if (result.Updates > result.Records
                || result.Deletes > result.Records
                || result.Updates > result.Records - result.Deletes)
                throw new ArgumentException("--updates plus --deletes must not exceed --records.");
            if (result.CompactEveryRecords > 0 && result.Records > 1_000_000)
                throw new ArgumentException("--compact-every-records is limited to runs of at most 1,000,000 records because periodic full compaction is intentionally a small-run diagnostic.");

            return result;
        }

        public static void PrintHelp() => Console.WriteLine("--workload tiered-segment --records N --batch N --payload-bytes N --payload-mode repeated|per-key-incompressible --updates N --deletes N --point-reads N --checkpoint-records N --compact-every-records N --seed N --cache-bytes N --max-local-storage-bytes N --min-free-space-bytes N --resource-sample-ms N [--ingest-only] [--require-isolated-cgroup] [--state-map-flush-trace] --s3-endpoint URL --s3-bucket NAME --output DIR (credentials: TERRABASE_S3_ACCESS_KEY / TERRABASE_S3_SECRET_KEY)");

        private static PayloadMode ParsePayloadMode(string value, string name) => value.Trim().ToLowerInvariant() switch
        {
            "repeated" => PayloadMode.Repeated,
            "per-key-incompressible" => PayloadMode.PerKeyIncompressible,
            _ => throw new ArgumentException($"--{name} must be 'repeated' or 'per-key-incompressible'."),
        };

        private static bool ParseBool(string value, string name) => bool.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"--{name} must be true or false.");

        private static int ParsePositive(string value, string name) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"--{name} must be a positive integer.");

        private static long ParseLong(string value, string name) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"--{name} must be a non-negative integer.");

        private static long ParsePositiveLong(string value, string name) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"--{name} must be a positive integer.");

        private static long ParseNonNegativeLong(string value, string name) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"--{name} must be a non-negative integer.");

        private static int ParseNonNegativeInt(string value, string name) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"--{name} must be a non-negative integer.");

        private static int ParseInt(string value, string name) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"--{name} must be a 32-bit integer.");
    }
}
