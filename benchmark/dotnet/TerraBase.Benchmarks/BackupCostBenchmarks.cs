using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using TerraBase.Continuity;
using TerraBase.Storage;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.Storage;

namespace TerraBase.Benchmarks;

public static class BackupCostBenchmarks
{
    internal const string Suite = "backup-cost";
    internal const string LocalRegion = "us-east-1";
    internal const double BytesPerMiB = 1024.0 * 1024.0;

    private const string RemoteRegion = "eu-west-1";
    private const int CommitsPerSegment = 8;
    private const int ManifestSegments = 10_000;

    private static readonly IReadOnlyDictionary<string, SqlValue> NoNamedParams = FrozenDictionary<string, SqlValue>.Empty;
    private static readonly TimeSpan IdleSettleWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SimulatedRegionalGetLatency = TimeSpan.FromMilliseconds(2);

    public static async Task RunAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ctx.IsSuiteSelected(Suite))
        {
            return;
        }

        if (ctx.ShouldRunBenchmark(Suite, "manifest_codec_10k_segments"))
        {
            RunManifestCodec10kSegments(ctx, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "archive_incremental_10k_manifest"))
        {
            await RunArchiveIncremental10kManifestAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "snapshot_churn_roundtrip"))
        {
            await RunSnapshotChurnRoundtripAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "pitr_restore_64_segments"))
        {
            await RunPitrRestore64SegmentsAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "retention_sweep_500_objects"))
        {
            await RunRetentionSweep500ObjectsAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "backup_status_manifest_poll_10k"))
        {
            await RunBackupStatusManifestPoll10kAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "tenant_idle_memory_100_dbs"))
        {
            await RunTenantIdleMemory100DbsAsync(ctx, cancellationToken);
        }
    }

    private static void RunManifestCodec10kSegments(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int warmup = 2;
        var databaseId = Ulid.NewUlid();
        var now = TimeProvider.System.GetUtcNow();
        var manifest = BuildManifest(databaseId, LocalRegion, ManifestSegments, now.AddHours(-6), anchorCoversSegments: false, segmentBytes: 65_536);
        var probe = new BackupCostProbe(null);
        var encodedBytes = 0L;

        var metrics = ctx.MeasureSync(
            Suite,
            "manifest_codec_10k_segments",
            warmup,
            20,
            index =>
            {
                var encoded = ManifestCodec.Serialize(manifest);
                var decoded = RequireDecoded(ManifestCodec.Deserialize(databaseId, encoded));
                BenchRunnerContext.AssertCondition(
                    decoded.Segments.Count == ManifestSegments && decoded.Snapshots.Count == 1,
                    "manifest_codec_10k_segments roundtrip lost segments or snapshots");
                encodedBytes = encoded.Length;
                if (index == warmup - 1)
                {
                    probe.Start();
                }
            },
            () => encodedBytes,
            "ManifestCodec.Serialize + Deserialize of a 10,000-segment ArchiveManifest",
            cancellationToken);
        _ = ctx.AttachBackupCost(metrics, probe.Finish(metrics, encodedManifestBytesPerOp: encodedBytes));
    }

    private static async Task RunArchiveIncremental10kManifestAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int warmup = 2;
        const int commitsPerCycle = 4;
        var databaseId = Ulid.NewUlid();
        var rawKey = NewRawKey();

        await using var server = CountingS3Server.Start(BackupCostRig.Bucket);
        await using var rig = BackupCostRig.Create(ctx, "archive-incremental-10k", server, 11, rawKey);
        await using var session = RequireCreated(await rig.Engine.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));
        var sequence = await ExecuteDdlAsync(session, databaseId, "CREATE TABLE events (id INTEGER PRIMARY KEY, body TEXT NOT NULL);", cancellationToken);

        var now = TimeProvider.System.GetUtcNow();
        var seeded = BuildManifest(databaseId, RemoteRegion, ManifestSegments, now.AddHours(-1), anchorCoversSegments: false, segmentBytes: 65_536);
        server.Seed(ArchiveKeys.Manifest(databaseId), ManifestCodec.Serialize(seeded), ManifestCodec.JsonContentType);

        var rowBody = RandomText(512);
        var probe = new BackupCostProbe(server);
        var metrics = await ctx.MeasureAsync(
            Suite,
            "archive_incremental_10k_manifest",
            warmup,
            12,
            async index =>
            {
                for (var step = 0; step < commitsPerCycle; step++)
                {
                    var written = RequireWrite(await session.ExecuteWriteAsync(
                        WriteRequest(databaseId, Statement("INSERT INTO events (id, body) VALUES (?, ?);", SqlValue.FromIntegral(sequence + 1), SqlValue.FromText(rowBody))),
                        cancellationToken));
                    sequence = written.Commit.Sequence;
                }

                var status = RequireBackupStatus(await rig.Continuity.ArchiveAsync(databaseId, forceSnapshot: false, cancellationToken));
                BenchRunnerContext.AssertCondition(
                    status.PendingSegments == 0 && status.ArchivedThrough.Get(LocalRegion) == sequence,
                    "archive_incremental_10k_manifest did not archive the new segment");
                if (index == warmup - 1)
                {
                    probe.Start();
                }
            },
            () => server.ObjectLength(ArchiveKeys.Manifest(databaseId)),
            "4 commits -> BackupContinuityService.ArchiveAsync(forceSnapshot: false) against a 10,000-segment manifest",
            cancellationToken);
        _ = ctx.AttachBackupCost(metrics, probe.Finish(metrics));
    }

    private static async Task RunSnapshotChurnRoundtripAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int warmup = 1;
        const int rowCount = 2_000;
        const int rowBytes = 2_048;
        const int insertBatch = 50;
        const int deleteBatch = 500;
        const int keptRows = rowCount / 5;
        var databaseId = Ulid.NewUlid();
        var rawKey = NewRawKey();

        await using var server = CountingS3Server.Start(BackupCostRig.Bucket);
        await using var rig = BackupCostRig.Create(ctx, "snapshot-churn", server, 12, rawKey);
        await using var session = RequireCreated(await rig.Engine.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));
        _ = await ExecuteDdlAsync(session, databaseId, "CREATE TABLE docs (id INTEGER PRIMARY KEY, body TEXT NOT NULL);", cancellationToken);

        for (var first = 1; first <= rowCount; first += insertBatch)
        {
            var statements = new SqlStatement[insertBatch];
            for (var offset = 0; offset < insertBatch; offset++)
            {
                statements[offset] = Statement("INSERT INTO docs (id, body) VALUES (?, ?);", SqlValue.FromIntegral(first + offset), SqlValue.FromText(RandomText(rowBytes)));
            }

            _ = RequireWrite(await session.ExecuteWriteAsync(WriteRequest(databaseId, statements), cancellationToken));
        }

        var deletedRows = 0L;
        for (var first = 1; first <= rowCount; first += deleteBatch)
        {
            var deleted = RequireWrite(await session.ExecuteWriteAsync(
                WriteRequest(databaseId, Statement("DELETE FROM docs WHERE id BETWEEN ? AND ? AND id % 5 != 0;", SqlValue.FromIntegral(first), SqlValue.FromIntegral(first + deleteBatch - 1))),
                cancellationToken));
            deletedRows += deleted.ResultSets[0].RowsAffected;
        }

        BenchRunnerContext.AssertCondition(deletedRows == rowCount - keptRows, "snapshot_churn_roundtrip did not delete 80% of rows");

        var rawBytes = LiveDatabaseBytes(rig.Restorer.Layout.DatabaseFile(databaseId));
        var storedBytes = 0L;
        var probe = new BackupCostProbe(server);
        var metrics = await ctx.MeasureAsync(
            Suite,
            "snapshot_churn_roundtrip",
            warmup,
            8,
            async index =>
            {
                var snapshot = RequireSnapshot(await rig.Engine.SnapshotAsync(databaseId, cancellationToken));
                var key = ArchiveKeys.Snapshot(databaseId, snapshot.TakenAtUtc, snapshot.Bookmark);
                RequireUploaded(await rig.Store.UploadFileAsync(key, snapshot.Path, cancellationToken));
                storedBytes = server.ObjectLength(key);

                var staging = ctx.RunDir("snapshot-churn-verify");
                _ = Directory.CreateDirectory(StorageLayout.DatabaseDirectoryIn(staging, databaseId));
                var downloaded = RequireDownloaded(await rig.Store.DownloadToFileAsync(key, StorageLayout.DatabaseFileIn(staging, databaseId), cancellationToken));
                BenchRunnerContext.AssertCondition(downloaded == storedBytes, "snapshot_churn_roundtrip downloaded size differs from the uploaded object");

                var rows = await CountRowsInCopyAsync(rig.StorageOptions with { RootDirectory = staging }, databaseId, rawKey, "SELECT COUNT(*) FROM docs;", cancellationToken);
                BenchRunnerContext.AssertCondition(rows == keptRows, "snapshot_churn_roundtrip restored snapshot has the wrong row count");

                File.Delete(snapshot.Path);
                Directory.Delete(staging, recursive: true);
                if (index == warmup - 1)
                {
                    probe.Start();
                }
            },
            () => storedBytes,
            "2,000 x 2 KiB rows, 80% deleted -> SnapshotAsync + S3 upload + download + SQLite row count",
            cancellationToken);
        _ = ctx.AttachBackupCost(metrics, probe.Finish(metrics, snapshotRawBytes: rawBytes, snapshotStoredBytes: storedBytes));
    }

    private static async Task RunPitrRestore64SegmentsAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int warmup = 1;
        const int segmentCount = 64;
        const int dataSegments = segmentCount - 1;
        const int expectedRows = dataSegments * CommitsPerSegment;
        var sourceId = Ulid.NewUlid();
        var rawKey = NewRawKey();

        await using var server = CountingS3Server.Start(BackupCostRig.Bucket);
        await using var restoreRig = BackupCostRig.Create(ctx, "pitr-restore-target", server, 14, rawKey);

        CausalBookmark restorePoint;
        long archivedBytes;
        await using (var sourceRig = BackupCostRig.Create(ctx, "pitr-restore-source", server, 13, rawKey))
        {
            await using var source = RequireCreated(await sourceRig.Engine.CreateAsync(new DatabaseCreateRequest(sourceId, rawKey, false), cancellationToken));
            var sequence = await ExecuteDdlAsync(source, sourceId, "CREATE TABLE events (id INTEGER PRIMARY KEY, body TEXT NOT NULL);", cancellationToken);
            _ = RequireBackupStatus(await sourceRig.Continuity.ArchiveNowAsync(sourceId, cancellationToken));

            var rowBody = RandomText(1_024);
            for (var segment = 0; segment < dataSegments; segment++)
            {
                for (var commit = 0; commit < CommitsPerSegment; commit++)
                {
                    var written = RequireWrite(await source.ExecuteWriteAsync(
                        WriteRequest(sourceId, Statement("INSERT INTO events (id, body) VALUES (?, ?);", SqlValue.FromIntegral(sequence + 1), SqlValue.FromText(rowBody))),
                        cancellationToken));
                    sequence = written.Commit.Sequence;
                }

                var status = RequireBackupStatus(await sourceRig.Continuity.ArchiveAsync(sourceId, forceSnapshot: false, cancellationToken));
                BenchRunnerContext.AssertCondition(status.PendingSegments == 0, "pitr_restore_64_segments seeding left pending segments");
            }

            restorePoint = source.CurrentBookmark;
            var manifest = RequireManifest(await sourceRig.Manifests.ReadCurrentAsync(sourceId, cancellationToken));
            BenchRunnerContext.AssertCondition(
                manifest.Snapshots.Count == 1 && manifest.Segments.Count == segmentCount,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"pitr_restore_64_segments seeded {manifest.Snapshots.Count} snapshots and {manifest.Segments.Count} segments instead of 1 and {segmentCount}"));
            archivedBytes = manifest.Snapshots.Sum(static item => item.SizeBytes) + manifest.Segments.Sum(static item => item.SizeBytes);
        }

        server.GetLatency = SimulatedRegionalGetLatency;
        var target = new RestoreTarget(restorePoint);
        var probe = new BackupCostProbe(server);
        var metrics = await ctx.MeasureAsync(
            Suite,
            "pitr_restore_64_segments",
            warmup,
            8,
            async index =>
            {
                var targetId = Ulid.NewUlid();
                var restored = RequireRestored(await restoreRig.Restorer.CopyAsync(Ulid.NewUlid(), sourceId, targetId, target, cancellationToken));
                BenchRunnerContext.AssertCondition(restored.Dominates(restorePoint), "pitr_restore_64_segments restored to an earlier bookmark");
                var rows = await CountRowsAsync(restoreRig.Engine, targetId, rawKey, "SELECT COUNT(*) FROM events;", cancellationToken);
                BenchRunnerContext.AssertCondition(rows == expectedRows, "pitr_restore_64_segments restored the wrong row count");
                RequireDeleted(await restoreRig.Engine.DeleteAsync(targetId, cancellationToken));
                if (index == warmup - 1)
                {
                    probe.Start();
                }
            },
            () => archivedBytes,
            "DatabaseRestorer.CopyAsync from snapshot + 64 archived segments with 2 ms simulated regional GET RTT",
            cancellationToken);
        _ = ctx.AttachBackupCost(metrics, probe.Finish(metrics));
    }

    private static async Task RunRetentionSweep500ObjectsAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int warmup = 1;
        const int iterations = 10;
        const int expiredSegments = 500;
        const int segmentBytes = 512;
        var totalOps = warmup + (ctx.Rounds * ctx.ScaleIterations(iterations));

        await using var server = CountingS3Server.Start(BackupCostRig.Bucket);
        await using var rig = BackupCostRig.Create(ctx, "retention-sweep", server, 15, NewRawKey());

        var now = TimeProvider.System.GetUtcNow();
        var cutoff = now.AddDays(-1);
        var segmentPayload = RandomNumberGenerator.GetBytes(segmentBytes);
        var snapshotPayload = RandomNumberGenerator.GetBytes(16_384);
        var databases = new Ulid[totalOps];
        var expiredKeys = new string[totalOps][];
        for (var op = 0; op < totalOps; op++)
        {
            var databaseId = Ulid.NewUlid();
            var manifest = BuildManifest(databaseId, LocalRegion, expiredSegments, now.AddDays(-2), anchorCoversSegments: true, segmentBytes);
            server.Seed(ArchiveKeys.Manifest(databaseId), ManifestCodec.Serialize(manifest), ManifestCodec.JsonContentType);
            server.Seed(manifest.Snapshots[0].Key, snapshotPayload, ManifestCodec.BinaryContentType);
            var keys = new string[manifest.Segments.Count];
            for (var index = 0; index < keys.Length; index++)
            {
                keys[index] = manifest.Segments[index].Key;
                server.Seed(keys[index], segmentPayload, ManifestCodec.BinaryContentType);
            }

            databases[op] = databaseId;
            expiredKeys[op] = keys;
        }

        var probe = new BackupCostProbe(server);
        var metrics = await ctx.MeasureAsync(
            Suite,
            "retention_sweep_500_objects",
            warmup,
            iterations,
            async index =>
            {
                var published = RequireManifest(await rig.Manifests.PublishAsync(databases[index], [], null, cutoff, cancellationToken));
                BenchRunnerContext.AssertCondition(
                    published.Segments.Count == 0 && published.Snapshots.Count == 1,
                    "retention_sweep_500_objects did not retire every expired segment");
                foreach (var key in expiredKeys[index])
                {
                    BenchRunnerContext.AssertCondition(server.ObjectLength(key) < 0, "retention_sweep_500_objects left an expired segment object behind");
                }

                if (index == warmup - 1)
                {
                    probe.Start();
                }
            },
            () => (long)expiredSegments * segmentBytes,
            "ArchiveManifests.PublishAsync with retention cutoff retiring 500 expired segment objects behind 1 anchor snapshot",
            cancellationToken);
        _ = ctx.AttachBackupCost(metrics, probe.Finish(metrics));
    }

    private static async Task RunBackupStatusManifestPoll10kAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int warmup = 3;
        var databaseId = Ulid.NewUlid();
        var rawKey = NewRawKey();

        await using var server = CountingS3Server.Start(BackupCostRig.Bucket);
        await using var rig = BackupCostRig.Create(ctx, "backup-status-poll", server, 16, rawKey);
        await using var session = RequireCreated(await rig.Engine.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));
        _ = await ExecuteDdlAsync(session, databaseId, "CREATE TABLE events (id INTEGER PRIMARY KEY, body TEXT NOT NULL);", cancellationToken);

        var now = TimeProvider.System.GetUtcNow();
        var manifest = BuildManifest(databaseId, LocalRegion, ManifestSegments, now.AddHours(-6), anchorCoversSegments: false, segmentBytes: 65_536);
        var encoded = ManifestCodec.Serialize(manifest);
        server.Seed(ArchiveKeys.Manifest(databaseId), encoded, ManifestCodec.JsonContentType);
        var expectedThrough = manifest.Segments[^1].LastSequence;

        var probe = new BackupCostProbe(server);
        var metrics = await ctx.MeasureAsync(
            Suite,
            "backup_status_manifest_poll_10k",
            warmup,
            40,
            async index =>
            {
                var status = RequireBackupStatus(await rig.Continuity.GetStatusAsync(databaseId, cancellationToken));
                BenchRunnerContext.AssertCondition(
                    status.ArchivedThrough.Get(LocalRegion) == expectedThrough && status.PendingSegments == 0,
                    "backup_status_manifest_poll_10k reported the wrong archived watermark");
                if (index == warmup - 1)
                {
                    probe.Start();
                }
            },
            () => encoded.Length,
            "BackupContinuityService.GetStatusAsync polling an unchanged 10,000-segment manifest",
            cancellationToken);
        _ = ctx.AttachBackupCost(metrics, probe.Finish(metrics));
    }

    private static async Task RunTenantIdleMemory100DbsAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int databaseCount = 100;
        const int rowsPerDatabase = 20;
        const int queriesPerDatabase = 3;
        var directory = ctx.RunDir("tenant-idle-memory");
        var rawKey = NewRawKey();
        var ids = new Ulid[databaseCount];
        var storage = new StorageEngineOptions(directory, LocalRegion, 17, 17, new DeltaColumnRegistry()) { FsyncCommitLog = false };
        await using var engine = new SqliteStorageEngine(storage, TimeProvider.System);

        var rowBody = RandomText(256);
        for (var db = 0; db < databaseCount; db++)
        {
            ids[db] = Ulid.NewUlid();
            await using var created = RequireCreated(await engine.CreateAsync(new DatabaseCreateRequest(ids[db], rawKey, false), cancellationToken));
            _ = await ExecuteDdlAsync(created, ids[db], "CREATE TABLE items (id INTEGER PRIMARY KEY, body TEXT NOT NULL);", cancellationToken);
            var statements = new SqlStatement[rowsPerDatabase];
            for (var row = 0; row < rowsPerDatabase; row++)
            {
                statements[row] = Statement("INSERT INTO items (id, body) VALUES (?, ?);", SqlValue.FromIntegral(row + 1), SqlValue.FromText(rowBody));
            }

            _ = RequireWrite(await created.ExecuteWriteAsync(WriteRequest(ids[db], statements), cancellationToken));
        }

        CollectMemory();
        var baseline = ctx.CaptureSnapshot();
        var sessions = new List<ISqlDatabaseSession>(databaseCount);
        try
        {
            for (var db = 0; db < databaseCount; db++)
            {
                sessions.Add(RequireOpened(await engine.OpenAsync(new DatabaseOpenRequest(ids[db], rawKey), cancellationToken)));
            }

            var queryStart = Stopwatch.GetTimestamp();
            for (var db = 0; db < databaseCount; db++)
            {
                var session = sessions[db];
                var count = RequireRead(await session.ExecuteReadAsync(ReadRequest(ids[db], Statement("SELECT COUNT(*) FROM items;")), cancellationToken));
                BenchRunnerContext.AssertCondition(
                    count.ResultSets[0].Rows[0][0].TryGetIntegral(out var rows) && rows == rowsPerDatabase,
                    "tenant_idle_memory_100_dbs count query returned the wrong row count");
                var point = RequireRead(await session.ExecuteReadAsync(ReadRequest(ids[db], Statement("SELECT id, body FROM items WHERE id = ?;", SqlValue.FromIntegral((db % rowsPerDatabase) + 1))), cancellationToken));
                BenchRunnerContext.AssertCondition(point.ResultSets[0].Rows.Count == 1, "tenant_idle_memory_100_dbs point query missed its row");
                var scan = RequireRead(await session.ExecuteReadAsync(ReadRequest(ids[db], Statement("SELECT SUM(LENGTH(body)) FROM items;")), cancellationToken));
                BenchRunnerContext.AssertCondition(scan.ResultSets[0].Rows.Count == 1, "tenant_idle_memory_100_dbs scan query returned no rows");
            }

            var queryElapsed = Stopwatch.GetElapsedTime(queryStart).TotalSeconds;
            var queryOps = databaseCount * queriesPerDatabase;
            CollectMemory();
            var warm = ctx.CaptureSnapshot();

            await Task.Delay(IdleSettleWindow, TimeProvider.System, cancellationToken);
            foreach (var session in sessions)
            {
                _ = SqliteStorageEngine.TrimIdleResources(session, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TimeProvider.System, cancellationToken);
            CollectMemory();
            var idle = ctx.CaptureSnapshot();

            var warmRssDeltaMiB = (warm.RssBytes - baseline.RssBytes) / BytesPerMiB;
            var idleRssDeltaMiB = (idle.RssBytes - baseline.RssBytes) / BytesPerMiB;
            var warmFdDelta = warm.OpenFdCount - baseline.OpenFdCount;
            var idleFdDelta = idle.OpenFdCount - baseline.OpenFdCount;
            var idleKiBPerDb = (idle.RssBytes - baseline.RssBytes) / 1024.0 / databaseCount;
            var nativeIdle = Math.Max(0L, idle.RssBytes - idle.ManagedHeapBytes);

            var metrics = ctx.RecordSnapshotMetric(
                Suite,
                "tenant_idle_memory_100_dbs",
                opsPerSec: queryElapsed > 0 ? queryOps / queryElapsed : 0,
                meanUs: idleKiBPerDb,
                workingSetMiB: idle.RssBytes / BytesPerMiB,
                diskBytes: BenchRunnerContext.DirectorySizeBytes(directory),
                note: string.Create(
                    CultureInfo.InvariantCulture,
                    $"dbs={databaseCount} queries={queryOps} warm_rss_delta={warmRssDeltaMiB:F1}MiB idle_rss_delta={idleRssDeltaMiB:F1}MiB warm_fd_delta={warmFdDelta} idle_fd_delta={idleFdDelta} idle_kib_per_db={idleKiBPerDb:F1} idle_settle={IdleSettleWindow.TotalSeconds:F0}s"),
                managedHeapMiB: idle.ManagedHeapBytes / BytesPerMiB,
                nativeRssMiB: nativeIdle / BytesPerMiB,
                openFdCount: idle.OpenFdCount,
                openFdDelta: idleFdDelta,
                unitsPerOp: databaseCount,
                unitLabel: "kib/db");
            _ = ctx.AttachBackupCost(metrics, new BackupCostMetrics(
                S3GetCount: 0,
                S3PutCount: 0,
                S3DeleteCount: 0,
                S3DeleteBatchCount: 0,
                S3ListCount: 0,
                S3NotModifiedCount: 0,
                S3ConflictCount: 0,
                UploadedBytes: 0,
                DownloadedBytes: 0,
                ManifestBytesUploaded: 0,
                SnapshotRawBytes: 0,
                SnapshotStoredBytes: 0,
                ObjectsDeleted: 0,
                RssDeltaMiB: idleRssDeltaMiB,
                OpenFdDelta: idleFdDelta));
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }

    private static ArchiveManifest BuildManifest(
        Ulid databaseId,
        string origin,
        int segmentCount,
        DateTimeOffset snapshotTakenAt,
        bool anchorCoversSegments,
        long segmentBytes)
    {
        var firstSequence = anchorCoversSegments ? 1L : CommitsPerSegment + 1L;
        var snapshotSequence = anchorCoversSegments ? (long)segmentCount * CommitsPerSegment : CommitsPerSegment;
        var segmentsStart = anchorCoversSegments
            ? snapshotTakenAt.AddDays(-28)
            : snapshotTakenAt.AddSeconds(1);
        var step = anchorCoversSegments ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(100);

        var bookmark = CausalBookmark.ForRegion(origin, snapshotSequence);
        var snapshot = new ArchivedSnapshot(
            ArchiveKeys.Snapshot(databaseId, snapshotTakenAt, bookmark),
            bookmark.Encode(),
            1,
            4L * 1024 * 1024,
            snapshotTakenAt);

        var segments = new ArchivedSegment[segmentCount];
        for (var index = 0; index < segmentCount; index++)
        {
            var first = firstSequence + ((long)index * CommitsPerSegment);
            var last = first + CommitsPerSegment - 1;
            var firstCommitted = segmentsStart + (step * index);
            var lastCommitted = firstCommitted + (step / 2);
            segments[index] = new ArchivedSegment(
                ArchiveKeys.Segment(databaseId, origin, first, last),
                origin,
                first,
                last,
                segmentBytes,
                lastCommitted,
                firstCommitted,
                lastCommitted);
        }

        return new ArchiveManifest(ArchiveManifest.CurrentVersion, databaseId.ToString(), [snapshot], segments, [], segmentsStart + (step * segmentCount));
    }

    private static async Task<long> ExecuteDdlAsync(ISqlDatabaseSession session, Ulid databaseId, string sql, CancellationToken cancellationToken)
    {
        var written = RequireWrite(await session.ExecuteWriteAsync(
            new SqlStatementRequest(databaseId, [Statement(sql)], SqlAccess.Write, ConsistencyMode.Strong, CausalBookmark.Empty),
            cancellationToken));
        return written.Commit.Sequence;
    }

    private static async Task<long> CountRowsAsync(SqliteStorageEngine engine, Ulid databaseId, ReadOnlyMemory<byte> rawKey, string sql, CancellationToken cancellationToken)
    {
        await using var session = RequireOpened(await engine.OpenAsync(new DatabaseOpenRequest(databaseId, rawKey), cancellationToken));
        var read = RequireRead(await session.ExecuteReadAsync(ReadRequest(databaseId, Statement(sql)), cancellationToken));
        if (read.ResultSets is [{ Rows: [[var value, ..], ..] }, ..] && value.TryGetIntegral(out var count))
        {
            return count;
        }

        throw new InvalidOperationException("row count query returned no integral value");
    }

    private static async Task<long> CountRowsInCopyAsync(StorageEngineOptions options, Ulid databaseId, ReadOnlyMemory<byte> rawKey, string sql, CancellationToken cancellationToken)
    {
        await using var engine = new SqliteStorageEngine(options, TimeProvider.System);
        return await CountRowsAsync(engine, databaseId, rawKey, sql, cancellationToken);
    }

    private static long LiveDatabaseBytes(string databaseFile)
    {
        var main = new FileInfo(databaseFile);
        var wal = new FileInfo(string.Concat(databaseFile, "-wal"));
        return (main.Exists ? main.Length : 0) + (wal.Exists ? wal.Length : 0);
    }

    private static void CollectMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static byte[] NewRawKey() => RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

    private static string RandomText(int length) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(((length * 3) / 4) + 3))[..length];

    private static SqlStatement Statement(string sql, params SqlValue[] values) => new(sql, values, NoNamedParams);

    private static SqlStatementRequest WriteRequest(Ulid databaseId, params SqlStatement[] statements) =>
        new(databaseId, statements, SqlAccess.Write, ConsistencyMode.Fast, CausalBookmark.Empty);

    private static SqlStatementRequest ReadRequest(Ulid databaseId, SqlStatement statement) =>
        new(databaseId, [statement], SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);

    private static ISqlDatabaseSession RequireCreated(CreateDatabaseOutcome outcome)
    {
        var (session, error) = outcome switch
        {
            ISqlDatabaseSession created => (created, (string?)null),
            DatabaseAlreadyExists exists => ((ISqlDatabaseSession?)null, exists.Message),
            InvalidDatabaseRequest invalid => ((ISqlDatabaseSession?)null, invalid.Message),
            StorageBusy busy => ((ISqlDatabaseSession?)null, busy.Message),
        };
        return Ready(session, error);
    }

    private static ISqlDatabaseSession RequireOpened(OpenDatabaseOutcome outcome)
    {
        var (session, error) = outcome switch
        {
            ISqlDatabaseSession opened => (opened, (string?)null),
            DatabaseNotFound missing => ((ISqlDatabaseSession?)null, missing.Message),
            InvalidDatabaseRequest invalid => ((ISqlDatabaseSession?)null, invalid.Message),
            StorageCorrupted corrupted => ((ISqlDatabaseSession?)null, corrupted.Message),
            StorageBusy busy => ((ISqlDatabaseSession?)null, busy.Message),
        };
        return Ready(session, error);
    }

    private static SqlWriteResult RequireWrite(SqlWriteOutcome outcome)
    {
        var (result, error) = outcome switch
        {
            SqlWriteResult written => (written, (string?)null),
            SqlError sql => ((SqlWriteResult?)null, sql.Message),
            StrongRequired strong => ((SqlWriteResult?)null, string.Concat("strong required: ", strong.Reason.ToString())),
            SchemaMismatch mismatch => ((SqlWriteResult?)null, string.Concat("schema mismatch: ", mismatch.ActualEpoch.ToString(CultureInfo.InvariantCulture))),
            StorageBusy busy => ((SqlWriteResult?)null, busy.Message),
            StorageTimeout timeout => ((SqlWriteResult?)null, timeout.Message),
        };
        return Ready(result, error);
    }

    private static SqlReadResult RequireRead(SqlReadOutcome outcome)
    {
        var (result, error) = outcome switch
        {
            SqlReadResult read => (read, (string?)null),
            SqlError sql => ((SqlReadResult?)null, sql.Message),
            StorageBusy busy => ((SqlReadResult?)null, busy.Message),
            StorageTimeout timeout => ((SqlReadResult?)null, timeout.Message),
        };
        return Ready(result, error);
    }

    private static DatabaseSnapshot RequireSnapshot(SnapshotDatabaseOutcome outcome)
    {
        var (snapshot, error) = outcome switch
        {
            DatabaseSnapshot taken => (taken, (string?)null),
            DatabaseNotFound missing => ((DatabaseSnapshot?)null, missing.Message),
            StorageBusy busy => ((DatabaseSnapshot?)null, busy.Message),
        };
        return Ready(snapshot, error);
    }

    private static void RequireDeleted(DeleteDatabaseOutcome outcome)
    {
        var error = outcome switch
        {
            DatabaseDeleted => (string?)null,
            DatabaseNotFound missing => missing.Message,
            StorageBusy busy => busy.Message,
        };
        Fail(error);
    }

    private static void RequireUploaded(ArchiveWriteOutcome outcome)
    {
        var error = outcome switch
        {
            ArchiveObjectWritten => (string?)null,
            ArchiveUnavailable unavailable => unavailable.Message,
            ArchiveTimedOut timeout => timeout.Message,
        };
        Fail(error);
    }

    private static long RequireDownloaded(ArchiveDownloadOutcome outcome)
    {
        var (length, error) = outcome switch
        {
            long bytes => (bytes, (string?)null),
            ArchiveObjectMissing missing => (-1L, missing.Message),
            ArchiveObjectTooLarge oversized => (-1L, string.Concat("archive object exceeds download limit: ", oversized.Key)),
            ArchiveUnavailable unavailable => (-1L, unavailable.Message),
            ArchiveTimedOut timeout => (-1L, timeout.Message),
        };
        Fail(error);
        return length;
    }

    private static ArchiveManifest RequireManifest(ArchiveManifestOutcome outcome)
    {
        var (manifest, error) = outcome switch
        {
            ArchiveManifest loaded => (loaded, (string?)null),
            ContinuityUnavailable unavailable => ((ArchiveManifest?)null, unavailable.Message),
        };
        return Ready(manifest, error);
    }

    private static ArchiveManifest RequireDecoded(ArchiveManifestDecodeOutcome outcome)
    {
        var (manifest, error) = outcome switch
        {
            ArchiveManifest decoded => (decoded, (string?)null),
            ArchiveManifestCorrupted corrupted => ((ArchiveManifest?)null, corrupted.Message),
        };
        return Ready(manifest, error);
    }

    private static CausalBookmark RequireRestored(ContinuityCopyOutcome outcome)
    {
        var (bookmark, error) = outcome switch
        {
            CausalBookmark restored => (restored, (string?)null),
            OperationFailed failed => ((CausalBookmark?)null, failed.Message),
        };
        return Ready(bookmark, error);
    }

    private static BackupStatus RequireBackupStatus(BackupStatusOutcome outcome)
    {
        var (status, error) = outcome switch
        {
            BackupStatus ok => (ok, (string?)null),
            ContinuityNotFound missing => ((BackupStatus?)null, missing.Message),
            ContinuityUnavailable unavailable => ((BackupStatus?)null, unavailable.Message),
        };
        return Ready(status, error);
    }

    private static T Ready<T>(T? value, string? error) where T : class
    {
        Fail(error);
        return value ?? throw new InvalidOperationException("benchmark outcome carried neither a value nor an error");
    }

    private static void Fail(string? error)
    {
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }
}
