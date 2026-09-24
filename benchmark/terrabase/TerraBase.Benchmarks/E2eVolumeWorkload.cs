using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Backup;
using TerraBase.Schema;
using TerraBase.Storage.Constraints;
using TerraBase.Indexing;
using TerraBase.Indexing.Automatic;
using TerraBase.Indexing.Runtime;
using TerraBase.Storage.Backup;
using TerraBase.StorageEngine;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>
/// End-to-end volume qualification: bulk writes of deterministic random
/// documents, exact read-back verification, a point-read benchmark, bulk
/// updates and deletes, full plus incremental backups, PITR and final
/// restores verified document-by-document (deleted documents must be absent),
/// an indexed query benchmark against the restored engine, and a compaction
/// pass verified by another full exact scan. Expected bodies are regenerated
/// statelessly from the seed so verification never holds a second copy of the
/// corpus in memory.
/// </summary>
public static class E2eVolumeWorkload
{
    private const int DocumentIdPrefixLength = 4; // "doc-"
    // Keep every volume document id at one fixed width.  The previous D8
    // format ordered ids correctly only while the corpus stayed below
    // 100,000,000 rows; a larger corpus produced a ninth digit and the exact
    // verifier rejected it.  Ten digits cover every non-negative int index,
    // which is also the largest value accepted by --documents.
    private const int DocumentIdDigits = 10;
    private const string SourceOwnershipMarkerName = ".terrabase-e2e-source-owned";
    private const string SourceOwnershipMarkerPrefix = "terrabase-e2e-volume-benchmark-source-v1:";
    private const string SourceJournalObjectPrefix = "e2e-volume-source-journal";
    private const string SourceJournalRegion = "rustfs";
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly TimeSpan ActivationSettleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ActivationSettlePollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly EntityId DatabaseIdentity =
        EntityId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", CultureInfo.InvariantCulture);
    private static readonly EntityId SourceWriterIdentity =
        EntityId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAX", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsRequested(string[] args)
    {
        var workload = ReadWorkload(args);
        return workload is "e2e-volume" or "e2e-100k" or "e2e-10m";
    }

    public static async Task<int> RunAsync(string[] args)
    {
        E2eOptions options;
        try
        {
            options = E2eOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid end-to-end volume configuration: {exception.Message}");
            E2eOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            E2eOptions.PrintHelp();
            return 0;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        if (options.RootLossRecovery
            && (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)))
        {
            throw new InvalidDataException(
                "--root-loss-recovery requires a fresh output directory that does not already exist.");
        }

        Directory.CreateDirectory(outputDirectory);
        E2eResourceTracker? resources = null;
        E2eTemporaryRootScope? temporaryRoot = null;
        try
        {
            temporaryRoot = E2eTemporaryRootScope.Create(outputDirectory, options.TempRoot);
            var rustFsDataPathStatus = ValidateRustFsDataPath(options);
            resources = E2eResourceTracker.Start(
                outputDirectory,
                Path.Combine(outputDirectory, "source"),
                Path.Combine(outputDirectory, "targets"),
                Path.Combine(outputDirectory, "uploads"),
                temporaryRoot.Path,
                temporaryRoot.ResolvedTempPath,
                options.RustFsDataPath,
                options.MaxLocalStorageBytes,
                options.MinimumFreeSpaceBytes,
                options.CheckpointRecords,
                options.ResourceSampleIntervalMilliseconds,
                options.RequireIsolatedCgroup,
                rustFsDataPathStatus);
            ActiveResources = resources;
            if (options.RequireIsolatedCgroup && !resources.IsolatedCgroup)
            {
                throw new InvalidOperationException(
                    "--require-isolated-cgroup requires a finite cgroup memory.max and a readable cgroup path; run the benchmark in an isolated cgroup.");
            }
            var (receipt, _) = await RunCoreAsync(options, outputDirectory).ConfigureAwait(false);
            await resources.CompleteAsync().ConfigureAwait(false);
            receipt = receipt with { Resources = resources.Snapshot() };
            var summary = E2eMarkdown.Create(receipt);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "e2e-volume-receipt.json"),
                JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "e2e-volume-summary.md"),
                summary).ConfigureAwait(false);
            Console.WriteLine($"End-to-end volume receipt: {outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            if (resources is not null)
                await resources.FailAsync(exception).ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            ActiveResources = null;
            if (resources is not null)
                await resources.DisposeAsync().ConfigureAwait(false);
            temporaryRoot?.Dispose();
        }
    }

    private static E2eResourceTracker? ActiveResources { get; set; }

    private static async Task<(E2eReceipt Receipt, string Summary)> RunCoreAsync(
        E2eOptions options,
        string outputDirectory)
    {
        var sourcePath = Path.Combine(outputDirectory, "source");
        var objectStorePath = Path.Combine(outputDirectory, "object-store");
        var targetRoot = Path.Combine(outputDirectory, "targets");
        var subject = RunReceipt.CaptureSubject();
        var restoreTargetCleanup = new RestoreTargetCleanupLedger(targetRoot);
        if (options.RootLossRecovery && HasReparsePointInPath(outputDirectory))
            throw new InvalidDataException("Root-loss recovery requires an output directory without symlink or reparse-point ancestors.");
        if (options.RootLossRecovery
            && Directory.EnumerateFileSystemEntries(outputDirectory)
                .Any(path => !IsResourceOwnedEntry(path, outputDirectory, options.TempRoot)))
            throw new InvalidDataException(
                "--root-loss-recovery requires a fresh output directory with no preexisting entries.");

        var sourceOwnershipMarkerContents = $"{SourceOwnershipMarkerPrefix}{Guid.NewGuid():N}\n";
        foreach (var path in new[] { sourcePath, objectStorePath, targetRoot })
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(sourcePath);
        await File.WriteAllTextAsync(
            Path.Combine(sourcePath, SourceOwnershipMarkerName),
            sourceOwnershipMarkerContents).ConfigureAwait(false);

        using var rustfs = options.ObjectTier == "s3" ? await RustFsWorkloadStore.OpenAsync(Path.Combine(outputDirectory, "uploads")).ConfigureAwait(false) : null;
        var cachePath = Path.Combine(sourcePath, "ssd-cache");
        IStorageSegmentStore? segments = rustfs is null ? null : new TieredStorageSegmentStore(
            new TieredStorageSegmentStoreOptions(rustfs.Store, cachePath, "segments", options.CacheBytes));
        var sourceJournal = rustfs is null
            ? null
            : new RemoteCommitJournal(
                rustfs.Store,
                DatabaseIdentity,
                SourceJournalRegion,
                SourceWriterIdentity,
                SourceJournalObjectPrefix);
        ActiveResources?.SetRemoteStore(rustfs?.Store);
        if (rustfs is not null)
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "rustfs-bucket.txt"), rustfs.Bucket).ConfigureAwait(false);
        DurableStorageEngine OpenSource() => new(
            sourcePath,
            segmentStore: segments,
            remoteCommitJournal: sourceJournal);

        var tenant = new TenantId("e2e-volume");
        var collection = new CollectionId("documents");
        var Users = new CollectionId("users");
        var Orders = new CollectionId("orders");
        TimeSpan relationshipsWriteElapsed = TimeSpan.Zero;
        TimeSpan relationshipsVerifyElapsed = TimeSpan.Zero;
        var relationshipVerifyPasses = 0;
        var relationshipEndpointsVerified = 0L;
        var queriesVerified = 0;
        int relationshipRebuildCount;
        long verifiedSnapshot;
        int verifiedCollections;
        TimeSpan snapshotPagingElapsed;
        TimeSpan collectionListingElapsed;

        // Phase 1: bulk writes of random documents in committed batches.
        var writeStarted = Stopwatch.GetTimestamp();
        Phase("bulk-write");
        await using (var engine = OpenSource())
        {
            // Backup capture requires a durable replication journal identity.
            await engine.InitializeJournalIdentityAsync().ConfigureAwait(false);
            for (var batchStart = 0; batchStart < options.Documents;)
            {
                ActiveResources?.ThrowIfExceeded();
                var count = Math.Min(options.BatchSize, options.Documents - batchStart);
                var mutations = new StorageMutation[count];
                for (var index = 0; index < count; index++)
                {
                    var documentIndex = batchStart + index;
                    mutations[index] = StorageMutation.Upsert(
                        new DocumentKey(tenant, collection, DocumentId(documentIndex)),
                        CreateRandomBody(options.Seed, documentIndex, updated: false),
                        DocumentPrecondition.Missing);
                }

                var result = await engine.ApplyBatchAsync(mutations).ConfigureAwait(false);
                if (result.Outcome != StorageBatchOutcome.Committed)
                    throw new InvalidDataException($"Batch at {batchStart} was not committed: {result.Outcome}.");

                // Advance by the committed count. Using BatchSize here can
                // wrap when both values are close to Int32.MaxValue even
                // though the final batch itself is valid.
                batchStart = checked(batchStart + count);
                await RecordProgressAsync("bulk-write", batchStart, options.Documents).ConfigureAwait(false);
            }

            if (options.Relationships > 0)
            {
                // Relationship documents are written as plain engine
                // batches and their edges are derived afterwards with the
                // schema-driven constraint rebuild: the DocumentStore's
                // single-document constraint path costs an engine-wide scan
                // per write, which is quadratic in the corpus (measured: two
                // seconds at a 20k corpus, still running after fifteen
                // minutes at 100k for the same 10k relationships).
                var relationshipsStarted = Stopwatch.GetTimestamp();
                Phase("relationship-write");
                await WriteRelationshipsAsync(engine, tenant, Users, Orders, options).ConfigureAwait(false);
                var schemas = BuildRelationshipSchemas(tenant, Users, Orders);
                relationshipRebuildCount = await engine
                    .RebuildConstraintRecordsAsync(tenant, schemas)
                    .ConfigureAwait(false);
                relationshipsWriteElapsed = Stopwatch.GetElapsedTime(relationshipsStarted);
            }
        }

        var writeElapsed = Stopwatch.GetElapsedTime(writeStarted);
        if (segments is not null)
        {
            await using (var engine = OpenSource()) await engine.CompactAsync().ConfigureAwait(false);
            foreach (var file in Directory.EnumerateFiles(cachePath, "*.seg")) File.Delete(file);
        }
        ReclaimPhaseMemory();

        // Phases 2 and 3 share one engine session: the exact read-back scan is
        // followed by the point-read benchmark over the same open engine.
        E2eLatencyStats? pointReadStats;
        TimeSpan pointReadElapsed;
        long verifiedOriginal;
        TimeSpan readbackElapsed;
        await using (var engine = OpenSource())
        {
            (verifiedOriginal, readbackElapsed) = await VerifyScanAsync(
                engine,
                tenant,
                collection,
                ExpectedState.Initial(options.Documents, options.Seed)).ConfigureAwait(false);
            (verifiedSnapshot, verifiedCollections, snapshotPagingElapsed, collectionListingElapsed) =
                await VerifySnapshotPagingAsync(
                    engine,
                    tenant,
                    collection,
                    options,
                    ExpectedState.Initial(options.Documents, options.Seed)).ConfigureAwait(false);
            (pointReadStats, pointReadElapsed) = options.PointReads == 0
                ? (null, TimeSpan.Zero)
                : await PointReadBenchmarkAsync(engine, tenant, collection, options).ConfigureAwait(false);

            if (options.Relationships > 0)
            {
                var relationshipsVerifyStarted = Stopwatch.GetTimestamp();
                Phase("relationship-verify");
                relationshipEndpointsVerified += await VerifyRelationshipsAsync(tenant, Users, Orders, engine, options).ConfigureAwait(false);
                relationshipVerifyPasses++;
                relationshipsVerifyElapsed = Stopwatch.GetElapsedTime(relationshipsVerifyStarted);
            }
        }

        ReclaimPhaseMemory();

        IBackupObjectStore store = rustfs?.Store ?? new FileSystemBackupObjectStore(objectStorePath);
        ActiveResources?.SetRemoteStore(store);
        using var signer = new HmacSha256BackupManifestSignatureProvider(
            "terrabase-e2e-volume-trust-root-2026"u8,
            "e2e-volume-root-v1");
        var encryption = new NoBackupEncryption();
        var manager = new BackupManager(
            store,
            encryption,
            signer,
            new BackupEncryptionKeyResolver([new BackupEncryptionKeyResolverEntry("e2e-volume", null, encryption)]),
            new BackupManifestSignatureKeyResolver([new BackupManifestSignatureKeyResolverEntry("e2e-volume", null, signer)]));

        // Phases 4 through 7 share ONE engine session: each fresh engine open
        // at this volume rebuilds the full in-memory state, so reopening
        // between the full backup and the publications only spends minutes and
        // memory. The incremental is still taken from a FRESH source instance
        // over the same engine, pinning the fresh-instance base cursor
        // resolution. Updates touch [0, Updates) once (v1 -> v2); deletes
        // remove the tail range [Documents - Deletes, Documents). The ranges
        // never overlap, so expected state stays statelessly derivable.
        var updateBatches = BatchCount(options.Updates, options.BatchSize);
        var deleteBatches = BatchCount(options.Deletes, options.BatchSize);

        BackupManifest full;
        BackupManifest incremental;
        BackupLogEntry[] logEntries;
        TimeSpan fullBackupElapsed;
        TimeSpan updatesElapsed;
        TimeSpan deletesElapsed;
        TimeSpan incrementalBackupElapsed;
        long updatesStarted;
        long deletesStarted;

        // The full backup runs in its own engine session: the capture's
        // retained index and buffers are fully released before the
        // publication session reopens the engine, so the incremental's
        // whole-log serialization never shares a heap with the full
        // capture's leftovers.
        {
            await using var engine = OpenSource();
            // The backup timer starts after the engine's in-memory state is
            // rebuilt so the phase row measures the backup, not the reopen.
            var fullBackupStarted = Stopwatch.GetTimestamp();
            Phase("full-backup");
            using (var source = new StorageEngineBackupSource(
                "e2e-volume",
                engine,
                new StaticMetadataProvider(tenant.Value, collection.Value)))
            {
                full = await manager.CreateFullBackupAsync(source).ConfigureAwait(false);
            }

            fullBackupElapsed = Stopwatch.GetElapsedTime(fullBackupStarted);
        }

        ReclaimPhaseMemory();
        await using (var engine = OpenSource())
        {
            updatesStarted = Stopwatch.GetTimestamp();
            Phase("bulk-updates");

            using (var freshSource = new StorageEngineBackupSource(
                "e2e-volume",
                engine,
                new StaticMetadataProvider(tenant.Value, collection.Value)))
            {
                // Bulk updates: document i in [0, Updates) moves to v2.
                for (var batchStart = 0; batchStart < options.Updates;)
                {
                    ActiveResources?.ThrowIfExceeded();
                    var count = Math.Min(options.BatchSize, options.Updates - batchStart);
                    var mutations = new StorageMutation[count];
                    for (var index = 0; index < count; index++)
                    {
                        var documentIndex = batchStart + index;
                        mutations[index] = StorageMutation.Upsert(
                            new DocumentKey(tenant, collection, DocumentId(documentIndex)),
                            CreateRandomBody(options.Seed, documentIndex, updated: true),
                            DocumentPrecondition.Version(1));
                    }

                    var result = await engine.ApplyBatchAsync(mutations).ConfigureAwait(false);
                    if (result.Outcome != StorageBatchOutcome.Committed)
                        throw new InvalidDataException($"Update batch at {batchStart} was not committed: {result.Outcome}.");

                    batchStart = checked(batchStart + count);
                    await RecordProgressAsync("bulk-updates", batchStart, options.Updates).ConfigureAwait(false);
                }

                updatesElapsed = Stopwatch.GetElapsedTime(updatesStarted);
                deletesStarted = Stopwatch.GetTimestamp();
                Phase("bulk-deletes");

                // Bulk deletes: the tail range [Documents - Deletes, Documents)
                // is removed with tombstones.
                var firstDeleted = options.Documents - options.Deletes;
                for (var batchStart = 0; batchStart < options.Deletes;)
                {
                    ActiveResources?.ThrowIfExceeded();
                    var count = Math.Min(options.BatchSize, options.Deletes - batchStart);
                    var mutations = new StorageMutation[count];
                    for (var index = 0; index < count; index++)
                    {
                        mutations[index] = StorageMutation.Delete(
                            new DocumentKey(tenant, collection, DocumentId(firstDeleted + batchStart + index)),
                            DocumentPrecondition.Version(1));
                    }

                    var result = await engine.ApplyBatchAsync(mutations).ConfigureAwait(false);
                    if (result.Outcome != StorageBatchOutcome.Committed)
                        throw new InvalidDataException($"Delete batch at {batchStart} was not committed: {result.Outcome}.");

                    batchStart = checked(batchStart + count);
                    await RecordProgressAsync("bulk-deletes", batchStart, options.Deletes).ConfigureAwait(false);
                }

                deletesElapsed = Stopwatch.GetElapsedTime(deletesStarted);
            }
        }

        // The incremental capture re-scans the full physical chain, exactly
        // like the full backup's authority pass; it gets its own fresh
        // engine session so that scan never shares a heap with the
        // publication phase's leftovers.
        ReclaimPhaseMemory();
        {
            await using var engine = OpenSource();
            using var freshSource = new StorageEngineBackupSource(
                "e2e-volume",
                engine,
                new StaticMetadataProvider(tenant.Value, collection.Value));
            var incrementalBackupStarted = Stopwatch.GetTimestamp();
            Phase("incremental-backup");
            incremental = await manager.CreateIncrementalBackupAsync(freshSource, full).ConfigureAwait(false);
            incrementalBackupElapsed = Stopwatch.GetElapsedTime(incrementalBackupStarted);

            var entries = new List<BackupLogEntry>();
            await foreach (var entry in freshSource.ReadLogAsync(
                    full.Boundary.Sequence,
                    incremental.Boundary.Sequence).ConfigureAwait(false))
            {
                entries.Add(entry);
            }

            logEntries = entries.ToArray();
            if (logEntries.Length != updateBatches + deleteBatches)
                throw new InvalidDataException(
                    $"Expected {updateBatches + deleteBatches} incremental log entries "
                    + $"({updateBatches} update + {deleteBatches} delete batches), got {logEntries.Length}.");
        }

        E2eRootLossRecoveryMeasurements? rootLossRecovery = null;
        if (options.RootLossRecovery)
        {
            if (rustfs is null)
                throw new InvalidDataException("--root-loss-recovery requires --object-tier s3 so the restore reads committed remote backup artifacts.");

            // At this point the source engine and backup source are closed, and
            // both the full and incremental manifests have been committed to
            // the object tier. The opt-in gate is deliberately here so no
            // later phase can accidentally depend on the local source root.
            ReclaimPhaseMemory();
            rootLossRecovery = await RunRootLossRecoveryAsync(
                sourcePath,
                outputDirectory,
                targetRoot,
                manager,
                full,
                incremental,
                sourceOwnershipMarkerContents,
                logEntries.Length,
                rustfs.Store,
                sourceJournal!,
                OpenSource,
                tenant,
                collection,
                options,
                restoreTargetCleanup).ConfigureAwait(false);
        }

        // The source engine is closed before the restore phases so a restore
        // target's own full in-memory state never coexists with the source's.
        ReclaimPhaseMemory();

        // Phase 8: restore to the full boundary and verify every original
        // document survived backup and restore, still at version 1 (the full
        // snapshot predates every update and delete).
        var fullRestoreStarted = Stopwatch.GetTimestamp();
        Phase("full-boundary-restore");
        var fullTargetPath = Path.Combine(targetRoot, "full");
        var fullTarget = new StorageEngineRestoreTarget(fullTargetPath);
        var fullRestore = await manager.RestoreToTimeAsync(
            "e2e-volume",
            full.Boundary.Timestamp,
            fullTarget).ConfigureAwait(false);
        var fullRestoreElapsed = Stopwatch.GetElapsedTime(fullRestoreStarted);
        if (fullRestore.AppliedLogEntries != 0)
            throw new InvalidDataException(
                $"The full-boundary restore applied {fullRestore.AppliedLogEntries} log entries.");

        long verifiedFullRestore;
        TimeSpan fullVerifyElapsed;
        await using (var engine = new DurableStorageEngine(fullTarget.ResolveActivePath()))
        {
            (verifiedFullRestore, fullVerifyElapsed) = await VerifyScanAsync(
                engine,
                tenant,
                collection,
                ExpectedState.Initial(options.Documents, options.Seed)).ConfigureAwait(false);
            if (options.Relationships > 0)
            {
                Phase("relationship-verify-full-restore");
                relationshipEndpointsVerified += await VerifyRelationshipsAsync(tenant, Users, Orders, engine, options).ConfigureAwait(false);
                relationshipVerifyPasses++;
            }
        }

        ReclaimPhaseMemory();
        // The full-boundary state has been verified document-by-document and
        // its counts and artifact identities are retained in the receipt. It
        // need not consume a second corpus-sized root while PITR and final
        // restores are materialized. Keep the final target below.
        restoreTargetCleanup.Delete(fullTargetPath, "full");

        // Phase 9: interior PITR restore. The cut prefers the boundary between
        // the update publications and the delete publications; with a single
        // phase it falls back to the midpoint of the first two entries, as in
        // prior qualifications. Expected counts derive from the actual entry
        // timestamps and publication order, never from timing assumptions:
        // entries are sequence-ordered batches, so applying k entries means
        // k * BatchSize update documents and every delete batch after them.
        var pitrEntries = ChoosePitrEntryCount(logEntries.Length, updateBatches, deleteBatches);
        // Deletes publish ascending from the head of the tail range, so a
        // partial application removes the FIRST appliedDeletes ids of that
        // range, not the tail-most ones.
        var appliedUpdateCount = Math.Min(
            (long)options.Updates,
            (long)pitrEntries * options.BatchSize);
        var appliedDeleteCount = Math.Min(
            (long)options.Deletes,
            (long)Math.Max(0, pitrEntries - updateBatches) * options.BatchSize);
        var pitrState = new ExpectedState(
            options.Documents,
            options.Seed,
            checked((int)appliedUpdateCount),
            options.Documents - options.Deletes,
            checked((int)appliedDeleteCount));
        var pitTimestamp = pitrEntries == logEntries.Length
            ? logEntries[^1].Watermark.Timestamp
            : Midpoint(logEntries[pitrEntries - 1].Watermark.Timestamp, logEntries[pitrEntries].Watermark.Timestamp);

        var pitrRestoreStarted = Stopwatch.GetTimestamp();
        Phase("pitr-restore");
        var pitrTargetPath = Path.Combine(targetRoot, "pitr");
        var pitrTarget = new StorageEngineRestoreTarget(pitrTargetPath);
        var pitrRestore = await manager.RestoreToTimeAsync(
            "e2e-volume",
            pitTimestamp,
            pitrTarget).ConfigureAwait(false);
        var pitrRestoreElapsed = Stopwatch.GetElapsedTime(pitrRestoreStarted);
        if (pitrRestore.AppliedLogEntries != pitrEntries)
            throw new InvalidDataException(
                $"The PITR restore applied {pitrRestore.AppliedLogEntries} entries, expected {pitrEntries}.");

        long verifiedPitr;
        TimeSpan pitrVerifyElapsed;
        await using (var engine = new DurableStorageEngine(pitrTarget.ResolveActivePath()))
        {
            (verifiedPitr, pitrVerifyElapsed) = await VerifyScanAsync(
                engine,
                tenant,
                collection,
                pitrState).ConfigureAwait(false);
            if (options.Relationships > 0)
            {
                Phase("relationship-verify-pitr");
                relationshipEndpointsVerified += await VerifyRelationshipsAsync(tenant, Users, Orders, engine, options).ConfigureAwait(false);
                relationshipVerifyPasses++;
            }
        }

        ReclaimPhaseMemory();
        // PITR has its own exact verification and applied-entry count in the
        // receipt. Release its materialized root before building the final
        // restore so at most one verified restore corpus remains resident on
        // disk at a time (apart from an opt-in root-loss run in progress).
        restoreTargetCleanup.Delete(pitrTargetPath, "pitr");

        // Phase 10: final restore through the whole chain. Every update is
        // present at v2, every deleted document is absent, and the survivors
        // match exactly — the no-data-loss guarantee end to end.
        var finalRestoreStarted = Stopwatch.GetTimestamp();
        Phase("final-restore");
        var finalTargetPath = Path.Combine(targetRoot, "final");
        var finalTarget = new StorageEngineRestoreTarget(finalTargetPath);
        var finalRestore = await manager.RestoreToTimeAsync(
            "e2e-volume",
            incremental.Boundary.Timestamp,
            finalTarget).ConfigureAwait(false);
        var finalRestoreElapsed = Stopwatch.GetElapsedTime(finalRestoreStarted);
        if (finalRestore.AppliedLogEntries != logEntries.Length)
            throw new InvalidDataException(
                $"The final restore applied {finalRestore.AppliedLogEntries} entries, expected {logEntries.Length}.");

        long verifiedFinal;
        long verifiedAbsent;
        TimeSpan finalVerifyElapsed;
        E2eLatencyStats? queryStats;
        E2eQueryDiagnostics? queryDiagnostics;
        TimeSpan indexBuildElapsed;
        TimeSpan queryElapsed;
        TimeSpan compactionElapsed;
        long verifiedCompacted;
        TimeSpan compactVerifyElapsed = TimeSpan.Zero;
        StorageCompactionResult compaction = new(0, 0, 0, null!);
        await using (var engine = new DurableStorageEngine(finalTarget.ResolveActivePath()))
        {
            var finalVerifyStarted = Stopwatch.GetTimestamp();
            var finalState = new ExpectedState(
                options.Documents,
                options.Seed,
                options.Updates,
                options.Documents - options.Deletes,
                options.Deletes);
            (verifiedFinal, _) = await VerifyScanAsync(
                engine,
                tenant,
                collection,
                finalState).ConfigureAwait(false);

            // Point-read absence sweep: every deleted id must read as null on
            // the restored engine, proving the tombstones landed.
            verifiedAbsent = 0;
            var firstDeleted = options.Documents - options.Deletes;
            for (var index = firstDeleted; index < options.Documents; index++)
            {
                var document = await engine.ReadAsync(new DocumentKey(tenant, collection, DocumentId(index))).ConfigureAwait(false);
                if (document is not null)
                    throw new InvalidDataException($"{DocumentId(index)} survived the final restore but was deleted.");
                verifiedAbsent++;
            }

            // The phase row covers the exact scan plus the absence sweep.
            finalVerifyElapsed = Stopwatch.GetElapsedTime(finalVerifyStarted);
            if (options.Relationships > 0)
            {
                Phase("relationship-verify-final-restore");
                relationshipEndpointsVerified += await VerifyRelationshipsAsync(tenant, Users, Orders, engine, options).ConfigureAwait(false);
                relationshipVerifyPasses++;
            }

            ReclaimPhaseMemory();

            // Phase 11: indexed query benchmark on the restored engine. The
            // automatic index on "sequence" is built and activated first
            // (itself a measured engine function), then deterministic range
            // windows are verified document-by-document against the
            // stateless expectation.
            queryStats = null;
            queryDiagnostics = null;
            indexBuildElapsed = TimeSpan.Zero;
            queryElapsed = TimeSpan.Zero;
            if (options.Queries > 0)
            {
                var indexBuildStarted = Stopwatch.GetTimestamp();
                Phase("index-build");
                await using (var runtime = await AutomaticIndexRuntime.OpenAsync(
                        engine,
                        new AutomaticIndexRuntimeOptions
                        {
                            DisablePageCache = true,
                            FeedPump = new AutomaticIndexFeedPumpOptions
                            {
                                SnapshotLimits = new IndexSnapshotBuildLimits
                                {
                                    MaximumDocuments = options.SnapshotBuildMaximumDocuments,
                                },
                            },
                        }).ConfigureAwait(false))
                {
                    var scope = new AutomaticIndexScope(tenant.Value, DatabaseIdentity.ToString(), collection.Value);
                    var candidate = new AutomaticIndexCandidate(scope, ["sequence"]);
                    await runtime.ApplyManualAsync(
                        new ScoredIndexCandidate(candidate, new CandidateScore(100, 90, 5, 1))).ConfigureAwait(false);
                    await runtime.BuildAsync(candidate.CandidateId).ConfigureAwait(false);
                    var baseline = Enumerable
                        .Repeat(new LogicalWork(100, 100, 200), AutomaticIndexShadowVerifier.MinimumSamples)
                        .ToArray();
                    var indexed = Enumerable
                        .Repeat(new LogicalWork(1, 1, 2), AutomaticIndexShadowVerifier.MinimumSamples)
                        .ToArray();
                    var verified = await runtime.VerifyShadowAsync(candidate.CandidateId, baseline, indexed).ConfigureAwait(false);
                    if (verified.Record.Phase != AutomaticIndexPhase.Active)
                        throw new InvalidDataException(
                            $"The benchmark sequence index did not activate ({verified.Record.Phase}).");
                    indexBuildElapsed = Stopwatch.GetElapsedTime(indexBuildStarted);

                    (queryStats, queryElapsed, queryDiagnostics) = await QueryBenchmarkAsync(
                        runtime,
                        scope,
                        tenant,
                        collection,
                        options,
                        outputDirectory).ConfigureAwait(false);
                    queriesVerified = options.Queries;
                }
            }

            // Phase 12: compaction on the fully verified restored engine, then
            // one more exact scan — compaction must not change a single byte.
            // A compaction publishes ONE segment, so a corpus at or beyond the
            // per-segment record limit (live documents plus retained
            // tombstones plus internal rows) cannot compact at all; that is a
            // capacity fact of the engine, recorded rather than failed.
            var compactionStarted = Stopwatch.GetTimestamp();
            Phase("compaction");
            var compacted = false;
            try
            {
                compaction = await engine.CompactAsync().ConfigureAwait(false);
                compacted = true;
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("cannot publish more than", StringComparison.Ordinal))
            {
                Phase("compaction-skipped-above-segment-record-limit");
            }

            compactionElapsed = compacted ? Stopwatch.GetElapsedTime(compactionStarted) : TimeSpan.Zero;
            if (compacted)
            {
                var compactVerifyStarted = Stopwatch.GetTimestamp();
                (verifiedCompacted, compactVerifyElapsed) = await VerifyScanAsync(
                    engine,
                    tenant,
                    collection,
                    new ExpectedState(
                        options.Documents,
                        options.Seed,
                        options.Updates,
                        options.Documents - options.Deletes,
                        options.Deletes)).ConfigureAwait(false);
                compactVerifyElapsed += Stopwatch.GetElapsedTime(compactVerifyStarted);
                if (verifiedCompacted != options.Documents - options.Deletes)
                    throw new InvalidDataException(
                        $"The compacted engine holds {verifiedCompacted} documents, expected {options.Documents - options.Deletes}.");
            }
            else
            {
                // The engine above the segment record limit is left
                // untouched; the final-restore scan already verified this
                // exact state.
                verifiedCompacted = verifiedFinal;
            }
        }

        long verifiedColdRemote = 0;
        int verifiedColdRemoteCollections = 0;
        int remoteSegmentObjects = 0;
        long coldRemoteCacheBytes = 0;
        long restoredLocalSegmentBytesAfterEviction = 0;
        TimeSpan coldRemoteElapsed = TimeSpan.Zero;
        if (rustfs is not null)
        {
            Phase("cold-remote-final-restore");
            (verifiedColdRemote, verifiedColdRemoteCollections, remoteSegmentObjects, coldRemoteCacheBytes, restoredLocalSegmentBytesAfterEviction, coldRemoteElapsed) =
                await VerifyColdRemoteRestoreAsync(
                    finalTarget.ResolveActivePath(),
                    rustfs.Store,
                    tenant,
                    collection,
                    options).ConfigureAwait(false);
        }

        // The final target has now passed final verification, compaction
        // verification, and the cold remote reopen. No later phase needs a
        // local restore root, so release it before recording the receipt.
        restoreTargetCleanup.Delete(finalTargetPath, "final");
        var restoreTargetCleanupMeasurements = restoreTargetCleanup.Complete();

        var sourceAckDurability = await CaptureSourceAckDurabilityAsync(
            sourceJournal,
            rustfs?.Store).ConfigureAwait(false);

        var restorePoints = await manager.ListRestorePointsAsync("e2e-volume").ConfigureAwait(false);
        if (restorePoints.Count != 2)
            throw new InvalidDataException(
                $"Expected two committed manifests, got {restorePoints.Count}.");

        var receipt = new E2eReceipt(
            "complete",
            DateTimeOffset.UtcNow,
            subject,
            options,
            new E2ePhaseMeasurements(
                writeElapsed,
                relationshipsWriteElapsed,
                relationshipsVerifyElapsed,
                readbackElapsed,
                pointReadElapsed,
                fullBackupElapsed,
                updatesElapsed,
                deletesElapsed,
                incrementalBackupElapsed,
                fullRestoreElapsed,
                fullVerifyElapsed,
                pitrRestoreElapsed,
                pitrVerifyElapsed,
                finalRestoreElapsed,
                finalVerifyElapsed,
                snapshotPagingElapsed,
                collectionListingElapsed,
                coldRemoteElapsed,
                indexBuildElapsed,
                queryElapsed,
                compactionElapsed,
                compactVerifyElapsed),
            new E2eVolumeMeasurements(
                options.Documents,
                verifiedOriginal,
                full.Artifacts[0].Length,
                full.Artifacts[0].PlaintextSha256,
                incremental.Artifacts[0].Length,
                incremental.Artifacts[0].PlaintextSha256,
                verifiedFullRestore,
                verifiedPitr,
                pitrEntries,
                verifiedFinal,
                verifiedAbsent,
                pointReadStats,
                queryStats,
                compaction.RecordsWritten,
                compaction.TombstonesRetained,
                verifiedCompacted,
                relationshipVerifyPasses,
                queriesVerified,
                verifiedSnapshot,
                verifiedCollections,
                relationshipEndpointsVerified,
                options.Relationships > 0 ? "all users and all relationship endpoints" : "no relationships configured",
                verifiedColdRemote,
                verifiedColdRemoteCollections,
                remoteSegmentObjects,
                coldRemoteCacheBytes,
                restoredLocalSegmentBytesAfterEviction,
                queryDiagnostics),
            rootLossRecovery,
            restoreTargetCleanupMeasurements,
            null,
            null,
            sourceAckDurability);
        if (ActiveResources is { } resources)
            await resources.CompleteAsync().ConfigureAwait(false);
        return (receipt, E2eMarkdown.Create(receipt));
    }

    private static async Task<E2eSourceAckDurability> CaptureSourceAckDurabilityAsync(
        RemoteCommitJournal? sourceJournal,
        IBackupObjectStore? objectStore)
    {
        if (sourceJournal is null || objectStore is null)
        {
            return new E2eSourceAckDurability(
                StrictSourceAckDurability: false,
                Mode: "local-wal-only",
                DatabaseIdentity: null,
                WriterIdentity: null,
                JournalFramePrefix: null,
                RemoteJournalFrontier: null,
                RemoteJournalFrameObjects: null,
                RemoteJournalBytes: null);
        }

        var frontier = await sourceJournal.GetValidatedFrontierAsync().ConfigureAwait(false);
        var frameObjects = 0;
        long frameBytes = 0;
        await foreach (var metadata in objectStore.ListAsync(sourceJournal.FramePrefix).ConfigureAwait(false))
        {
            frameObjects = checked(frameObjects + 1);
            frameBytes = checked(frameBytes + metadata.Length);
        }

        if (frontier == 0 || frameObjects == 0)
        {
            throw new InvalidDataException(
                "Strict RustFS source acknowledgement was requested, but the remote commit journal has no committed frames.");
        }

        return new E2eSourceAckDurability(
            StrictSourceAckDurability: true,
            Mode: "remote-commit-journal",
            DatabaseIdentity: sourceJournal.DatabaseId.ToString(),
            WriterIdentity: sourceJournal.WriterId.ToString(),
            JournalFramePrefix: sourceJournal.FramePrefix,
            RemoteJournalFrontier: frontier,
            RemoteJournalFrameObjects: frameObjects,
            RemoteJournalBytes: frameBytes);
    }

    private static int BatchCount(int documents, int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // Avoid documents + batchSize - 1: both operands are user-supplied
        // Int32 values and that conventional ceiling formula overflows near
        // the supported maximum document count.
        return documents / batchSize + (documents % batchSize == 0 ? 0 : 1);
    }

    private static void ValidateDocumentIdConfiguration(int documents)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documents);

        // Validate the upper generated id at option-parse time. This keeps a
        // 100M run from discovering an id-width mismatch only after hours of
        // writes, while still accepting every non-negative Int32 index.
        var lastIndex = documents - 1;
        var identifier = DocumentId(lastIndex);
        if (identifier.Length != DocumentIdPrefixLength + DocumentIdDigits
            || ParseDocumentIndex(identifier, documents) != lastIndex)
        {
            throw new ArgumentException(
                $"The document id format cannot represent the configured upper index {lastIndex:N0}.");
        }
    }

    /// <summary>
    /// Builds the failure report for a query the planner could not serve:
    /// the planner's own explanation plus every lifecycle record, and the
    /// physical generation state when it can be observed through the
    /// internal backend. Reflection keeps this dead path out of the
    /// production surface.
    /// </summary>
    private static async Task<InvalidDataException> DescribePlanningFailureAsync(
        AutomaticIndexRuntime runtime,
        AutomaticIndexScope scope,
        IndexRuntimeQuery request,
        QueryPlanningException exception)
    {
        var explain = await runtime.ExplainAsync(scope, request).ConfigureAwait(false);
        var snapshot = runtime.Coordinator.Snapshot(scope).Indexes;
        var phases = string.Join(", ", snapshot
            .Select(static record => $"{record.CandidateId}:{record.Phase}:visible={record.Visible}:applied={record.AppliedPosition}"));
        var physical = "unavailable";
        try
        {
            var backendField = typeof(AutomaticIndexRuntime).GetField(
                "backend",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var backend = backendField?.GetValue(runtime);
            var inspect = backend?.GetType().GetMethod("InspectPhysicalAsync");
            if (backend is not null && inspect is not null)
            {
                var states = new List<string>();
                foreach (var record in snapshot)
                {
                    var awaitable = (dynamic)inspect.Invoke(backend, [record.CandidateId, default(CancellationToken)])!;
                    var state = (AutomaticIndexPhysicalState)awaitable.GetAwaiter().GetResult();
                    states.Add($"{record.CandidateId}:exists={state.Exists}:visible={state.Visible}:applied={state.AppliedPosition}");
                }

                physical = string.Join(", ", states);
            }
        }
        catch (Exception diagnosticFailure)
        {
            physical = $"probe failed: {diagnosticFailure.GetType().Name}";
        }

        return new InvalidDataException(
            $"An indexed query failed to plan: {exception.Message} | reason='{explain.Reason}' "
            + $"| candidates={explain.Candidates.Count} rejected={string.Join("; ", explain.RejectedCandidates.Select(static candidate => $"{candidate.IndexName}: {candidate.Reason}"))} "
            + $"| indexes=[{phases}] | physical=[{physical}]", exception);
    }

    /// <summary>
    /// Chooses how many incremental entries the interior PITR cut applies.
    /// The boundary between the update and delete publications is the most
    /// interesting cut (updates present, deletes not yet); with only one
    /// publication phase the midpoint of the first two entries applies, and a
    /// single-entry chain restores that one entry.
    /// </summary>
    private static int ChoosePitrEntryCount(int entryCount, int updateBatches, int deleteBatches)
    {
        if (entryCount == 0)
            throw new InvalidDataException("The incremental chain has no entries.");
        if (updateBatches > 0 && deleteBatches > 0)
            return updateBatches;
        return Math.Min(entryCount, 2);
    }

    private static DateTimeOffset Midpoint(DateTimeOffset first, DateTimeOffset second) =>
        second <= first ? first : first.AddTicks((second - first).Ticks / 2);

    /// <summary>
    /// Returns transient allocations from the previous phase before the next
    /// phase opens its own disk-backed state maps and cache files. The
    /// large-object heap is compacted too: a phase that churns large frame
    /// buffers can leave fragmentation behind, and a later phase's
    /// contiguous working allocations can then fail even with most of the
    /// host's memory free.
    /// </summary>
    private static void ReclaimPhaseMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string DocumentId(int index) =>
        $"doc-{index.ToString($"D{DocumentIdDigits}", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// A stateless splitmix64 draw so every expected body can be regenerated
    /// from (seed, index, salt) without retaining the corpus in memory.
    /// </summary>
    private static ulong Draw(ulong seed, int index, int salt)
    {
        var x = (unchecked((ulong)(uint)salt) * 0x9E3779B97F4A7C15UL)
            ^ (unchecked((ulong)(uint)index) * 0xBF58476D1CE4E5B9UL)
            ^ seed;
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static string RandomString(ulong value, int minLength, int maxLength)
    {
        var length = minLength + (int)(value % (ulong)(maxLength - minLength + 1));
        var characters = new char[length];
        for (var index = 0; index < length; index++)
        {
            var draw = Draw(value, index, 0xABCD);
            characters[index] = Alphabet[(int)(draw % (ulong)Alphabet.Length)];
        }

        return new string(characters);
    }

    /// <summary>
    /// Builds the deterministic random body for one document. Mixed scalar,
    /// string, unicode, nested-object, array, and null fields exercise JSON
    /// fidelity; the output is a pure function of (seed, index, updated).
    /// Field sizes are intentionally lean: the durable engine writes bodies
    /// through its WAL and disk-backed state maps, so larger bodies increase
    /// physical storage, object-tier traffic, and verification work. Expected
    /// bodies are regenerated statelessly and never retained as a corpus.
    /// </summary>
    private static JsonElement CreateRandomBody(int seed, int index, bool updated)
    {
        var salt = updated ? 0x5EED : 0;
        var draw = Draw((ulong)seed, index, salt);
        var nested = new
        {
            alpha = (int)(draw % 100_000),
            beta = (draw >> 11) * 0.000_001,
        };
        var tagCount = 1 + (int)((draw >> 23) % 3);
        var tags = new string[tagCount];
        for (var tag = 0; tag < tagCount; tag++)
        {
            tags[tag] = RandomString(Draw((ulong)seed, index, salt + 0x100 + tag), 2, 5);
        }

        return JsonSerializer.SerializeToElement(new
        {
            name = RandomString(draw, 4, 10),
            sequence = index,
            phase = updated ? "updated" : "initial",
            score = (draw >> 29) * 0.000_001,
            active = (draw & 1) == 0,
            note = (draw & 2) == 0 ? null : RandomString(draw >> 3, 3, 6),
            unicode = "μÑ-" + RandomString(draw >> 5, 2, 4),
            tags,
            nested,
            payload = RandomString(Draw((ulong)seed, index, salt + 0x200), 8, 24),
        });
    }

    /// <summary>The typed shape behind the relationship schema registration.</summary>
    private sealed record UserDocument
    {
        public string Name { get; set; } = "";
    }

    /// <summary>The typed shape behind the relationship schema registration.</summary>
    private sealed record OrderDocument
    {
        public string UserId { get; set; } = "";
    }

    /// <summary>Builds the users/orders schema registry backing the relationship phase.</summary>
    private static SchemaRegistry BuildRelationshipSchemas(TenantId tenant, CollectionId users, CollectionId orders)
    {
        var schemas = new SchemaRegistry();
        schemas.Register(tenant, users, new SchemaMetadata(
            "users",
            1,
            typeof(UserDocument),
            [new FieldMetadata("name", typeof(string), new SchemaValueDescriptor(SchemaValueType.Text, nullable: true, canonicalEntityId: false))]));
        schemas.Register(tenant, orders, new SchemaMetadata(
            "orders",
            1,
            typeof(OrderDocument),
            [new FieldMetadata("userId", typeof(string), new SchemaValueDescriptor(SchemaValueType.Text, nullable: true, canonicalEntityId: false))],
            [new RelationshipMetadata(
                "customer",
                "orders",
                "users",
                RelationshipKind.ManyToOne,
                DeletePolicy.Restrict,
                foreignKey: "userId")]));
        return schemas;
    }

    /// <summary>
    /// Writes the relationship corpus: one user document per user and one
    /// order per relationship, each order carrying a foreign key resolved
    /// statelessly (order i belongs to user i mod Users), so every related
    /// set is derivable from the seed-free configuration alone.
    /// </summary>
    private static async Task WriteRelationshipsAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId users,
        CollectionId orders,
        E2eOptions options)
    {
        var userCount = Math.Max(1, options.Relationships / 4);
        var userMutations = new StorageMutation[userCount];
        for (var index = 0; index < userCount; index++)
        {
            using var body = JsonDocument.Parse($"{{\"name\":\"user-{index}\"}}");
            userMutations[index] = StorageMutation.Upsert(
                new DocumentKey(tenant, users, $"user-{index}"),
                body.RootElement.Clone(),
                DocumentPrecondition.Missing);
        }

        var userResult = await engine.ApplyBatchAsync(userMutations).ConfigureAwait(false);
        if (userResult.Outcome != StorageBatchOutcome.Committed)
            throw new InvalidDataException($"User batch was not committed: {userResult.Outcome}.");

        var orderBatch = new List<StorageMutation>(capacity: 4_096);
        for (var index = 0; index < options.Relationships; index++)
        {
            using var body = JsonDocument.Parse($"{{\"userId\":\"user-{index % userCount}\"}}");
            orderBatch.Add(StorageMutation.Upsert(
                new DocumentKey(tenant, orders, $"order-{index:D8}"),
                body.RootElement.Clone(),
                DocumentPrecondition.Missing));
            if (orderBatch.Count == 4_096)
            {
                var batchResult = await engine.ApplyBatchAsync(orderBatch).ConfigureAwait(false);
                if (batchResult.Outcome != StorageBatchOutcome.Committed)
                    throw new InvalidDataException($"Order batch was not committed: {batchResult.Outcome}.");
                orderBatch.Clear();
            }
        }

        if (orderBatch.Count > 0)
        {
            var tailResult = await engine.ApplyBatchAsync(orderBatch).ConfigureAwait(false);
            if (tailResult.Outcome != StorageBatchOutcome.Committed)
                throw new InvalidDataException($"Order tail batch was not committed: {tailResult.Outcome}.");
        }
    }

    /// <summary>
    /// Verifies the relationship corpus and its derived edges: exact bodies
    /// for every user and order, exact related sets through the relationship
    /// read surface, a bounded traversal, and the restrict delete policy.
    /// </summary>
    private static async Task<int> VerifyRelationshipsAsync(
        TenantId tenant,
        CollectionId users,
        CollectionId orders,
        DurableStorageEngine engine,
        E2eOptions options)
    {
        var userCount = Math.Max(1, options.Relationships / 4);
        var ordersPerUser = options.Relationships / userCount;

        // Exact bodies.
        for (var index = 0; index < userCount; index++)
        {
            var user = await engine.ReadAsync(new DocumentKey(tenant, users, $"user-{index}")).ConfigureAwait(false);
            if (user is null || user.Version != 1 || user.Body.GetProperty("name").GetString() != $"user-{index}")
                throw new InvalidDataException($"user-{index} does not match after restore.");
        }

        for (var index = 0; index < options.Relationships; index++)
        {
            var order = await engine.ReadAsync(new DocumentKey(tenant, orders, $"order-{index:D8}")).ConfigureAwait(false);
            if (order is null || order.Version != 1 || order.Body.GetProperty("userId").GetString() != $"user-{index % userCount}")
                throw new InvalidDataException($"order-{index:D8} does not match after restore.");
        }

        // Derived edges through the engine's relationship reads: every user's
        // related set is exactly its four orders, in insert order. A restored
        // engine carries the documents but not the derived constraint
        // records, so the schema-driven rebuild pass runs first — the same
        // maintenance the relationship workload performs post-restore.
        var schemas = BuildRelationshipSchemas(tenant, users, orders);
        await engine.RebuildConstraintRecordsAsync(tenant, schemas).ConfigureAwait(false);
        // Not disposed: the store borrows the caller's engine as its storage
        // provider and must not take it down with the verification scope.
        var store = new DocumentStore(engine, new TerraBase.MetricsRegistry(), schemas);
        // The qualification must cover every derived relationship endpoint,
        // including the default 10k relationship fixture. The exact body
        // loops above already cover every user and order; this loop now also
        // exercises every user's related-set and bounded traversal surface.
        var sampleCount = userCount;
        var listedEndpoints = 0;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var userIndex = sample;
            var related = await store.ListRelatedAsync(
                tenant,
                new DocumentKey(tenant, users, $"user-{userIndex}"),
                "customer",
                RelationshipDirection.Reverse,
                limit: 100).ConfigureAwait(false);
            // Orders are assigned round-robin (order i belongs to user
            // i mod Users), so user u owns every order index congruent to u.
            var expectedIds = new HashSet<string>(StringComparer.Ordinal);
            for (var orderIndex = userIndex; orderIndex < options.Relationships; orderIndex += userCount)
            {
                expectedIds.Add($"order-{orderIndex:D8}");
            }

            if (expectedIds.Count != ordersPerUser)
                throw new InvalidDataException(
                    $"user-{userIndex} expected {ordersPerUser} orders, derived {expectedIds.Count}.");

            // Relationship reads are bounded by contract. Exercise a small
            // page first, then the complete bounded set, and verify every
            // returned endpoint belongs to the expected relation. There is no
            // continuation cursor on this API, so callers must choose a
            // limit that is sufficient for the relation they need.
            var pageLimit = Math.Min(2, expectedIds.Count);
            var relatedPage = await store.ListRelatedAsync(
                tenant,
                new DocumentKey(tenant, users, $"user-{userIndex}"),
                "customer",
                RelationshipDirection.Reverse,
                limit: pageLimit).ConfigureAwait(false);
            if (relatedPage.Count != pageLimit
                || relatedPage.Any(endpoint => !expectedIds.Contains(endpoint.Endpoint.DocumentId)))
            {
                throw new InvalidDataException(
                    $"user-{userIndex} bounded related page returned {relatedPage.Count} endpoints, expected {pageLimit}.");
            }

            if (related.Count != expectedIds.Count
                || related.Any(endpoint => !expectedIds.Contains(endpoint.Endpoint.DocumentId)))
            {
                throw new InvalidDataException(
                    $"user-{userIndex} related set is {{ {string.Join(", ", related.Select(endpoint => endpoint.Endpoint.DocumentId).Order())} }}, expected {{ {string.Join(", ", expectedIds.Order())} }}.");
            }
            listedEndpoints = checked(listedEndpoints + related.Count);

            var traversal = await store.TraverseRelationshipsAsync(
                tenant,
                new DocumentKey(tenant, users, $"user-{userIndex}"),
                "customer",
                RelationshipDirection.Reverse,
                bounds: new RelationshipTraversalBounds(MaximumHops: 2, MaximumResults: 100, MaximumBytes: 64 * 1024, MaximumWork: 200)).ConfigureAwait(false);
            if (traversal.Endpoints.Count != expectedIds.Count)
                throw new InvalidDataException(
                    $"user-{userIndex} traversal returned {traversal.Endpoints.Count} endpoints, expected {expectedIds.Count}.");
        }

        // Restrict delete policy: deleting a user with orders must conflict
        // and leave the user readable.
        var restricted = await store.DeleteAsync(new DocumentKey(tenant, users, "user-0")).ConfigureAwait(false);
        if (restricted.Outcome != WriteOutcome.Conflict)
            throw new InvalidDataException($"Restrict delete must conflict, got {restricted.Outcome}.");
        var survivor = await engine.ReadAsync(new DocumentKey(tenant, users, "user-0")).ConfigureAwait(false);
            if (survivor is null)
            throw new InvalidDataException("user-0 disappeared after a rejected restrict delete.");

        return listedEndpoints;
    }

    /// <summary>
    /// The statelessly derivable expectation for one document index: updates
    /// apply ascending from zero and deletes apply ascending from the head of
    /// the tail range (publication order), so at any restore cut the deleted
    /// set is [FirstDeletedIndex, FirstDeletedIndex + DeletedDocuments) and
    /// the updated set is [0, UpdatedDocuments). Everything else is the
    /// original version 1 body.
    /// </summary>
    private readonly record struct ExpectedState(
        int Documents,
        int Seed,
        int UpdatedDocuments,
        int FirstDeletedIndex,
        int DeletedDocuments)
    {
        public static ExpectedState Initial(int documents, int seed) =>
            new(documents, seed, 0, documents, 0);

        public int LiveDocuments => Documents - DeletedDocuments;

        public bool IsDeleted(int index) =>
            index >= FirstDeletedIndex && index < FirstDeletedIndex + DeletedDocuments;

        public bool IsUpdated(int index) => index < UpdatedDocuments;
    }

    private static void VerifyDocument(ExpectedState state, int index, Document? document)
    {
        if (state.IsDeleted(index))
            throw new InvalidDataException($"{DocumentId(index)} was returned but belongs to the deleted range.");
        if (document is null)
            throw new InvalidDataException($"Document {DocumentId(index)} is missing.");
        var expectedVersion = state.IsUpdated(index) ? 2UL : 1UL;
        if (document.Version != expectedVersion)
            throw new InvalidDataException(
                $"{DocumentId(index)} version {document.Version} does not match {expectedVersion}.");
        var expected = CreateRandomBody(state.Seed, index, updated: state.IsUpdated(index));
        if (!JsonElement.DeepEquals(document.Body, expected))
            throw new InvalidDataException($"{DocumentId(index)} body does not match the expected value.");
    }

    /// <summary>
    /// Scans one engine collection and verifies every document's body and
    /// version exactly match the statelessly regenerated expectation, with no
    /// duplicates, no missing identifiers, and no survivors from the deleted
    /// range.
    /// </summary>
    private static void Phase(string name)
    {
        ActiveResources?.ThrowIfExceeded();
        ActiveResources?.SetPhase(name);
        Console.WriteLine($"phase: {name} [{DateTimeOffset.Now:HH:mm:ss}]");
    }

    private static Task RecordProgressAsync(string phase, long completed, long total) =>
        ActiveResources?.RecordProgressAsync(phase, completed, total) ?? Task.CompletedTask;

    private static async Task<(long Verified, TimeSpan Elapsed)> VerifyScanAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId collection,
        ExpectedState state)
    {
        ActiveResources?.SetPhase("exact-scan", 0, state.LiveDocuments);
        var started = Stopwatch.GetTimestamp();
        long verified = 0;
        await foreach (var document in engine.ScanAsync(tenant, collection).ConfigureAwait(false))
        {
            var index = ParseDocumentIndex(document.Key.DocumentId, state.Documents);
            VerifyDocument(state, index, document);
            // StorageEngine.ScanAsync reads the collection's ordered state
            // index. The expected state can contain one deleted range in the
            // middle of the key space for an interior PITR cut, so derive the
            // next live id directly instead of retaining one marker per
            // corpus document in a HashSet.
            var expectedIndex = verified < state.FirstDeletedIndex
                ? checked((int)verified)
                : checked(state.FirstDeletedIndex + state.DeletedDocuments + (int)(verified - state.FirstDeletedIndex));
            if (index != expectedIndex)
                throw new InvalidDataException(
                    $"Scan position {verified} returned {DocumentId(index)}, expected {DocumentId(expectedIndex)}.");
            verified++;
            if ((verified & 0x3FFF) == 0)
                ActiveResources?.ThrowIfExceeded();
            if (ActiveResources is { } resources && resources.ShouldRecord(verified, state.LiveDocuments))
                await resources.RecordProgressAsync("exact-scan", verified, state.LiveDocuments).ConfigureAwait(false);
        }

        if (verified != state.LiveDocuments)
            throw new InvalidDataException(
                $"Scan returned {verified} documents, expected {state.LiveDocuments}.");

        return (verified, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Exercises the bounded document and collection listing capabilities on
    /// one immutable snapshot. The cursor is always the last returned key, so
    /// this verifies both page boundaries and the absence of duplicate or
    /// skipped rows without retaining the corpus in memory.
    /// </summary>
    private static async Task<(long Documents, int Collections, TimeSpan DocumentElapsed, TimeSpan CollectionElapsed)>
        VerifySnapshotPagingAsync(
            DurableStorageEngine engine,
            TenantId tenant,
            CollectionId collection,
            E2eOptions options,
            ExpectedState state)
    {
        ActiveResources?.SetPhase("snapshot-document-pages", 0, state.LiveDocuments);
        await using var snapshot = await engine.BeginSnapshotAsync().ConfigureAwait(false);

        const int documentPageSize = 997;
        var documentStarted = Stopwatch.GetTimestamp();
        long verified = 0;
        string? afterDocument = null;
        while (true)
        {
            var page = new List<Document>(documentPageSize);
            await foreach (var document in engine.ScanSnapshotAsync(
                               snapshot,
                               tenant,
                               collection,
                               afterDocument,
                               documentPageSize).ConfigureAwait(false))
            {
                page.Add(document);
            }

            if (page.Count == 0)
                break;

            foreach (var document in page)
            {
                var index = ParseDocumentIndex(document.Key.DocumentId, state.Documents);
                VerifyDocument(state, index, document);
                var expectedIndex = verified < state.FirstDeletedIndex
                    ? checked((int)verified)
                    : checked(state.FirstDeletedIndex + state.DeletedDocuments + (int)(verified - state.FirstDeletedIndex));
                if (index != expectedIndex)
                {
                    throw new InvalidDataException(
                        $"Snapshot list position {verified} returned {DocumentId(index)}, expected {DocumentId(expectedIndex)}.");
                }

                verified++;
                if (ActiveResources is { } resources && resources.ShouldRecord(verified, state.LiveDocuments))
                    await resources.RecordProgressAsync("snapshot-document-pages", verified, state.LiveDocuments).ConfigureAwait(false);
            }

            var nextAfter = page[^1].Key.DocumentId;
            if (string.Equals(afterDocument, nextAfter, StringComparison.Ordinal))
                throw new InvalidDataException("Snapshot document listing cursor did not advance.");
            afterDocument = nextAfter;
        }

        if (verified != state.LiveDocuments)
        {
            throw new InvalidDataException(
                $"Snapshot document listing returned {verified} documents, expected {state.LiveDocuments}.");
        }

        var documentElapsed = Stopwatch.GetElapsedTime(documentStarted);
        var collectionStarted = Stopwatch.GetTimestamp();
        var listedCollections = new List<string>();
        string? afterCollection = null;
        while (true)
        {
            var page = await engine.ListSnapshotCollectionsAsync(
                snapshot,
                tenant,
                afterCollection,
                maximumCollections: 2).ConfigureAwait(false);
            if (page.Length == 0)
                break;

            foreach (var listed in page)
            {
                if (listedCollections.Count > 0
                    && string.CompareOrdinal(listedCollections[^1], listed.Value) >= 0)
                {
                    throw new InvalidDataException("Snapshot collection listing is not strictly ordered.");
                }

                listedCollections.Add(listed.Value);
            }

            var nextAfter = page[^1].Value;
            if (string.Equals(afterCollection, nextAfter, StringComparison.Ordinal))
                throw new InvalidDataException("Snapshot collection listing cursor did not advance.");
            afterCollection = nextAfter;
        }

        var expectedCollections = options.Relationships > 0
            ? new[] { collection.Value, "orders", "users" }
            : new[] { collection.Value };
        if (!listedCollections.SequenceEqual(expectedCollections, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Snapshot collection listing returned [{string.Join(", ", listedCollections)}], expected [{string.Join(", ", expectedCollections)}].");
        }

        return (verified, listedCollections.Count, documentElapsed, Stopwatch.GetElapsedTime(collectionStarted));
    }

    /// <summary>
    /// Qualifies recovery after the benchmark-owned source root is lost. The
    /// source is removed only after its full and incremental manifests have
    /// been observed in the configured object store. The restore target is a
    /// fresh root; its segments are then uploaded under a separate remote
    /// prefix, evicted locally, and reopened through the S3 segment tier.
    /// </summary>
    private static async Task<E2eRootLossRecoveryMeasurements> RunRootLossRecoveryAsync(
        string sourcePath,
        string outputDirectory,
        string targetRoot,
        BackupManager manager,
        BackupManifest full,
        BackupManifest incremental,
        string sourceOwnershipMarkerContents,
        int expectedLogEntries,
        IBackupObjectStore objectStore,
        RemoteCommitJournal sourceJournal,
        Func<DurableStorageEngine> openSource,
        TenantId tenant,
        CollectionId collection,
        E2eOptions options,
        RestoreTargetCleanupLedger restoreTargetCleanup)
    {
        var checkpointPath = Path.Combine(outputDirectory, "root-loss-recovery-checkpoint.json");
        var phase = "starting";
        var checkpoint = new RootLossRecoveryCheckpoint(
            "diagnostic-only",
            "in-progress",
            phase,
            DateTimeOffset.UtcNow,
            SourceRootDeleted: false,
            ArtifactsExpected: 0,
            ArtifactsVerified: 0,
            ArtifactBytesVerified: 0,
            AppliedLogEntries: 0,
            null);

        try
        {
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);

            phase = "committed-manifest-check";
            checkpoint = checkpoint with { Phase = phase };
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);
            var committed = await manager.ListRestorePointsAsync("e2e-volume").ConfigureAwait(false);
            var durableFull = FindDurableManifest(committed, full, "full");
            var durableIncremental = FindDurableManifest(committed, incremental, "incremental");
            if (durableFull.Kind != BackupKind.Full
                || durableIncremental.Kind != BackupKind.Incremental
                || !string.Equals(durableIncremental.BaseBackupId, durableFull.BackupId, StringComparison.Ordinal)
                || !string.Equals(durableIncremental.BaseManifestSha256, durableFull.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The durable full and incremental manifests do not form the expected recovery chain.");
            }

            phase = "artifact-preflight";
            checkpoint = checkpoint with { Phase = phase };
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);
            var preflight = await PreflightCommittedArtifactsAsync(
                objectStore,
                full,
                incremental).ConfigureAwait(false);
            checkpoint = checkpoint with
            {
                Phase = "artifacts-verified",
                Status = "ready-to-delete",
                ArtifactsExpected = preflight.Artifacts,
                ArtifactsVerified = preflight.Artifacts,
                ArtifactBytesVerified = preflight.Bytes,
            };
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);

            phase = "deleting-source-root";
            checkpoint = checkpoint with { Phase = phase };
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);
            DeleteOwnedSourceRoot(sourcePath, outputDirectory, sourceOwnershipMarkerContents);
            checkpoint = checkpoint with
            {
                Phase = "source-deleted",
                Status = "in-progress",
                SourceRootDeleted = true,
            };
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);

            // Reopen the deleted source through the same stable remote journal
            // namespace used by every source instance. This proves that the
            // source acknowledgement barrier itself can rebuild the source
            // WAL before the ordinary backup restore is exercised below.
            phase = "journal-source-recovery";
            checkpoint = checkpoint with { Phase = phase };
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);
            Phase("root-loss-journal-source-recovery");
            var journalRecoveryStarted = Stopwatch.GetTimestamp();
            // The tiered segment store retains acknowledged segments in the
            // remote object store after the local root is deleted. Bootstrap
            // the source WAL and manifest from the same stable journal before
            // opening the engine; StorageEngine deliberately refuses to infer
            // authority from remote segment objects alone.
            RemoteCommitJournalRecovery.RebuildWalFromRemote(
                sourceJournal,
                sourcePath,
                batchSize: 4096,
                allowedEmptyDirectories: new HashSet<string>(StringComparer.Ordinal)
                {
                    "ssd-cache",
                });
            long journalSourceDocumentsVerified;
            await using (var recoveredSource = openSource())
            {
                (journalSourceDocumentsVerified, _) = await VerifyScanAsync(
                    recoveredSource,
                    tenant,
                    collection,
                    new ExpectedState(
                        options.Documents,
                        options.Seed,
                        options.Updates,
                        options.Documents - options.Deletes,
                        options.Deletes)).ConfigureAwait(false);
            }

            if (journalSourceDocumentsVerified != options.Documents - options.Deletes)
            {
                throw new InvalidDataException(
                    $"Remote journal source recovery verified {journalSourceDocumentsVerified} documents, expected {options.Documents - options.Deletes}.");
            }

            var journalSourceRecoveryElapsed = Stopwatch.GetElapsedTime(journalRecoveryStarted);
            var journalFrontier = await sourceJournal.GetValidatedFrontierAsync().ConfigureAwait(false);
            if (journalFrontier == 0)
            {
                throw new InvalidDataException("Remote journal source recovery completed with an empty journal frontier.");
            }

            // Keep the final source-loss assertion meaningful after the
            // journal-only reopen. The recovered source is benchmark-owned,
            // so mark it before deleting it a second time.
            await File.WriteAllTextAsync(
                Path.Combine(sourcePath, SourceOwnershipMarkerName),
                sourceOwnershipMarkerContents).ConfigureAwait(false);
            DeleteOwnedSourceRoot(sourcePath, outputDirectory, sourceOwnershipMarkerContents);
            if (Directory.Exists(sourcePath))
                throw new IOException("The source root still exists after journal recovery verification.");

            var recoveredRoot = Path.Combine(targetRoot, "root-loss-recovered");
            if (Directory.Exists(recoveredRoot) || File.Exists(recoveredRoot))
                throw new InvalidDataException("Root-loss recovery target must be a fresh benchmark-owned root.");
            var target = new StorageEngineRestoreTarget(recoveredRoot);
            phase = "restoring";
            checkpoint = checkpoint with { Phase = phase };
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);
            Phase("root-loss-restore");
            var restoreStarted = Stopwatch.GetTimestamp();
            var restore = await manager.RestoreAsync(durableIncremental, target).ConfigureAwait(false);
            var restoreElapsed = Stopwatch.GetElapsedTime(restoreStarted);
            if (!string.Equals(restore.RestorePoint.BackupId, durableIncremental.BackupId, StringComparison.Ordinal)
                || !string.Equals(restore.RestorePoint.SourceId, durableIncremental.SourceId, StringComparison.Ordinal)
                || restore.RestorePoint.Kind != durableIncremental.Kind
                || restore.RestorePoint.Boundary.Sequence != durableIncremental.Boundary.Sequence
                || restore.RestorePoint.Boundary.Timestamp != durableIncremental.Boundary.Timestamp
                || !string.Equals(restore.RestorePoint.BaseBackupId, durableIncremental.BaseBackupId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Root-loss restore selected '{restore.RestorePoint.BackupId}' instead of durable incremental '{durableIncremental.BackupId}' or returned a different chain identity.");
            }
            if (restore.AppliedLogEntries != expectedLogEntries)
            {
                throw new InvalidDataException(
                    $"Root-loss restore applied {restore.AppliedLogEntries} log entries, expected {expectedLogEntries}.");
            }
            checkpoint = checkpoint with
            {
                Phase = "restored",
                AppliedLogEntries = restore.AppliedLogEntries,
            };
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);

            phase = "cold-remote-reopen";
            checkpoint = checkpoint with { Phase = phase };
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);
            Phase("root-loss-cold-remote-reopen");
            var cold = await VerifyColdRemoteRestoreAsync(
                target.ResolveActivePath(),
                objectStore,
                tenant,
                collection,
                options,
                objectPrefix: "root-loss-recovered").ConfigureAwait(false);

            checkpoint = checkpoint with
            {
                Status = "complete",
                Phase = "verified",
            };
            await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);

            // The root-loss receipt and checkpoint retain the recovery proof;
            // the verified recovery target is no longer needed once the cold
            // remote reopen has completed. Reclaim it before the final
            // restore materializes its target.
            restoreTargetCleanup.Delete(recoveredRoot, "root-loss-recovered");

            return new E2eRootLossRecoveryMeasurements(
                SourceRootDeleted: !Directory.Exists(sourcePath),
                AppliedLogEntries: restore.AppliedLogEntries,
                DocumentsVerified: cold.Verified,
                DeletedDocumentsVerifiedAbsent: options.Deletes,
                CollectionsVerified: cold.Collections,
                RemoteSegmentObjects: cold.RemoteObjects,
                ColdRemoteCacheBytes: cold.CacheBytes,
                RestoredLocalSegmentBytesAfterEviction: cold.LocalSegmentBytesAfterEviction,
                RestoreElapsed: restoreElapsed,
                ColdRemoteElapsed: cold.Elapsed,
                ArtifactsVerified: preflight.Artifacts,
                ArtifactBytesVerified: preflight.Bytes,
                JournalSourceRecoveryVerified: true,
                JournalSourceDocumentsVerified: journalSourceDocumentsVerified,
                JournalSourceRecoveryElapsed: journalSourceRecoveryElapsed,
                JournalSourceFrontier: journalFrontier,
                CheckpointPath: Path.GetRelativePath(outputDirectory, checkpointPath));
        }
        catch (Exception exception)
        {
            checkpoint = checkpoint with
            {
                Status = "failed",
                Phase = phase,
                Failure = exception.ToString(),
            };
            try
            {
                await WriteRootLossCheckpointAsync(checkpointPath, checkpoint).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original failure when the failure receipt
                // itself cannot be persisted.
            }

            throw;
        }
    }

    private static async Task<(int Artifacts, long Bytes)> PreflightCommittedArtifactsAsync(
        IBackupObjectStore objectStore,
        BackupManifest full,
        BackupManifest incremental)
    {
        var artifacts = full.Artifacts.Concat(incremental.Artifacts).ToArray();
        var expected = new Dictionary<string, BackupArtifact>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (expected.TryGetValue(artifact.Key, out var existing))
            {
                if (existing.Length != artifact.Length
                    || !string.Equals(existing.Sha256, artifact.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The backup chain references conflicting artifact metadata for '{artifact.Key}'.");
                }

                continue;
            }

            expected.Add(artifact.Key, artifact);
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var metadata in objectStore.ListAsync($"backups/{full.SourceId}/").ConfigureAwait(false))
        {
            if (!expected.TryGetValue(metadata.Key, out var artifact))
                continue;
            if (!listed.Add(metadata.Key))
                throw new InvalidDataException($"The object store listed backup artifact '{metadata.Key}' more than once.");
            ValidateArtifactMetadata(metadata, artifact);
        }

        if (listed.Count != expected.Count)
        {
            var missing = expected.Keys.Where(key => !listed.Contains(key));
            throw new FileNotFoundException(
                $"The committed backup artifact listing is missing: {string.Join(", ", missing)}.");
        }

        long totalBytes = 0;
        foreach (var artifact in expected.Values)
        {
            var metadata = await objectStore.HeadAsync(artifact.Key).ConfigureAwait(false)
                ?? throw new FileNotFoundException("A committed backup artifact is missing.", artifact.Key);
            ValidateArtifactMetadata(metadata, artifact);

            await using var stream = await objectStore.GetAsync(
                artifact.Key,
                artifact.Length).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long length = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                    break;
                length = checked(length + read);
                if (length > artifact.Length)
                    throw new InvalidDataException($"Backup artifact '{artifact.Key}' exceeded its manifest length.");
                hash.AppendData(buffer, 0, read);
            }

            if (length != artifact.Length)
                throw new InvalidDataException($"Backup artifact '{artifact.Key}' length {length} does not match manifest length {artifact.Length}.");
            var actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actualSha256, artifact.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Backup artifact '{artifact.Key}' checksum does not match its manifest.");
            totalBytes = checked(totalBytes + length);
        }

        return (expected.Count, totalBytes);
    }

    private static BackupManifest FindDurableManifest(
        IReadOnlyList<BackupManifest> committed,
        BackupManifest expected,
        string label)
    {
        var matches = committed
            .Where(manifest => string.Equals(manifest.BackupId, expected.BackupId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Root-loss recovery requires exactly one durable {label} manifest for '{expected.BackupId}', found {matches.Length}.");
        }

        var durable = matches[0];
        if (!string.Equals(durable.Sha256, expected.Sha256, StringComparison.Ordinal)
            || !string.Equals(durable.Signature, expected.Signature, StringComparison.Ordinal)
            || durable.Boundary.Sequence != expected.Boundary.Sequence
            || durable.Boundary.Timestamp != expected.Boundary.Timestamp)
        {
            throw new InvalidDataException(
                $"The durable {label} manifest '{expected.BackupId}' does not match the manifest returned by backup creation.");
        }

        return durable;
    }

    private static void ValidateArtifactMetadata(BackupObjectMetadata metadata, BackupArtifact artifact)
    {
        if (!string.Equals(metadata.Key, artifact.Key, StringComparison.Ordinal)
            || metadata.Length != artifact.Length)
        {
            throw new InvalidDataException($"Backup artifact metadata does not match the manifest for '{artifact.Key}'.");
        }

        var checksumUnavailable = string.Equals(metadata.Sha256, "unknown", StringComparison.OrdinalIgnoreCase)
            || (metadata.Sha256.Length == 64 && metadata.Sha256.All(static character => character == '0'));
        if (!checksumUnavailable
            && !string.Equals(metadata.Sha256, artifact.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Backup artifact catalog checksum does not match the manifest for '{artifact.Key}'.");
        }
    }

    private static async Task WriteRootLossCheckpointAsync(
        string checkpointPath,
        RootLossRecoveryCheckpoint checkpoint)
    {
        checkpoint = checkpoint with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, JsonOptions);
        var temporaryPath = checkpointPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, checkpointPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Deletes exactly the source directory created by this workload. The
    /// exact path, non-reparse ownership boundary, and marker are checked
    /// before recursive deletion so an opt-in qualification cannot traverse a
    /// caller-selected or symlinked directory outside its output root.
    /// </summary>
    private static void DeleteOwnedSourceRoot(
        string sourcePath,
        string outputDirectory,
        string sourceOwnershipMarkerContents)
    {
        var outputRoot = Path.GetFullPath(outputDirectory);
        var expectedSourcePath = Path.Combine(outputRoot, "source");
        var actualSourcePath = Path.GetFullPath(sourcePath);
        if (!string.Equals(actualSourcePath, expectedSourcePath, StringComparison.Ordinal))
            throw new InvalidDataException("Root-loss recovery refused to delete a source path outside the benchmark output root.");
        if (!Directory.Exists(outputRoot) || !Directory.Exists(actualSourcePath))
            throw new InvalidDataException("Root-loss recovery source ownership boundary is missing.");
        if (HasReparsePointInPath(outputRoot) || HasReparsePointInPath(actualSourcePath))
            throw new InvalidDataException("Root-loss recovery refuses to delete a source root below a reparse point.");
        if (HasReparsePointInTree(actualSourcePath))
            throw new InvalidDataException("Root-loss recovery refuses to delete a source tree containing a reparse point.");

        var markerPath = Path.Combine(actualSourcePath, SourceOwnershipMarkerName);
        if (!File.Exists(markerPath)
            || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0
            || !string.Equals(File.ReadAllText(markerPath), sourceOwnershipMarkerContents, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Root-loss recovery source ownership marker is missing or invalid.");
        }

        var quarantinePath = Path.Combine(outputRoot, $"source-quarantine-{Guid.NewGuid():N}");
        if (Directory.Exists(quarantinePath) || File.Exists(quarantinePath))
            throw new IOException("Root-loss recovery generated a conflicting source quarantine path.");

        // Rename within the same output directory first. This makes the
        // ownership decision apply to one directory entry, then the moved
        // path is revalidated before recursive deletion.
        Directory.Move(actualSourcePath, quarantinePath);
        if (Directory.Exists(actualSourcePath) || !Directory.Exists(quarantinePath))
            throw new IOException("Root-loss recovery source quarantine move did not complete atomically.");

        var quarantineMarkerPath = Path.Combine(quarantinePath, SourceOwnershipMarkerName);
        if (HasReparsePointInPath(quarantinePath)
            || HasReparsePointInTree(quarantinePath)
            || !File.Exists(quarantineMarkerPath)
            || (File.GetAttributes(quarantineMarkerPath) & FileAttributes.ReparsePoint) != 0
            || !string.Equals(File.ReadAllText(quarantineMarkerPath), sourceOwnershipMarkerContents, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Root-loss recovery refused to delete the quarantined source because its ownership marker or reparse state changed.");
        }

        Directory.Delete(quarantinePath, recursive: true);
        if (Directory.Exists(actualSourcePath) || Directory.Exists(quarantinePath))
            throw new IOException("Root-loss recovery source root still exists after quarantine deletion.");
    }

    /// <summary>
    /// Removes one restore root created by this workload after its exact
    /// verification has completed. The path is accepted only when it is the
    /// expected named child of the benchmark target root and neither the
    /// target root nor its contents contain a reparse point. This keeps the
    /// cleanup bounded to benchmark-owned data while recording the measured
    /// proof before deleting the verified root.
    /// </summary>
    private static long DeleteVerifiedRestoreTarget(
        string targetPath,
        string targetRoot,
        string expectedName)
    {
        var targetRootFullPath = Path.GetFullPath(targetRoot);
        var expectedTargetPath = Path.GetFullPath(Path.Combine(targetRootFullPath, expectedName));
        var actualTargetPath = Path.GetFullPath(targetPath);
        if (!string.Equals(actualTargetPath, expectedTargetPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Restore target cleanup refused a path outside the expected '{expectedName}' target.");
        }

        if (!Directory.Exists(targetRootFullPath)
            || !Directory.Exists(actualTargetPath)
            || HasReparsePointInPath(targetRootFullPath)
            || HasReparsePointInTree(actualTargetPath))
        {
            throw new InvalidDataException(
                $"Restore target cleanup refused '{actualTargetPath}' because its ownership boundary is missing or contains a reparse point.");
        }

        var physicalBytes = E2eResourceTracker.MeasurePhysicalBytes(actualTargetPath);
        Directory.Delete(actualTargetPath, recursive: true);
        if (Directory.Exists(actualTargetPath) || File.Exists(actualTargetPath))
        {
            throw new IOException($"Verified restore target '{actualTargetPath}' still exists after cleanup.");
        }

        return physicalBytes;
    }

    private sealed class RestoreTargetCleanupLedger
    {
        private readonly string targetRoot;
        private readonly List<E2eRestoreTargetCleanupEntry> deletedTargets = [];

        internal RestoreTargetCleanupLedger(string targetRoot)
        {
            this.targetRoot = Path.GetFullPath(targetRoot);
        }

        internal void Delete(string targetPath, string expectedName)
        {
            ActiveResources?.ThrowIfExceeded();
            var physicalBytes = DeleteVerifiedRestoreTarget(targetPath, targetRoot, expectedName);
            deletedTargets.Add(new(
                expectedName,
                Path.GetFullPath(targetPath),
                physicalBytes));
            ActiveResources?.ThrowIfExceeded();
        }

        internal E2eRestoreTargetCleanupMeasurements Complete()
        {
            if (!Directory.Exists(targetRoot)
                || HasReparsePointInPath(targetRoot)
                || HasReparsePointInTree(targetRoot))
            {
                throw new InvalidDataException(
                    $"Restore target cleanup evidence refused '{targetRoot}' because its ownership boundary is missing or contains a reparse point.");
            }

            var remainingEntries = Directory
                .EnumerateFileSystemEntries(targetRoot)
                .Select(static path => Path.GetFileName(path) ?? string.Empty)
                .Where(static name => name.Length > 0)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var finalTargetPath = Path.Combine(targetRoot, "final");
            var finalTargetDeleted = deletedTargets.Any(static target => target.Name == "final")
                && !Directory.Exists(finalTargetPath)
                && !File.Exists(finalTargetPath);
            if (!finalTargetDeleted)
            {
                throw new InvalidDataException(
                    $"Restore target cleanup evidence did not prove removal of the final target '{finalTargetPath}'.");
            }

            var remainingPhysicalBytes = E2eResourceTracker.MeasurePhysicalBytes(targetRoot);
            return new E2eRestoreTargetCleanupMeasurements(
                targetRoot,
                deletedTargets.ToArray(),
                deletedTargets.Sum(static target => target.PhysicalBytesBeforeDeletion),
                remainingPhysicalBytes,
                finalTargetDeleted,
                remainingEntries);
        }
    }

    private static bool HasReparsePointInPath(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
        }

        return false;
    }

    private static bool IsResourceOwnedEntry(string path, string outputDirectory, string? requestedTempRoot)
    {
        var name = Path.GetFileName(path);
        var tempRoot = Path.GetFullPath(requestedTempRoot ?? Path.Combine(outputDirectory, "tmp"));
        return name is "progress.jsonl"
            or "resource-samples.jsonl"
            or "resource-envelope.json"
            or "e2e-volume-resource-failure.json"
            || string.Equals(Path.GetFullPath(path), tempRoot, StringComparison.Ordinal);
    }

    private static string ValidateRustFsDataPath(E2eOptions options)
    {
        if (options.ObjectTier != "s3")
            return "not-applicable";
        if (options.RustFsDataPath is null)
            return "missing";

        var path = Path.GetFullPath(options.RustFsDataPath);
        if (!Directory.Exists(path) || File.Exists(path) || HasReparsePointInPath(path))
        {
            throw new InvalidDataException(
                $"--rustfs-data-path must be an existing directory without symlink or reparse-point ancestors: '{path}'.");
        }

        try
        {
            if (!E2eResourceTracker.CanMeasureDirectory(path))
                throw new IOException("Physical disk usage is not readable, including the podman user namespace fallback.");
            var root = Path.GetPathRoot(path);
            if (root is null || new DriveInfo(root).AvailableFreeSpace < 0)
                throw new IOException("The RustFS data root filesystem free-space probe is unavailable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidDataException(
                $"--rustfs-data-path is not readable for strict local accounting: '{path}'.", exception);
        }

        return "validated";
    }

    private static bool HasReparsePointInTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;

            foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    return true;
                if (Directory.Exists(entry))
                    pending.Push(entry);
            }
        }

        return false;
    }

    /// <summary>
    /// Reopens a completed restore through the RustFS segment tier. Restore
    /// materialization itself intentionally uses the local transactional
    /// engine; once its checksummed segments are uploaded under a new prefix,
    /// deleting the restored hot/warm/cold copies proves the normal runtime
    /// can reconstruct the same state from remote objects alone.
    /// </summary>
    private static async Task<(long Verified, int Collections, int RemoteObjects, long CacheBytes, long LocalSegmentBytesAfterEviction, TimeSpan Elapsed)>
        VerifyColdRemoteRestoreAsync(
            string activePath,
            IBackupObjectStore objectStore,
            TenantId tenant,
            CollectionId collection,
            E2eOptions options,
            string objectPrefix = "restored-final")
    {
        var started = Stopwatch.GetTimestamp();
        var uploadedSegments = new Dictionary<string, StorageSegmentDescriptor>(StringComparer.Ordinal);
        var placement = new LocalStorageTierPlacement(activePath);
        var manifestPath = Path.Combine(activePath, "MANIFEST");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The restored engine manifest is missing.", manifestPath);

        using (var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false)))
        {
            if (!manifest.RootElement.TryGetProperty("Segments", out var segments)
                || segments.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The restored engine manifest has no active segment list.");
            }

            foreach (var entry in segments.EnumerateArray())
            {
                var segment = new StorageSegmentDescriptor(
                    entry.GetProperty("Id").GetString()!,
                    (StorageTier)entry.GetProperty("Tier").GetInt32())
                {
                    Checksum = entry.GetProperty("Checksum").GetString(),
                    ByteLength = entry.GetProperty("ByteLength").GetInt64(),
                };
                if (segment.Checksum is not { Length: 64 } checksum || segment.ByteLength < 49)
                {
                    throw new InvalidDataException(
                        $"The restored segment '{segment.Id}' has no content-addressed checksum.");
                }

                var remoteKey = $"{objectPrefix}/{checksum}.seg";
                if (uploadedSegments.ContainsKey(remoteKey))
                    continue;

                uploadedSegments.Add(remoteKey, segment);

                var path = placement.Resolve(segment).Path;
                if (!File.Exists(path))
                    throw new FileNotFoundException("The restored segment was not materialized locally.", path);

                await using var input = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var uploaded = await objectStore.PutAsync(
                    remoteKey,
                    input,
                    checksum).ConfigureAwait(false);
                if (uploaded.Length != segment.ByteLength
                    || !string.Equals(uploaded.Sha256, checksum, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The restored remote segment confirmation did not match '{segment.Id}'.");
                }
            }
        }

        foreach (var tier in Enum.GetValues<StorageTier>())
        {
            var directory = Path.Combine(activePath, tier.ToString().ToLowerInvariant());
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*.seg", SearchOption.TopDirectoryOnly))
                File.Delete(path);
        }

        var localSegmentBytesAfterEviction = Enum.GetValues<StorageTier>()
            .Select(tier => Path.Combine(activePath, tier.ToString().ToLowerInvariant()))
            .Where(Directory.Exists)
            .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.seg", SearchOption.TopDirectoryOnly))
            .Select(static path => new FileInfo(path).Length)
            .Sum();

        var cacheRoot = Path.Combine(activePath, "ssd-cache");
        // This restored-target verification reopens an already committed
        // generation only to read and compact it. It performs no acknowledged
        // user mutations, so it explicitly uses the local-WAL-only diagnostic
        // mode; the strict source workload always supplies sourceJournal.
        var tiered = new TieredStorageSegmentStore(new TieredStorageSegmentStoreOptions(
            objectStore,
            cacheRoot,
            objectPrefix,
            maximumCacheBytes: options.CacheBytes,
            allowLocalWalOnly: true));
        var state = new ExpectedState(
            options.Documents,
            options.Seed,
            options.Updates,
            options.Documents - options.Deletes,
            options.Deletes);
        long verified;
        int collections;
        // This is the first reopen with the injected tiered store. The
        // restore target itself has already committed a local generation;
        // this compaction publishes a fresh immutable representation through
        // the remote segment writer before the cache is evicted.
        await using (var tieredEngine = new DurableStorageEngine(
                         activePath,
                         new LocalStorageTierPlacement(activePath),
                         StorageWalDurability.Synced,
                         tiered))
        {
            try
            {
                _ = await tieredEngine.CompactAsync().ConfigureAwait(false);
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("cannot publish more than", StringComparison.Ordinal))
            {
                // Large restored corpora may exceed the single-segment record
                // limit. The already uploaded immutable segments remain a
                // valid remote representation, so retain them and continue
                // with the cold reopen gate.
            }
        }

        foreach (var path in Directory.Exists(cacheRoot)
                     ? Directory.EnumerateFiles(cacheRoot, "*.seg", SearchOption.TopDirectoryOnly).ToArray()
                     : Array.Empty<string>())
        {
            File.Delete(path);
        }

        await using (var reopened = new DurableStorageEngine(
                         activePath,
                         new LocalStorageTierPlacement(activePath),
                         StorageWalDurability.Synced,
                         tiered))
        {
            (verified, _) = await VerifyScanAsync(reopened, tenant, collection, state).ConfigureAwait(false);
            (_, collections, _, _) = await VerifySnapshotPagingAsync(
                reopened,
                tenant,
                collection,
                options,
                state).ConfigureAwait(false);
            if (options.Relationships > 0)
                _ = await VerifyRelationshipsAsync(
                    tenant,
                    new CollectionId("users"),
                    new CollectionId("orders"),
                    reopened,
                    options).ConfigureAwait(false);

            var firstDeleted = options.Documents - options.Deletes;
            for (var index = firstDeleted; index < options.Documents; index++)
            {
                var document = await reopened.ReadAsync(
                    new DocumentKey(tenant, collection, DocumentId(index))).ConfigureAwait(false);
                if (document is not null)
                {
                    throw new InvalidDataException(
                        $"{DocumentId(index)} survived the cold remote reopen but was deleted.");
                }
            }
        }

        var remoteObjects = await VerifyExactRemoteSegmentSetAsync(
            activePath,
            objectStore,
            objectPrefix,
            uploadedSegments).ConfigureAwait(false);

        var cacheBytes = Directory.Exists(cacheRoot)
            ? Directory.EnumerateFiles(cacheRoot, "*.seg", SearchOption.TopDirectoryOnly)
                .Select(static path => new FileInfo(path).Length)
                .Sum()
            : 0L;
        return (verified, collections, remoteObjects, cacheBytes, localSegmentBytesAfterEviction, Stopwatch.GetElapsedTime(started));
    }

    private static async Task<int> VerifyExactRemoteSegmentSetAsync(
        string activePath,
        IBackupObjectStore objectStore,
        string objectPrefix,
        IReadOnlyDictionary<string, StorageSegmentDescriptor> preuploadedSegments)
    {
        var segments = await ReadActiveManifestSegmentsAsync(activePath).ConfigureAwait(false);
        var expected = new Dictionary<string, StorageSegmentDescriptor>(preuploadedSegments, StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            var key = $"{objectPrefix}/{segment.Checksum}.seg";
            if (expected.TryGetValue(key, out var existing)
                && (existing.ByteLength != segment.ByteLength
                    || !string.Equals(existing.Checksum, segment.Checksum, StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"The active manifest contains conflicting remote metadata for '{key}'.");
            }

            expected[key] = segment;
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var metadata in objectStore.ListAsync($"{objectPrefix}/").ConfigureAwait(false))
        {
            if (!expected.TryGetValue(metadata.Key, out var segment)
                || !listed.Add(metadata.Key))
            {
                throw new InvalidDataException(
                    $"The remote segment prefix contains an unexpected or duplicate object '{metadata.Key}'.");
            }

            ValidateRemoteSegmentMetadata(metadata, segment);
        }

        if (listed.Count != expected.Count)
        {
            var missing = expected.Keys.Where(key => !listed.Contains(key));
            throw new FileNotFoundException(
                $"The remote segment prefix is missing active manifest objects: {string.Join(", ", missing)}.");
        }

        foreach (var pair in expected)
        {
            var metadata = await objectStore.HeadAsync(pair.Key).ConfigureAwait(false)
                ?? throw new FileNotFoundException("An active manifest remote segment is missing.", pair.Key);
            ValidateRemoteSegmentMetadata(metadata, pair.Value);
        }

        return expected.Count;
    }

    private static async Task<IReadOnlyList<StorageSegmentDescriptor>> ReadActiveManifestSegmentsAsync(
        string activePath)
    {
        var manifestPath = Path.Combine(activePath, "MANIFEST");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The restored engine manifest is missing.", manifestPath);

        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false));
        if (!manifest.RootElement.TryGetProperty("Segments", out var segments)
            || segments.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The restored engine manifest has no active segment list.");
        }

        var result = new List<StorageSegmentDescriptor>();
        foreach (var entry in segments.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("Id", out var idValue)
                || idValue.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("Tier", out var tierValue)
                || tierValue.ValueKind != JsonValueKind.Number
                || !tierValue.TryGetInt32(out var tier)
                || tier is < (int)StorageTier.Hot or > (int)StorageTier.Cold
                || !entry.TryGetProperty("Checksum", out var checksumValue)
                || checksumValue.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("ByteLength", out var lengthValue)
                || lengthValue.ValueKind != JsonValueKind.Number
                || !lengthValue.TryGetInt64(out var length))
            {
                throw new InvalidDataException("The restored engine manifest contains an invalid active segment.");
            }

            var id = idValue.GetString();
            var checksum = checksumValue.GetString();
            if (string.IsNullOrWhiteSpace(id)
                || id is "." or ".."
                || Path.IsPathRooted(id)
                || id.Contains('/', StringComparison.Ordinal)
                || id.Contains('\\', StringComparison.Ordinal)
                || id.Contains('\0', StringComparison.Ordinal)
                || checksum is not { Length: 64 }
                || !checksum.All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f')
                || length < 49)
            {
                throw new InvalidDataException("The restored engine manifest contains an invalid active segment identity.");
            }

            result.Add(new StorageSegmentDescriptor(id!, (StorageTier)tier)
            {
                Checksum = checksum,
                ByteLength = length,
            });
        }

        return result;
    }

    private static void ValidateRemoteSegmentMetadata(
        BackupObjectMetadata metadata,
        StorageSegmentDescriptor segment)
    {
        if (metadata.Length != segment.ByteLength)
        {
            throw new InvalidDataException(
                $"Remote segment metadata does not match the active manifest for '{metadata.Key}'.");
        }

        var checksumUnavailable = string.Equals(metadata.Sha256, "unknown", StringComparison.OrdinalIgnoreCase)
            || (metadata.Sha256.Length == 64 && metadata.Sha256.All(static value => value == '0'));
        if (!checksumUnavailable
            && !string.Equals(metadata.Sha256, segment.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Remote segment checksum does not match the active manifest for '{metadata.Key}'.");
        }
    }

    private static int ParseDocumentIndex(string identifier, int maximum)
    {
        if (!identifier.StartsWith("doc-", StringComparison.Ordinal)
            || identifier.Length != DocumentIdPrefixLength + DocumentIdDigits
            || !int.TryParse(
                identifier.AsSpan(DocumentIdPrefixLength),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index)
            || index < 0
            || index >= maximum)
        {
            throw new InvalidDataException($"Unexpected document identifier '{identifier}'.");
        }

        return index;
    }

    /// <summary>
    /// Benchmarks point reads: a deterministic random sample of document ids,
    /// each read verified exactly against the stateless expectation, with
    /// per-operation latencies for p50/p95/p99.
    /// </summary>
    private static async Task<(E2eLatencyStats Stats, TimeSpan Elapsed)> PointReadBenchmarkAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId collection,
        E2eOptions options)
    {
        var state = ExpectedState.Initial(options.Documents, options.Seed);
        var latencies = new double[options.PointReads];
        ActiveResources?.SetPhase("point-reads", 0, options.PointReads);
        var started = Stopwatch.GetTimestamp();
        for (var read = 0; read < options.PointReads; read++)
        {
            var index = (int)(Draw((ulong)options.Seed, read, 0x9EAD) % (ulong)options.Documents);
            var readStarted = Stopwatch.GetTimestamp();
            var document = await engine.ReadAsync(new DocumentKey(tenant, collection, DocumentId(index))).ConfigureAwait(false);
            latencies[read] = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds;
            if ((read & 0x3FF) == 0)
                ActiveResources?.ThrowIfExceeded();
            VerifyDocument(state, index, document);
            if (ActiveResources is { } resources && resources.ShouldRecord(read + 1, options.PointReads))
                await resources.RecordProgressAsync("point-reads", read + 1, options.PointReads).ConfigureAwait(false);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        return (E2eLatencyStats.From(options.PointReads, latencies), elapsed);
    }

    /// <summary>
    /// Benchmarks indexed range queries against the restored engine: each
    /// deterministic window over the live "sequence" range is verified for
    /// exact count, exact ids, and exact bodies, and the plan must be served
    /// by the active index rather than a source-scan fallback.
    /// </summary>
    private static async Task<(E2eLatencyStats Stats, TimeSpan Elapsed, E2eQueryDiagnostics Diagnostics)> QueryBenchmarkAsync(
        AutomaticIndexRuntime runtime,
        AutomaticIndexScope scope,
        TenantId tenant,
        CollectionId collection,
        E2eOptions options,
        string outputDirectory)
    {
        var state = new ExpectedState(
            options.Documents,
            options.Seed,
            options.Updates,
            options.Documents - options.Deletes,
            options.Deletes);
        var latencies = new double[options.Queries];
        var samples = new List<E2eQuerySample>(options.Queries);
        ActiveResources?.SetPhase("indexed-queries", 0, options.Queries);
        using var process = Process.GetCurrentProcess();
        var started = Stopwatch.GetTimestamp();
        for (var query = 0; query < options.Queries; query++)
        {
            var draw = Draw((ulong)options.Seed, query, 0x0EED);
            var low = (int)(draw % (ulong)Math.Max(1, state.LiveDocuments - 1));
            var requestedWidth = 1 + (int)((draw >> 17) % 1_000UL);
            var high = Math.Min(low + requestedWidth, state.LiveDocuments) - 1;
            var effectiveWidth = high - low + 1;
            var request = new IndexRuntimeQuery(
                tenant,
                collection,
                [IndexPredicate.GreaterThanOrEqual("sequence", (long)low), IndexPredicate.LessThanOrEqual("sequence", (long)high)],
                resultLimit: 1_000);
            var settleStarted = Stopwatch.GetTimestamp();
            QueryPlannerResult result;
            TimeSpan attemptLatency;
            var generation0Before = 0;
            var generation1Before = 0;
            var generation2Before = 0;
            var rssBefore = 0L;
            while (true)
            {
                generation0Before = GC.CollectionCount(0);
                generation1Before = GC.CollectionCount(1);
                generation2Before = GC.CollectionCount(2);
                rssBefore = process.WorkingSet64;
                var attemptStarted = Stopwatch.GetTimestamp();
                try
                {
                    result = await runtime.QueryAsync(scope, request).ConfigureAwait(false);
                    attemptLatency = Stopwatch.GetElapsedTime(attemptStarted);
                    break;
                }
                catch (QueryPlanningException exception)
                {
                    // Activation publishes planner visibility the instant the
                    // physical generation reaches its build target, while the
                    // live tail keeps applying frames in the background. A
                    // first query that loses that race is retried until the
                    // tail settles — the runtime's own contract is that a
                    // current-source read stays index-served during a brief
                    // tail stall. Only the settling window is retried, and
                    // only the successful attempt's latency is recorded;
                    // later queries must be served immediately.
                    if (query > 0
                        || Stopwatch.GetElapsedTime(settleStarted) > ActivationSettleTimeout)
                    {
                        throw await DescribePlanningFailureAsync(runtime, scope, request, exception).ConfigureAwait(false);
                    }

                    await Task.Delay(ActivationSettlePollInterval).ConfigureAwait(false);
                }
            }

            latencies[query] = attemptLatency.TotalMilliseconds;
            if ((query & 0x3FF) == 0)
                ActiveResources?.ThrowIfExceeded();
            if (ActiveResources is { } resources && resources.ShouldRecord(query + 1, options.Queries))
                await resources.RecordProgressAsync("indexed-queries", query + 1, options.Queries).ConfigureAwait(false);
            var generation0After = GC.CollectionCount(0);
            var generation1After = GC.CollectionCount(1);
            var generation2After = GC.CollectionCount(2);
            var rssAfter = process.WorkingSet64;

            if (string.IsNullOrEmpty(result.Plan.IndexName) || result.Plan.UsedSnapshotFallback)
                throw new InvalidDataException(
                    $"Query {query} was not served by the active index: {result.Plan.ExplainText}.");

            var expectedCount = high - low + 1;
            if (result.Documents.Count != expectedCount)
                throw new InvalidDataException(
                    $"Query {query} [sequence {low}, {high}] returned {result.Documents.Count} documents, expected {expectedCount}.");
            for (var position = 0; position < result.Documents.Count; position++)
            {
                var document = result.Documents[position];
                var index = ParseDocumentIndex(document.Key.DocumentId, options.Documents);
                if (index != low + position)
                    throw new InvalidDataException(
                        $"Query {query} position {position} returned {document.Key.DocumentId}, expected {DocumentId(low + position)}.");
                VerifyDocument(state, index, document);
            }

            var measurement = result.Execution.Measurement;
            var physicalWork = result.Execution.PhysicalWork;
            samples.Add(new E2eQuerySample(
                query,
                low,
                high,
                requestedWidth,
                effectiveWidth,
                result.Documents.Count,
                attemptLatency.TotalMilliseconds,
                measurement.RowsExamined,
                measurement.BytesRead,
                measurement.WorkUnits,
                physicalWork?.EntryReads,
                physicalWork?.FullDocumentReads,
                generation0Before,
                generation0After,
                generation0After - generation0Before,
                generation1Before,
                generation1After,
                generation1After - generation1Before,
                generation2Before,
                generation2After,
                generation2After - generation2Before,
                rssBefore,
                rssAfter,
                rssAfter - rssBefore,
                result.Plan.IndexName,
                result.Plan.UsedSnapshotFallback));
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var diagnostics = await WriteQueryDiagnosticsAsync(samples, outputDirectory).ConfigureAwait(false);
        return (E2eLatencyStats.From(options.Queries, latencies), elapsed, diagnostics);
    }

    private static async Task<E2eQueryDiagnostics> WriteQueryDiagnosticsAsync(
        IReadOnlyList<E2eQuerySample> samples,
        string outputDirectory)
    {
        const string sampleFileName = "indexed-query-samples.jsonl";
        const string worstFileName = "indexed-query-worst.md";
        var samplePath = Path.Combine(outputDirectory, sampleFileName);
        var worstPath = Path.Combine(outputDirectory, worstFileName);
        ulong totalRowsExamined = 0;
        ulong totalBytesRead = 0;
        ulong totalWorkUnits = 0;
        ulong totalEntryReads = 0;
        ulong totalFullDocumentReads = 0;
        var physicalCountersAvailable = true;
        var generation0Collections = 0;
        var generation1Collections = 0;
        var generation2Collections = 0;
        var peakRssBytes = 0L;

        await using (var writer = new StreamWriter(samplePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            foreach (var sample in samples)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(sample, CompactJsonOptions)).ConfigureAwait(false);
                totalRowsExamined = checked(totalRowsExamined + sample.RowsExamined);
                totalBytesRead = checked(totalBytesRead + sample.BytesRead);
                totalWorkUnits = checked(totalWorkUnits + sample.WorkUnits);
                if (sample.EntryReads is { } entryReads && sample.FullDocumentReads is { } fullDocumentReads)
                {
                    totalEntryReads = checked(totalEntryReads + entryReads);
                    totalFullDocumentReads = checked(totalFullDocumentReads + fullDocumentReads);
                }
                else
                {
                    physicalCountersAvailable = false;
                }

                generation0Collections = checked(generation0Collections + sample.Generation0Collections);
                generation1Collections = checked(generation1Collections + sample.Generation1Collections);
                generation2Collections = checked(generation2Collections + sample.Generation2Collections);
                peakRssBytes = Math.Max(peakRssBytes, Math.Max(sample.RssBeforeBytes, sample.RssAfterBytes));
            }
        }

        var worst = samples
            .OrderByDescending(static sample => sample.ElapsedMilliseconds)
            .ThenBy(static sample => sample.Query)
            .Take(20)
            .ToArray();
        var markdown = new StringBuilder()
            .AppendLine("# Indexed query worst samples")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{samples.Count:N0} deterministic indexed query samples; rows, bytes, work, and physical counters are the runtime receipt for the successful query attempt. RSS is `Process.WorkingSet64`; GC columns are collection deltas during that attempt.")
            .AppendLine()
            .AppendLine("| Query | Low | High | Requested width | Effective width | Elapsed ms | Rows examined | Bytes read | Work units | Entry reads | Full document reads | GC Δ (0/1/2) | RSS Δ B |")
            .AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var sample in worst)
        {
            markdown.AppendLine(CultureInfo.InvariantCulture, $"| {sample.Query} | {sample.LowSequence} | {sample.HighSequence} | {sample.RequestedWindowWidth} | {sample.EffectiveWindowWidth} | {sample.ElapsedMilliseconds:F3} | {sample.RowsExamined:N0} | {sample.BytesRead:N0} | {sample.WorkUnits:N0} | {FormatCounter(sample.EntryReads)} | {FormatCounter(sample.FullDocumentReads)} | {sample.Generation0Collections}/{sample.Generation1Collections}/{sample.Generation2Collections} | {sample.RssDeltaBytes:N0} |");
        }

        await File.WriteAllTextAsync(worstPath, markdown.ToString()).ConfigureAwait(false);
        return new E2eQueryDiagnostics(
            samples.Count,
            sampleFileName,
            worstFileName,
            totalRowsExamined,
            totalBytesRead,
            totalWorkUnits,
            physicalCountersAvailable ? totalEntryReads : null,
            physicalCountersAvailable ? totalFullDocumentReads : null,
            physicalCountersAvailable,
            generation0Collections,
            generation1Collections,
            generation2Collections,
            peakRssBytes,
            samples.Count == 0 ? null : samples.Max(static sample => sample.ElapsedMilliseconds));
    }

    private static string FormatCounter(ulong? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable";

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

    private sealed class StaticMetadataProvider(string tenant, string collection)
        : IStorageBackupMetadataProvider
    {
        public ValueTask<StorageBackupMetadataSnapshot> CaptureAsync(
            ulong commitSequence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new StorageBackupMetadataSnapshot(
                [
                    new StorageBackupCollectionScope(tenant, collection),
                    new StorageBackupCollectionScope("e2e-volume", "users"),
                    new StorageBackupCollectionScope("e2e-volume", "orders"),
                ],
                [],
                []));
        }
    }

    private sealed class E2eOptions
    {
        public string ObjectTier { get; private set; } = "local";
        public int Documents { get; private set; } = 100_000;

        public int BatchSize { get; private set; } = 1_000;

        public int PointReads { get; private set; } = 50_000;

        public int Updates { get; private set; } = 1_000;

        public int Deletes { get; private set; } = 1_000;

        public int Queries { get; private set; } = 1_000;

        /// <summary>
        /// Maximum documents in one durable automatic-index snapshot build
        /// batch. This is recorded in the receipt so 256/1024 comparisons
        /// can use the same benchmark binary and fixture.
        /// </summary>
        public int SnapshotBuildMaximumDocuments { get; private set; } = 1_024;

        public int Relationships { get; private set; } = 10_000;

        public int Seed { get; private set; } = 1337;

        public bool RootLossRecovery { get; private set; }

        public long CacheBytes { get; private set; } = 16L * 1024 * 1024;

        public long CheckpointRecords { get; private set; } = 500_000;

        public int ResourceSampleIntervalMilliseconds { get; private set; } = 1_000;

        public long? MaxLocalStorageBytes { get; private set; }

        public long MinimumFreeSpaceBytes { get; private set; }

        public bool RequireIsolatedCgroup { get; private set; }

        public string? TempRoot { get; private set; }

        public string? RustFsDataPath { get; private set; }

        public string OutputDirectory { get; private set; } = Path.Combine(
            "bench",
            "receipts",
            $"e2e-volume-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");

        public bool ShowHelp { get; private set; }

        public static E2eOptions Parse(string[] args)
        {
            var options = new E2eOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument is "--help" or "-h")
                {
                    options.ShowHelp = true;
                    continue;
                }
                if (argument.Equals("--root-loss-recovery", StringComparison.OrdinalIgnoreCase))
                {
                    options.RootLossRecovery = true;
                    continue;
                }
                if (argument.Equals("--require-isolated-cgroup", StringComparison.OrdinalIgnoreCase))
                {
                    options.RequireIsolatedCgroup = true;
                    continue;
                }

                var (name, value) = ReadOption(args, ref index, argument);
                switch (name)
                {
                    case "object-tier":
                        if (value is not ("local" or "s3")) throw new ArgumentException("--object-tier must be local or s3.");
                        options.ObjectTier = value;
                        break;
                    case "workload":
                        break;
                    case "documents":
                        options.Documents = ParseInteger(value, name, 1);
                        break;
                    case "batch-size":
                        options.BatchSize = ParseInteger(value, name, 1);
                        break;
                    case "point-reads":
                        options.PointReads = ParseInteger(value, name, 0);
                        break;
                    case "updates":
                        options.Updates = ParseInteger(value, name, 0);
                        break;
                    case "deletes":
                        options.Deletes = ParseInteger(value, name, 0);
                        break;
                    case "queries":
                        options.Queries = ParseInteger(value, name, 0);
                        break;
                    case "snapshot-build-maximum-documents":
                        options.SnapshotBuildMaximumDocuments = ParseInteger(value, name, 1);
                        break;
                    case "relationships":
                        options.Relationships = ParseInteger(value, name, 0);
                        break;
                    case "seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
                            throw new ArgumentException($"--{name} must be a 32-bit integer.");
                        options.Seed = seed;
                        break;
                    case "output":
                        options.OutputDirectory = value;
                        break;
                    case "cache-bytes":
                        options.CacheBytes = ParseLong(value, name, 0);
                        break;
                    case "checkpoint-records":
                        options.CheckpointRecords = ParseLong(value, name, 1);
                        break;
                    case "resource-sample-ms":
                        options.ResourceSampleIntervalMilliseconds = ParseInt(value, name, 1);
                        break;
                    case "max-local-storage-bytes":
                        options.MaxLocalStorageBytes = ParseLong(value, name, 1);
                        break;
                    case "min-free-space-bytes":
                        options.MinimumFreeSpaceBytes = ParseLong(value, name, 0);
                        break;
                    case "temp-root":
                        options.TempRoot = value;
                        break;
                    case "rustfs-data-path":
                        options.RustFsDataPath = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '--{name}'.");
                }
            }

            if ((long)options.Updates + options.Deletes > options.Documents)
                throw new ArgumentException(
                    "--updates plus --deletes must not exceed --documents (updates touch the head, deletes the tail).");
            if ((long)options.Updates + options.Deletes == 0)
                throw new ArgumentException(
                    "--updates plus --deletes must be at least one: the incremental backup and PITR phases require a publication after the full backup.");
            if (options.RootLossRecovery && options.ObjectTier != "s3")
                throw new ArgumentException(
                    "--root-loss-recovery requires --object-tier s3 so recovery reads committed remote backup artifacts.");
            if (options.ObjectTier != "s3" && options.RustFsDataPath is not null)
                throw new ArgumentException("--rustfs-data-path requires --object-tier s3.");
            if (options.ObjectTier == "s3"
                && options.RustFsDataPath is null
                && (options.MaxLocalStorageBytes is not null || options.MinimumFreeSpaceBytes > 0))
            {
                throw new ArgumentException(
                    "--rustfs-data-path is required when an S3 run uses --max-local-storage-bytes or --min-free-space-bytes; strict local accounting would otherwise omit the RustFS data root.");
            }
            if (options.Queries > 0 && options.Deletes == options.Documents)
                throw new ArgumentException("--deletes must be less than --documents when --queries is greater than zero.");
            if (options.SnapshotBuildMaximumDocuments > 4_096)
                throw new ArgumentException("--snapshot-build-maximum-documents must be <= 4096.");

            _ = BatchCount(options.Documents, options.BatchSize);
            ValidateDocumentIdConfiguration(options.Documents);
            return options;
        }

        public static void PrintHelp()
        {
            Console.WriteLine("TerraBase end-to-end volume qualification");
            Console.WriteLine("  --workload e2e-volume|e2e-100k|e2e-10m");
            Console.WriteLine("  --object-tier local|s3  Source immutable segments and backup objects (S3 credentials via TERRABASE_S3_* env vars)");
            Console.WriteLine("  --documents N         Random documents written and verified (default: 100000)");
            Console.WriteLine("  --batch-size N        Documents per committed batch (default: 1000)");
            Console.WriteLine("  --point-reads N       Verified random point reads on the source engine (default: 50000)");
            Console.WriteLine("  --updates N           Bulk updated documents, head range [0, N) (default: 1000)");
            Console.WriteLine("  --deletes N           Bulk deleted documents, tail range (default: 1000)");
            Console.WriteLine("  --queries N           Verified indexed range queries on the restored engine (default: 1000)");
            Console.WriteLine("  --snapshot-build-maximum-documents N  Maximum documents per durable index snapshot batch (default: 1024; max: 4096)");
            Console.WriteLine("  --relationships N     Related documents written through the schema store (default: 10000)");
            Console.WriteLine("  --seed N              Fixed fixture seed (default: 1337)");
            Console.WriteLine("  --output DIR          Receipt and object-store directory");
            Console.WriteLine("  --root-loss-recovery  Opt in to deleting the owned source root and recovering from committed S3 backups");
            Console.WriteLine("  --cache-bytes N       Bounded SSD segment cache (default: 16777216)");
            Console.WriteLine("  --checkpoint-records N Progress/resource checkpoint interval (default: 500000)");
            Console.WriteLine("  --resource-sample-ms N Resource sample interval (default: 1000)");
            Console.WriteLine("  --max-local-storage-bytes N Finite cap across output, temp, and RustFS data roots");
            Console.WriteLine("  --min-free-space-bytes N Fail closed before the measured filesystem reaches this free-space floor");
        Console.WriteLine("  --temp-root DIR       Run-owned TMPDIR for backup and restore spools; set TMPDIR to this path before launch");
            Console.WriteLine("  --rustfs-data-path DIR Include a local RustFS data root in disk/free-space accounting");
            Console.WriteLine("  --require-isolated-cgroup Require a finite memory cgroup limit");
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

        private static int ParseInt(string value, string name, int minimum)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
                throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
            return parsed;
        }

        private static long ParseLong(string value, string name, long minimum)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
                throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
            return parsed;
        }
    }

    private sealed record E2eLatencyStats(
        int Operations,
        double OperationsPerSecond,
        double P50Milliseconds,
        double P95Milliseconds,
        double P99Milliseconds)
    {
        /// <summary>
        /// Throughput is derived from the summed per-operation time, not from
        /// the phase wall clock: the phase interleaves harness verification
        /// (body regeneration and deep equality) that is not database work.
        /// </summary>
        public static E2eLatencyStats From(int operations, double[] latencyMilliseconds)
        {
            var totalMilliseconds = 0.0;
            foreach (var latency in latencyMilliseconds)
            {
                totalMilliseconds += latency;
            }

            Array.Sort(latencyMilliseconds);
            return new E2eLatencyStats(
                operations,
                operations / Math.Max(totalMilliseconds / 1000.0, 0.000_001),
                Percentile(latencyMilliseconds, 0.50),
                Percentile(latencyMilliseconds, 0.95),
                Percentile(latencyMilliseconds, 0.99));
        }

        private static double Percentile(double[] sorted, double fraction) =>
            sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(fraction * sorted.Length))];
    }

    private sealed record E2eQuerySample(
        int Query,
        int LowSequence,
        int HighSequence,
        int RequestedWindowWidth,
        int EffectiveWindowWidth,
        int ResultCount,
        double ElapsedMilliseconds,
        ulong RowsExamined,
        ulong BytesRead,
        ulong WorkUnits,
        ulong? EntryReads,
        ulong? FullDocumentReads,
        int Generation0CollectionsBefore,
        int Generation0CollectionsAfter,
        int Generation0Collections,
        int Generation1CollectionsBefore,
        int Generation1CollectionsAfter,
        int Generation1Collections,
        int Generation2CollectionsBefore,
        int Generation2CollectionsAfter,
        int Generation2Collections,
        long RssBeforeBytes,
        long RssAfterBytes,
        long RssDeltaBytes,
        string IndexName,
        bool UsedSnapshotFallback);

    private sealed record E2eQueryDiagnostics(
        int Samples,
        string SampleJsonl,
        string WorstMarkdown,
        ulong TotalRowsExamined,
        ulong TotalBytesRead,
        ulong TotalWorkUnits,
        ulong? TotalEntryReads,
        ulong? TotalFullDocumentReads,
        bool PhysicalCountersAvailable,
        int Generation0Collections,
        int Generation1Collections,
        int Generation2Collections,
        long PeakRssBytes,
        double? WorstElapsedMilliseconds);

    private sealed record E2eReceipt(
        string Status,
        DateTimeOffset FinishedAtUtc,
        SubjectProvenance Subject,
        E2eOptions Configuration,
        E2ePhaseMeasurements Phases,
        E2eVolumeMeasurements Volume,
        E2eRootLossRecoveryMeasurements? RootLossRecovery,
        E2eRestoreTargetCleanupMeasurements RestoreTargetCleanup,
        string? Error,
        E2eResourceSnapshot? Resources = null,
        E2eSourceAckDurability? SourceAckDurability = null);

    private sealed record E2eSourceAckDurability(
        bool StrictSourceAckDurability,
        string Mode,
        string? DatabaseIdentity,
        string? WriterIdentity,
        string? JournalFramePrefix,
        ulong? RemoteJournalFrontier,
        int? RemoteJournalFrameObjects,
        long? RemoteJournalBytes);

    private sealed record E2eRestoreTargetCleanupEntry(
        string Name,
        string Path,
        long PhysicalBytesBeforeDeletion);

    private sealed record E2eRestoreTargetCleanupMeasurements(
        string TargetRoot,
        E2eRestoreTargetCleanupEntry[] DeletedTargets,
        long PhysicalBytesReclaimed,
        long RemainingPhysicalBytes,
        bool FinalTargetDeleted,
        string[] RemainingEntries);

    private sealed record E2eRootLossRecoveryMeasurements(
        bool SourceRootDeleted,
        int AppliedLogEntries,
        long DocumentsVerified,
        long DeletedDocumentsVerifiedAbsent,
        int CollectionsVerified,
        int RemoteSegmentObjects,
        long ColdRemoteCacheBytes,
        long RestoredLocalSegmentBytesAfterEviction,
        TimeSpan RestoreElapsed,
        TimeSpan ColdRemoteElapsed,
        int ArtifactsVerified,
        long ArtifactBytesVerified,
        bool JournalSourceRecoveryVerified,
        long JournalSourceDocumentsVerified,
        TimeSpan JournalSourceRecoveryElapsed,
        ulong JournalSourceFrontier,
        string CheckpointPath);

    private sealed record RootLossRecoveryCheckpoint(
        string Mode,
        string Status,
        string Phase,
        DateTimeOffset UpdatedAtUtc,
        bool SourceRootDeleted,
        int ArtifactsExpected,
        int ArtifactsVerified,
        long ArtifactBytesVerified,
        int AppliedLogEntries,
        string? Failure);

    private sealed record E2ePhaseMeasurements(
        TimeSpan Write,
        TimeSpan RelationshipWrite,
        TimeSpan RelationshipVerify,
        TimeSpan VerifyReadback,
        TimeSpan PointReads,
        TimeSpan FullBackup,
        TimeSpan Updates,
        TimeSpan Deletes,
        TimeSpan IncrementalBackup,
        TimeSpan FullRestore,
        TimeSpan FullRestoreVerify,
        TimeSpan PitrRestore,
        TimeSpan PitrRestoreVerify,
        TimeSpan FinalRestore,
        TimeSpan FinalRestoreVerify,
        TimeSpan SnapshotPaging,
        TimeSpan CollectionListing,
        TimeSpan ColdRemoteReopen,
        TimeSpan IndexBuild,
        TimeSpan Queries,
        TimeSpan Compaction,
        TimeSpan CompactionVerify);

    private sealed record E2eVolumeMeasurements(
        int DocumentsWritten,
        long DocumentsVerifiedOriginal,
        long FullArtifactBytes,
        string FullArtifactSha256,
        long IncrementalArtifactBytes,
        string IncrementalArtifactSha256,
        long DocumentsVerifiedFullRestore,
        long DocumentsVerifiedPitr,
        int PitrAppliedLogEntries,
        long DocumentsVerifiedFinal,
        long DeletedDocumentsVerifiedAbsent,
        E2eLatencyStats? PointReadLatency,
        E2eLatencyStats? QueryLatency,
        ulong CompactionRecordsWritten,
        ulong CompactionTombstonesRetained,
        long DocumentsVerifiedCompacted,
        int RelationshipVerifyPasses,
        int QueriesVerified,
        long DocumentsVerifiedSnapshot,
        int CollectionsVerified,
        long RelationshipEndpointsVerified,
        string RelationshipVerificationScope,
        long DocumentsVerifiedColdRemote,
        int ColdRemoteCollectionsVerified,
        int RemoteSegmentObjects,
        long ColdRemoteCacheBytes,
        long RestoredLocalSegmentBytesAfterEviction,
        E2eQueryDiagnostics? QueryDiagnostics);

    private static class E2eMarkdown
    {
        public static string Create(E2eReceipt receipt)
        {
            var phase = receipt.Phases;
            var volume = receipt.Volume;
            var options = receipt.Configuration;
            var pointReads = volume.PointReadLatency;
            var queries = volume.QueryLatency;
            return $"""
                # TerraBase end-to-end volume qualification

                Status: **{receipt.Status}** — {receipt.FinishedAtUtc:O}

                {volume.DocumentsWritten:N0} deterministic random documents written in batches of {options.BatchSize:N0}, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around {options.Updates:N0} updates and {options.Deletes:N0} deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

                Source acknowledgement durability: **{DescribeSourceAckDurability(receipt.SourceAckDurability)}**

                Automatic index snapshot build batch: **{options.SnapshotBuildMaximumDocuments:N0} documents** (`Configuration.SnapshotBuildMaximumDocuments` in the JSON receipt).

                | Phase | Elapsed | Note |
                | --- | ---: | ---: |
                | Bulk write | {phase.Write.TotalMilliseconds:N0} ms | {volume.DocumentsWritten / Math.Max(phase.Write.TotalSeconds, 0.001):N0} docs/s |
                | Relationship write | {phase.RelationshipWrite.TotalMilliseconds:N0} ms | {options.Relationships:N0} orders + users through the schema store |
                | Relationship verify | {phase.RelationshipVerify.TotalMilliseconds:N0} ms | {volume.RelationshipEndpointsVerified:N0} endpoints; {volume.RelationshipVerificationScope}; traversal, restrict delete |
                | Read-back verify | {phase.VerifyReadback.TotalMilliseconds:N0} ms | {volume.DocumentsVerifiedOriginal:N0} exact matches |
                | Point reads | {phase.PointReads.TotalMilliseconds:N0} ms | {DescribeLatency(pointReads)} |
                | Full backup | {phase.FullBackup.TotalMilliseconds:N0} ms | artifact {volume.FullArtifactBytes:N0} B |
                | Bulk updates | {phase.Updates.TotalMilliseconds:N0} ms | {options.Updates:N0} documents to v2 |
                | Bulk deletes | {phase.Deletes.TotalMilliseconds:N0} ms | {options.Deletes:N0} tombstones |
                | Incremental backup | {phase.IncrementalBackup.TotalMilliseconds:N0} ms | artifact {volume.IncrementalArtifactBytes:N0} B |
                | Full-boundary restore | {phase.FullRestore.TotalMilliseconds:N0} ms | 0 log entries |
                | Full restore verify | {phase.FullRestoreVerify.TotalMilliseconds:N0} ms | {volume.DocumentsVerifiedFullRestore:N0} exact matches |
                | PITR restore | {phase.PitrRestore.TotalMilliseconds:N0} ms | {volume.PitrAppliedLogEntries} log entries |
                | PITR restore verify | {phase.PitrRestoreVerify.TotalMilliseconds:N0} ms | {volume.DocumentsVerifiedPitr:N0} exact matches |
                | Final restore | {phase.FinalRestore.TotalMilliseconds:N0} ms | full incremental chain |
                | Final restore verify | {phase.FinalRestoreVerify.TotalMilliseconds:N0} ms | {volume.DocumentsVerifiedFinal:N0} exact, {volume.DeletedDocumentsVerifiedAbsent:N0} absent |
                | Snapshot document pages | {phase.SnapshotPaging.TotalMilliseconds:N0} ms | {volume.DocumentsVerifiedSnapshot:N0} exact documents |
                | Snapshot collection pages | {phase.CollectionListing.TotalMilliseconds:N0} ms | {volume.CollectionsVerified:N0} collections |
                | Cold RustFS reopen | {phase.ColdRemoteReopen.TotalMilliseconds:N0} ms | {volume.DocumentsVerifiedColdRemote:N0} exact, {volume.ColdRemoteCollectionsVerified:N0} collections, {volume.RemoteSegmentObjects:N0} segment objects, SSD cache {volume.ColdRemoteCacheBytes:N0} B, restored local segments {volume.RestoredLocalSegmentBytesAfterEviction:N0} B |
                | Root-loss recovery | {DescribeRootLossElapsed(receipt.RootLossRecovery)} | {DescribeRootLossRecovery(receipt.RootLossRecovery)} |
                | Restore-target cleanup | {receipt.RestoreTargetCleanup.DeletedTargets.Length:N0} roots | {DescribeRestoreTargetCleanup(receipt.RestoreTargetCleanup)} |
                | Index build | {phase.IndexBuild.TotalMilliseconds:N0} ms | automatic index on sequence, active |
                | Indexed queries | {phase.Queries.TotalMilliseconds:N0} ms | {DescribeLatency(queries)} |
                | Compaction | {phase.Compaction.TotalMilliseconds:N0} ms | {volume.CompactionRecordsWritten:N0} records, {volume.CompactionTombstonesRetained:N0} tombstones |
                | Compaction verify | {phase.CompactionVerify.TotalMilliseconds:N0} ms | {volume.DocumentsVerifiedCompacted:N0} exact matches |

                Full artifact plaintext SHA-256: `{volume.FullArtifactSha256}`
                Incremental artifact plaintext SHA-256: `{volume.IncrementalArtifactSha256}`
                {DescribeResources(receipt.Resources)}
                {DescribeQueryDiagnostics(volume.QueryDiagnostics)}
                """;
        }

        private static string DescribeLatency(E2eLatencyStats? stats) => stats is null
            ? "skipped"
            : $"{stats.OperationsPerSecond:N0} ops/s, p50 {stats.P50Milliseconds:N3} ms, p95 {stats.P95Milliseconds:N3} ms, p99 {stats.P99Milliseconds:N3} ms";

        private static string DescribeRootLossElapsed(E2eRootLossRecoveryMeasurements? recovery) => recovery is null
            ? "skipped"
            : $"{recovery.JournalSourceRecoveryElapsed.TotalMilliseconds + recovery.RestoreElapsed.TotalMilliseconds + recovery.ColdRemoteElapsed.TotalMilliseconds:N0} ms";

        private static string DescribeRootLossRecovery(E2eRootLossRecoveryMeasurements? recovery) => recovery is null
            ? "opt-in flag disabled"
            : $"source deleted={recovery.SourceRootDeleted}; {recovery.DocumentsVerified:N0} exact; "
              + $"{recovery.DeletedDocumentsVerifiedAbsent:N0} absent; {recovery.CollectionsVerified:N0} collections; "
              + $"{recovery.AppliedLogEntries} log entries; {recovery.RemoteSegmentObjects:N0} remote segments; "
              + $"{recovery.ArtifactsVerified:N0} artifacts/{recovery.ArtifactBytesVerified:N0} B preflighted; "
              + $"journal source recovery={recovery.JournalSourceRecoveryVerified} "
              + $"({recovery.JournalSourceDocumentsVerified:N0} exact, frontier {recovery.JournalSourceFrontier:N0}, "
              + $"{recovery.JournalSourceRecoveryElapsed.TotalMilliseconds:N0} ms); "
              + $"SSD cache {recovery.ColdRemoteCacheBytes:N0} B; local segments after eviction {recovery.RestoredLocalSegmentBytesAfterEviction:N0} B "
              + $"(restore {recovery.RestoreElapsed.TotalMilliseconds:N0} ms, cold reopen {recovery.ColdRemoteElapsed.TotalMilliseconds:N0} ms); "
              + $"checkpoint `{recovery.CheckpointPath}`";

        private static string DescribeSourceAckDurability(E2eSourceAckDurability? durability) => durability is null
            ? "receipt field unavailable"
            : durability.StrictSourceAckDurability
                ? $"strict remote commit journal; frontier {durability.RemoteJournalFrontier:N0}; "
                  + $"{durability.RemoteJournalFrameObjects:N0} frames/{durability.RemoteJournalBytes:N0} B; "
                  + $"database {durability.DatabaseIdentity}; writer {durability.WriterIdentity}; "
                  + $"prefix `{durability.JournalFramePrefix}`"
                : "local WAL only (strict remote source acknowledgement disabled)";

        private static string DescribeRestoreTargetCleanup(E2eRestoreTargetCleanupMeasurements cleanup) =>
            $"final deleted={cleanup.FinalTargetDeleted}; reclaimed {cleanup.PhysicalBytesReclaimed:N0} B; remaining entries [{string.Join(", ", cleanup.RemainingEntries)}], {cleanup.RemainingPhysicalBytes:N0} B after cleanup";

        private static string DescribeQueryDiagnostics(E2eQueryDiagnostics? diagnostics) => diagnostics is null
            ? "Indexed query diagnostics: skipped."
            : $"Indexed query samples: `{diagnostics.SampleJsonl}`; worst-query table: `{diagnostics.WorstMarkdown}`; logical totals rows {diagnostics.TotalRowsExamined.ToString("N0", CultureInfo.InvariantCulture)}, bytes {diagnostics.TotalBytesRead.ToString("N0", CultureInfo.InvariantCulture)}, work {diagnostics.TotalWorkUnits.ToString("N0", CultureInfo.InvariantCulture)}; physical counters available: {diagnostics.PhysicalCountersAvailable}.";

        private static string DescribeResources(E2eResourceSnapshot? resources) => resources is null
            ? "Resource instrumentation: unavailable."
            : $"""
                ## Resource envelope

                | Measure | Value |
                | --- | ---: |
                | Isolated cgroup | {resources.IsolatedCgroup} |
                | Configured local cap | {resources.MaxLocalStorageBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "unbounded"} B |
                | Peak local bytes | {resources.PeakLocalStorageBytes:N0} B |
                | Maximum observed cap overshoot | {resources.MaximumObservedCapOvershootBytes:N0} B (qualification requires 0 B) |
                | Peak source bytes | {resources.PeakSourceBytes:N0} B |
                | Peak target bytes | {resources.PeakTargetBytes:N0} B |
                | Peak upload/checkpoint bytes | {resources.PeakUploadsBytes:N0} B |
                | Peak temporary spool bytes | {resources.PeakTemporaryBytes:N0} B |
                | Resolved `Path.GetTempPath()` | `{resources.ResolvedTempPath}` |
                | Peak RustFS data-root bytes | {resources.PeakRustFsDataBytes:N0} B |
                | RustFS data-root baseline | {resources.InitialRustFsDataBytes:N0} B |
                | RustFS data-root growth | {resources.RustFsDataGrowthBytes:N0} B |
                | Peak SSD cache bytes | {resources.PeakCacheBytes:N0} B |
                | Minimum free bytes observed | {resources.MinimumObservedFreeBytes:N0} B (floor {resources.MinimumFreeSpaceBytes:N0} B) |
                | Peak RSS | {resources.PeakRssBytes:N0} B |
                | RustFS objects | {resources.Remote?.ObjectCount.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable"} |
                | RustFS bytes | {resources.Remote?.TotalBytes.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable"} B |
                | RustFS data-root accounting | {resources.RustFsDataPathStatus} |
                | Swap current at final sample | {resources.SwapCurrentBytes:N0} B |
                | cgroup oom events at final sample | {resources.OomEvents:N0} |
                | cgroup oom kill events at final sample | {resources.OomKillEvents:N0} |
                | cgroup OOM deltas | oom {resources.InitialOomEvents:N0}->{resources.FinalOomEvents:N0}; oom_kill {resources.InitialOomKillEvents:N0}->{resources.FinalOomKillEvents:N0} |

                Resource samples: `{resources.ResourceSamplesPath}`; progress: `{resources.ProgressPath}`; envelope: `resource-envelope.json`.
                """;
    }
}
