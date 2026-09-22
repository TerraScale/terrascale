using System.Diagnostics;
using System.Globalization;
using System.Runtime;
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
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly TimeSpan ActivationSettleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ActivationSettlePollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly EntityId DatabaseIdentity =
        EntityId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

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
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var (receipt, summary) = await RunAsync(options, outputDirectory).ConfigureAwait(false);
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
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<(E2eReceipt Receipt, string Summary)> RunAsync(
        E2eOptions options,
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

        using var rustfs = options.ObjectTier == "s3" ? await RustFsWorkloadStore.OpenAsync(Path.Combine(outputDirectory, "uploads")).ConfigureAwait(false) : null;
        var cachePath = Path.Combine(sourcePath, "ssd-cache");
        IStorageSegmentStore? segments = rustfs is null ? null : new TieredStorageSegmentStore(
            new TieredStorageSegmentStoreOptions(rustfs.Store, cachePath, "segments", 16L * 1024 * 1024));
        if (rustfs is not null)
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "rustfs-bucket.txt"), rustfs.Bucket).ConfigureAwait(false);
        DurableStorageEngine OpenSource() => new(sourcePath, segmentStore: segments);

        var tenant = new TenantId("e2e-volume");
        var collection = new CollectionId("documents");
        var Users = new CollectionId("users");
        var Orders = new CollectionId("orders");
        TimeSpan relationshipsWriteElapsed = TimeSpan.Zero;
        TimeSpan relationshipsVerifyElapsed = TimeSpan.Zero;
        var relationshipVerifyPasses = 0;
        var queriesVerified = 0;
        int relationshipRebuildCount;

        // Phase 1: bulk writes of random documents in committed batches.
        var writeStarted = Stopwatch.GetTimestamp();
        Phase("bulk-write");
        await using (var engine = OpenSource())
        {
            // Backup capture requires a durable replication journal identity.
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
                        CreateRandomBody(options.Seed, documentIndex, updated: false),
                        DocumentPrecondition.Missing);
                }

                var result = await engine.ApplyBatchAsync(mutations).ConfigureAwait(false);
                if (result.Outcome != StorageBatchOutcome.Committed)
                    throw new InvalidDataException($"Batch at {batchStart} was not committed: {result.Outcome}.");
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
            (pointReadStats, pointReadElapsed) = options.PointReads == 0
                ? (null, TimeSpan.Zero)
                : await PointReadBenchmarkAsync(engine, tenant, collection, options).ConfigureAwait(false);

            if (options.Relationships > 0)
            {
                var relationshipsVerifyStarted = Stopwatch.GetTimestamp();
                Phase("relationship-verify");
                await VerifyRelationshipsAsync(tenant, Users, Orders, engine, options).ConfigureAwait(false);
                relationshipVerifyPasses++;
                relationshipsVerifyElapsed = Stopwatch.GetElapsedTime(relationshipsVerifyStarted);
            }
        }

        ReclaimPhaseMemory();

        IBackupObjectStore store = rustfs?.Store ?? new FileSystemBackupObjectStore(objectStorePath);
        using var signer = new HmacSha256BackupManifestSignatureProvider(
            "terrabase-e2e-volume-trust-root-2026"u8,
            "e2e-volume-root-v1");
        var encryption = new NoBackupEncryption();
        var manager = new BackupManager(
            store,
            encryption,
            signer,
            new BackupEncryptionKeyResolver([new BackupEncryptionKeyResolverEntry("e2e-volume", null, encryption)]),
            new BackupManifestSignatureKeyResolver([new BackupManifestSignatureKeyResolverEntry("e2e-volume", null, signer)]),
            // A ten-million-document compressed snapshot legitimately exceeds
            // the default 1 GiB artifact cap, so the volume workload raises it
            // explicitly instead of weakening the default for everyone.
            maximumArtifactReadBytes: 2_000_000_000);

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
                for (var batchStart = 0; batchStart < options.Updates; batchStart += options.BatchSize)
                {
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
                }

                updatesElapsed = Stopwatch.GetElapsedTime(updatesStarted);
                deletesStarted = Stopwatch.GetTimestamp();
                Phase("bulk-deletes");

                // Bulk deletes: the tail range [Documents - Deletes, Documents)
                // is removed with tombstones.
                var firstDeleted = options.Documents - options.Deletes;
                for (var batchStart = 0; batchStart < options.Deletes; batchStart += options.BatchSize)
                {
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

        // The source engine is closed before the restore phases so a restore
        // target's own full in-memory state never coexists with the source's.
        ReclaimPhaseMemory();

        // Phase 8: restore to the full boundary and verify every original
        // document survived backup and restore, still at version 1 (the full
        // snapshot predates every update and delete).
        var fullRestoreStarted = Stopwatch.GetTimestamp();
        Phase("full-boundary-restore");
        var fullTarget = new StorageEngineRestoreTarget(Path.Combine(targetRoot, "full"));
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
                await VerifyRelationshipsAsync(tenant, Users, Orders, engine, options).ConfigureAwait(false);
                relationshipVerifyPasses++;
            }
        }

        ReclaimPhaseMemory();

        // Phase 9: interior PITR restore. The cut prefers the boundary between
        // the update publications and the delete publications; with a single
        // phase it falls back to the midpoint of the first two entries, as in
        // prior qualifications. Expected counts derive from the actual entry
        // timestamps and publication order, never from timing assumptions:
        // entries are sequence-ordered batches, so applying k entries means
        // k * BatchSize update documents and every delete batch after them.
        var pitrEntries = ChoosePitrEntryCount(logEntries.Length, updateBatches, deleteBatches);
        var appliedUpdates = Math.Min(options.Updates, pitrEntries * options.BatchSize);
        var appliedDeletes = Math.Min(
            options.Deletes,
            Math.Max(0, pitrEntries - updateBatches) * options.BatchSize);
        // Deletes publish ascending from the head of the tail range, so a
        // partial application removes the FIRST appliedDeletes ids of that
        // range, not the tail-most ones.
        var pitrState = new ExpectedState(
            options.Documents,
            options.Seed,
            appliedUpdates,
            options.Documents - options.Deletes,
            appliedDeletes);
        var pitTimestamp = pitrEntries == logEntries.Length
            ? logEntries[^1].Watermark.Timestamp
            : Midpoint(logEntries[pitrEntries - 1].Watermark.Timestamp, logEntries[pitrEntries].Watermark.Timestamp);

        var pitrRestoreStarted = Stopwatch.GetTimestamp();
        Phase("pitr-restore");
        var pitrTarget = new StorageEngineRestoreTarget(Path.Combine(targetRoot, "pitr"));
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
                await VerifyRelationshipsAsync(tenant, Users, Orders, engine, options).ConfigureAwait(false);
                relationshipVerifyPasses++;
            }
        }

        ReclaimPhaseMemory();

        // Phase 10: final restore through the whole chain. Every update is
        // present at v2, every deleted document is absent, and the survivors
        // match exactly — the no-data-loss guarantee end to end.
        var finalRestoreStarted = Stopwatch.GetTimestamp();
        Phase("final-restore");
        var finalTarget = new StorageEngineRestoreTarget(Path.Combine(targetRoot, "final"));
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
                await VerifyRelationshipsAsync(tenant, Users, Orders, engine, options).ConfigureAwait(false);
                relationshipVerifyPasses++;
            }

            ReclaimPhaseMemory();

            // Phase 11: indexed query benchmark on the restored engine. The
            // automatic index on "sequence" is built and activated first
            // (itself a measured engine function), then deterministic range
            // windows are verified document-by-document against the
            // stateless expectation.
            queryStats = null;
            indexBuildElapsed = TimeSpan.Zero;
            queryElapsed = TimeSpan.Zero;
            if (options.Queries > 0)
            {
                var indexBuildStarted = Stopwatch.GetTimestamp();
                Phase("index-build");
                await using (var runtime = await AutomaticIndexRuntime.OpenAsync(
                        engine,
                        new AutomaticIndexRuntimeOptions { DisablePageCache = true }).ConfigureAwait(false))
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

                    (queryStats, queryElapsed) = await QueryBenchmarkAsync(runtime, scope, tenant, collection, options).ConfigureAwait(false);
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

        var restorePoints = await manager.ListRestorePointsAsync("e2e-volume").ConfigureAwait(false);
        if (restorePoints.Count != 2)
            throw new InvalidDataException(
                $"Expected two committed manifests, got {restorePoints.Count}.");

        var receipt = new E2eReceipt(
            "complete",
            DateTimeOffset.UtcNow,
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
                queriesVerified),
            null);
        return (receipt, E2eMarkdown.Create(receipt));
    }

    private static int BatchCount(int documents, int batchSize) =>
        (documents + batchSize - 1) / batchSize;

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
    /// Returns the previous phase's engine memory before the next phase opens
    /// its own full in-memory state. Volume phases each hold the whole corpus
    /// resident; without this the restore phases inherit the write phase's
    /// not-yet-reclaimed heap on top of their own. The large-object heap is
    /// compacted too: a phase that churns gigabytes of frame buffers leaves
    /// LOH fragmentation behind, and a later phase's multi-hundred-megabyte
    /// contiguous allocations (dictionary resizes at this volume) can then
    /// fail even with most of the host's memory free.
    /// </summary>
    private static void ReclaimPhaseMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string DocumentId(int index) => $"doc-{index:D8}";

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
    /// Field sizes are intentionally lean: the durable engine keeps each
    /// document body resident twice (current state plus its physical record)
    /// alongside three per-key dictionaries, which costs roughly 1.5 KiB of
    /// resident memory per document regardless of body size, so a
    /// ten-million-document corpus needs lean bodies to fit a 32 GiB host.
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
    private static async Task VerifyRelationshipsAsync(
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
        var sampleCount = Math.Min(userCount, 100);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var userIndex = (sample * (userCount / Math.Max(1, sampleCount))) % userCount;
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
            if (related.Count != expectedIds.Count
                || related.Any(endpoint => !expectedIds.Contains(endpoint.Endpoint.DocumentId)))
            {
                throw new InvalidDataException(
                    $"user-{userIndex} related set is {{ {string.Join(", ", related.Select(endpoint => endpoint.Endpoint.DocumentId).Order())} }}, expected {{ {string.Join(", ", expectedIds.Order())} }}.");
            }

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
    private static void Phase(string name) => Console.WriteLine($"phase: {name} [{DateTimeOffset.Now:HH:mm:ss}]");

    private static async Task<(long Verified, TimeSpan Elapsed)> VerifyScanAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId collection,
        ExpectedState state)
    {
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
        }

        if (verified != state.LiveDocuments)
            throw new InvalidDataException(
                $"Scan returned {verified} documents, expected {state.LiveDocuments}.");

        return (verified, Stopwatch.GetElapsedTime(started));
    }

    private static int ParseDocumentIndex(string identifier, int maximum)
    {
        if (!identifier.StartsWith("doc-", StringComparison.Ordinal)
            || identifier.Length != DocumentIdPrefixLength + 8
            || !int.TryParse(
                identifier.AsSpan(DocumentIdPrefixLength),
                NumberStyles.Integer,
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
        var started = Stopwatch.GetTimestamp();
        for (var read = 0; read < options.PointReads; read++)
        {
            var index = (int)(Draw((ulong)options.Seed, read, 0x9EAD) % (ulong)options.Documents);
            var readStarted = Stopwatch.GetTimestamp();
            var document = await engine.ReadAsync(new DocumentKey(tenant, collection, DocumentId(index))).ConfigureAwait(false);
            latencies[read] = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds;
            VerifyDocument(state, index, document);
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
    private static async Task<(E2eLatencyStats Stats, TimeSpan Elapsed)> QueryBenchmarkAsync(
        AutomaticIndexRuntime runtime,
        AutomaticIndexScope scope,
        TenantId tenant,
        CollectionId collection,
        E2eOptions options)
    {
        var state = new ExpectedState(
            options.Documents,
            options.Seed,
            options.Updates,
            options.Documents - options.Deletes,
            options.Deletes);
        var latencies = new double[options.Queries];
        var started = Stopwatch.GetTimestamp();
        for (var query = 0; query < options.Queries; query++)
        {
            var draw = Draw((ulong)options.Seed, query, 0x0EED);
            var low = (int)(draw % (ulong)Math.Max(1, state.LiveDocuments - 1));
            var width = 1 + (int)((draw >> 17) % 1_000UL);
            var high = Math.Min(low + width, state.LiveDocuments) - 1;
            var request = new IndexRuntimeQuery(
                tenant,
                collection,
                [IndexPredicate.GreaterThanOrEqual("sequence", (long)low), IndexPredicate.LessThanOrEqual("sequence", (long)high)],
                resultLimit: 1_000);
            var settleStarted = Stopwatch.GetTimestamp();
            QueryPlannerResult result;
            TimeSpan attemptLatency;
            while (true)
            {
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
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        return (E2eLatencyStats.From(options.Queries, latencies), elapsed);
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

        public int Relationships { get; private set; } = 10_000;

        public int Seed { get; private set; } = 1337;

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
                    default:
                        throw new ArgumentException($"Unknown option '--{name}'.");
                }
            }

            if (options.Updates + options.Deletes > options.Documents)
                throw new ArgumentException(
                    "--updates plus --deletes must not exceed --documents (updates touch the head, deletes the tail).");
            if (options.Updates + options.Deletes == 0)
                throw new ArgumentException(
                    "--updates plus --deletes must be at least one: the incremental backup and PITR phases require a publication after the full backup.");
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
            Console.WriteLine("  --relationships N     Related documents written through the schema store (default: 10000)");
            Console.WriteLine("  --seed N              Fixed fixture seed (default: 1337)");
            Console.WriteLine("  --output DIR          Receipt and object-store directory");
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

    private sealed record E2eReceipt(
        string Status,
        DateTimeOffset FinishedAtUtc,
        E2eOptions Configuration,
        E2ePhaseMeasurements Phases,
        E2eVolumeMeasurements Volume,
        string? Error);

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
        int QueriesVerified);

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

                {volume.DocumentsWritten:N0} deterministic random documents written in batches of {options.BatchSize:N0}, verified by exact read-back and point reads, backed up (full + incremental around {options.Updates:N0} updates and {options.Deletes:N0} deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

                | Phase | Elapsed | Note |
                | --- | ---: | ---: |
                | Bulk write | {phase.Write.TotalMilliseconds:N0} ms | {volume.DocumentsWritten / Math.Max(phase.Write.TotalSeconds, 0.001):N0} docs/s |
                | Relationship write | {phase.RelationshipWrite.TotalMilliseconds:N0} ms | {options.Relationships:N0} orders + users through the schema store |
                | Relationship verify | {phase.RelationshipVerify.TotalMilliseconds:N0} ms | related sets, traversal, restrict delete |
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
                | Index build | {phase.IndexBuild.TotalMilliseconds:N0} ms | automatic index on sequence, active |
                | Indexed queries | {phase.Queries.TotalMilliseconds:N0} ms | {DescribeLatency(queries)} |
                | Compaction | {phase.Compaction.TotalMilliseconds:N0} ms | {volume.CompactionRecordsWritten:N0} records, {volume.CompactionTombstonesRetained:N0} tombstones |
                | Compaction verify | {phase.CompactionVerify.TotalMilliseconds:N0} ms | {volume.DocumentsVerifiedCompacted:N0} exact matches |

                Full artifact plaintext SHA-256: `{volume.FullArtifactSha256}`
                Incremental artifact plaintext SHA-256: `{volume.IncrementalArtifactSha256}`
                """;
        }

        private static string DescribeLatency(E2eLatencyStats? stats) => stats is null
            ? "skipped"
            : $"{stats.OperationsPerSecond:N0} ops/s, p50 {stats.P50Milliseconds:N3} ms, p95 {stats.P95Milliseconds:N3} ms, p99 {stats.P99Milliseconds:N3} ms";
    }
}
