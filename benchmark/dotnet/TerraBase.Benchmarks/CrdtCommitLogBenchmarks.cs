using System.Globalization;
using TerraBase.Storage;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Storage;

namespace TerraBase.Benchmarks;

public static class CrdtCommitLogBenchmarks
{
    private const string PayloadText = "crdt-commitlog-benchmark-payload-0123456789abcdefghijklmnopqrstuvwxyz-0123456789";

    public static async Task RunAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.ShouldRun("crdt"))
        {
            if (ctx.ShouldRunBenchmark("crdt", "preupdate_and_stamp_100"))
            {
                await RunPreupdateAndStamp100Async(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("crdt", "apply_remote_batch_50"))
            {
                await RunApplyRemoteBatch50Async(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("crdt", "apply_remote_conflict_merge_50"))
            {
                await RunApplyRemoteConflictMerge50Async(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("crdt", "mutation_codec_roundtrip"))
            {
                RunMutationCodecRoundtrip(ctx, cancellationToken);
            }
        }

        if (ctx.ShouldRun("commitlog"))
        {
            if (ctx.ShouldRunBenchmark("commitlog", "commitlog_frame_seal_open"))
            {
                await RunCommitLogFrameSealOpenAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("commitlog", "commitlog_append_read_tail"))
            {
                await RunCommitLogAppendReadTailAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("commitlog", "version_vector_snapshot_satisfies"))
            {
                await RunVersionVectorSnapshotSatisfiesAsync(ctx, cancellationToken);
            }
        }
    }

    private static async Task RunPreupdateAndStamp100Async(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int RowsPerBatch = 100;
        var dir = ctx.RunDir("preupdate_and_stamp_100");
        var key = StorageBenchmarks.CreateRawKey();
        var dbId = Ulid.NewUlid();
        var deltaColumns = new DeltaColumnRegistry();
        deltaColumns.Register(dbId, "items", "score");
        await using var engine = StorageBenchmarks.CreateEngine(dir, fsyncCommitLog: false, deltaColumns: deltaColumns);
        await using var session = await StorageBenchmarks.CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await StorageBenchmarks.CreateItemsTableAsync(session, dbId, cancellationToken);

        var expectedRows = 0L;
        _ = await ctx.MeasureAsync(
            "crdt",
            "preupdate_and_stamp_100",
            warmup: 3,
            iterations: 40,
            async index =>
            {
                var baseId = (index * RowsPerBatch) + 1L;
                var statements = new SqlStatement[RowsPerBatch];
                for (var item = 0; item < RowsPerBatch; item++)
                {
                    var id = baseId + item;
                    statements[item] = StorageBenchmarks.Statement(
                        "INSERT INTO items (id, name, score, payload) VALUES (?1, ?2, ?3, ?4)",
                        SqlValue.FromIntegral(id),
                        SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"stamp-{id}")),
                        SqlValue.FromIntegral(id),
                        SqlValue.FromText(PayloadText));
                }

                var result = await StorageBenchmarks.ExecuteWriteAsync(session, dbId, statements, ConsistencyMode.Fast, cancellationToken);
                BenchRunnerContext.AssertCondition(
                    result.Commit.Sequence > 0
                    && MutationBatchCodec.TryDecode(result.Commit.DeltaPayload.Span, out var decoded)
                    && decoded.Mutations.Count == RowsPerBatch,
                    "preupdate_and_stamp_100 did not capture and stamp 100 rows");
                expectedRows += RowsPerBatch;
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "100 rows/op; FsyncCommitLog=false",
            cancellationToken: cancellationToken);

        var count = await StorageBenchmarks.CountRowsAsync(session, dbId, "items", cancellationToken);
        BenchRunnerContext.AssertCondition(count == expectedRows, string.Create(CultureInfo.InvariantCulture, $"preupdate_and_stamp_100 row count {count} != {expectedRows}"));
    }

    private static async Task RunApplyRemoteBatch50Async(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int BatchSize = 50;
        const string RemoteOrigin = "gru";
        const string LocalOrigin = "iad";
        var dir = ctx.RunDir("apply_remote_batch_50");
        var key = StorageBenchmarks.CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = StorageBenchmarks.CreateEngine(dir, fsyncCommitLog: false, region: LocalOrigin, nodeId: 2);
        await using var session = await StorageBenchmarks.CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await StorageBenchmarks.CreateItemsTableAsync(session, dbId, cancellationToken);

        var remoteClock = new HlcClock(nodeId: 1, TimeProvider.System);
        var schemaEpoch = session.SchemaEpoch;
        var nextSequence = 1L;
        var nextRowId = 1L;
        var totalApplied = 0L;

        _ = await ctx.MeasureAsync(
            "crdt",
            "apply_remote_batch_50",
            warmup: 3,
            iterations: 40,
            async _ =>
            {
                var commits = new CommitMetadata[BatchSize];
                for (var item = 0; item < BatchSize; item++)
                {
                    var rowId = nextRowId++;
                    var seq = nextSequence++;
                    var mutation = new RowMutation(
                        "items",
                        MutationOp.Insert,
                        1,
                        [SqlValue.FromIntegral(rowId)],
                        [
                            new ColumnChange("name", ChangeKind.Assign, 0, SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"remote-{rowId}")), 0),
                            new ColumnChange("score", ChangeKind.Assign, 0, SqlValue.FromIntegral(rowId * 2L), 0),
                            new ColumnChange("payload", ChangeKind.Assign, 0, SqlValue.FromText(PayloadText), 0),
                        ]);
                    var payload = MutationBatchCodec.Encode(new DeltaBatch([], [mutation]));
                    commits[item] = new CommitMetadata(
                        Ulid.NewUlid(),
                        RemoteOrigin,
                        seq,
                        remoteClock.Tick(),
                        schemaEpoch,
                        ConsistencyMode.Fast,
                        CausalBookmark.ForRegion(RemoteOrigin, seq),
                        payload);
                }

                var applied = await ApplyRemoteBatchAsync(session, new RemoteCommitBatch(dbId, commits), cancellationToken);
                BenchRunnerContext.AssertCondition(
                    applied.Applied == BatchSize && applied.Duplicates == 0 && applied.Buffered == 0 && applied.Quarantined == 0,
                    string.Create(CultureInfo.InvariantCulture, $"apply_remote_batch_50 expected 50 applied, got applied={applied.Applied} dup={applied.Duplicates} buf={applied.Buffered} quar={applied.Quarantined}"));
                totalApplied += applied.Applied;
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "50 commits/op; FsyncCommitLog=false",
            cancellationToken: cancellationToken);

        var count = await StorageBenchmarks.CountRowsAsync(session, dbId, "items", cancellationToken);
        BenchRunnerContext.AssertCondition(count == totalApplied, string.Create(CultureInfo.InvariantCulture, $"apply_remote_batch_50 row count {count} != {totalApplied}"));
    }

    private static async Task RunApplyRemoteConflictMerge50Async(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int RowCount = 50;
        const long DeltaStep = 3L;
        const string RemoteOrigin = "gru";
        const string LocalOrigin = "iad";
        var dir = ctx.RunDir("apply_remote_conflict_merge_50");
        var key = StorageBenchmarks.CreateRawKey();
        var dbId = Ulid.NewUlid();
        var deltaColumns = new DeltaColumnRegistry();
        deltaColumns.Register(dbId, "items", "score");
        await using var engine = StorageBenchmarks.CreateEngine(dir, fsyncCommitLog: false, region: LocalOrigin, nodeId: 2, deltaColumns: deltaColumns);
        await using var session = await StorageBenchmarks.CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await StorageBenchmarks.CreateItemsTableAsync(session, dbId, cancellationToken);
        await StorageBenchmarks.SeedItemsAsync(session, dbId, RowCount, batchSize: RowCount, cancellationToken);

        var basePhysicalMs = engine.Clock.Tick().WallMs;
        var schemaEpoch = session.SchemaEpoch;
        var nextSequence = 1L;
        var totalIterations = 0L;

        _ = await ctx.MeasureAsync(
            "crdt",
            "apply_remote_conflict_merge_50",
            warmup: 3,
            iterations: 40,
            async iteration =>
            {
                var commits = new CommitMetadata[RowCount];
                for (var item = 0; item < RowCount; item++)
                {
                    var rowId = item + 1L;
                    var seq = nextSequence++;
                    var higherHlc = ((item + iteration) & 1) == 0;
                    var commitHlc = higherHlc
                        ? new HlcTimestamp(basePhysicalMs + 10_000L + seq, 0, 1)
                        : new HlcTimestamp(Math.Max(1L, basePhysicalMs - 10_000L), (ushort)(item + 1), 1);

                    var mutation = new RowMutation(
                        "items",
                        MutationOp.Update,
                        1,
                        [SqlValue.FromIntegral(rowId)],
                        [
                            new ColumnChange("name", ChangeKind.Assign, 0, SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"merge-{seq}")), 0),
                            new ColumnChange("score", ChangeKind.DeltaOp, 0, SqlValue.Null, DeltaStep),
                            new ColumnChange("payload", ChangeKind.Assign, 0, SqlValue.FromText(PayloadText), 0),
                        ]);
                    var payload = MutationBatchCodec.Encode(new DeltaBatch([], [mutation]));
                    commits[item] = new CommitMetadata(
                        Ulid.NewUlid(),
                        RemoteOrigin,
                        seq,
                        commitHlc,
                        schemaEpoch,
                        ConsistencyMode.Fast,
                        CausalBookmark.ForRegion(RemoteOrigin, seq),
                        payload);
                }

                var applied = await ApplyRemoteBatchAsync(session, new RemoteCommitBatch(dbId, commits), cancellationToken);
                BenchRunnerContext.AssertCondition(
                    applied.Applied == RowCount && applied.Duplicates == 0 && applied.Buffered == 0 && applied.Quarantined == 0,
                    string.Create(CultureInfo.InvariantCulture, $"apply_remote_conflict_merge_50 expected 50 applied, got applied={applied.Applied} dup={applied.Duplicates} buf={applied.Buffered} quar={applied.Quarantined}"));
                totalIterations++;
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "50 rows Assign+DeltaOp alternating HLC conflict merge; FsyncCommitLog=false",
            cancellationToken: cancellationToken,
            unitsPerOp: RowCount,
            unitLabel: "merges");

        var count = await StorageBenchmarks.CountRowsAsync(session, dbId, "items", cancellationToken);
        BenchRunnerContext.AssertCondition(count == RowCount, string.Create(CultureInfo.InvariantCulture, $"apply_remote_conflict_merge_50 row count {count} != {RowCount}"));

        var expectedScore = 10L + (totalIterations * DeltaStep);
        var check = await StorageBenchmarks.ExecuteReadAsync(
            session,
            dbId,
            [StorageBenchmarks.Statement("SELECT score FROM items WHERE id = 1")],
            cancellationToken);
        BenchRunnerContext.AssertCondition(
            check.ResultSets[0].Rows.Count == 1
            && check.ResultSets[0].Rows[0][0].TryGetIntegral(out var actualScore)
            && actualScore == expectedScore,
            string.Create(CultureInfo.InvariantCulture, $"apply_remote_conflict_merge_50 delta score mismatch, expected {expectedScore}"));
    }

    private static void RunMutationCodecRoundtrip(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var batch = BuildSampleDeltaBatch(50);
        var encodedSize = MutationBatchCodec.Encode(batch).Length;

        _ = ctx.MeasureSync(
            "crdt",
            "mutation_codec_roundtrip",
            warmup: 100,
            iterations: 5000,
            _ =>
            {
                var bytes = MutationBatchCodec.Encode(batch);
                var ok = MutationBatchCodec.TryDecode(bytes, out var decoded);
                BenchRunnerContext.AssertCondition(
                    ok && bytes.Length == encodedSize && decoded.Mutations.Count == batch.Mutations.Count,
                    "mutation_codec_roundtrip failed decode verification");
            },
            note: string.Create(CultureInfo.InvariantCulture, $"50 mutations/op; payload_b={encodedSize}"),
            cancellationToken: cancellationToken);
    }

    private static async Task RunCommitLogFrameSealOpenAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int CommitsPerSegment = 25;
        var sourceDir = ctx.RunDir("commitlog_frame_seal_src");
        var targetDir = ctx.RunDir("commitlog_frame_seal_dst");
        var key = StorageBenchmarks.CreateRawKey();
        var dbId = Ulid.NewUlid();

        await using var sourceEngine = StorageBenchmarks.CreateEngine(sourceDir, fsyncCommitLog: false, region: "gru", nodeId: 1);
        await using var targetEngine = StorageBenchmarks.CreateEngine(targetDir, fsyncCommitLog: false, region: "iad", nodeId: 2);
        await using var sourceSession = await StorageBenchmarks.CreateDatabaseAsync(sourceEngine, dbId, key, cancellationToken);
        await using var targetSession = await StorageBenchmarks.CreateDatabaseAsync(targetEngine, dbId, key, cancellationToken);

        await StorageBenchmarks.CreateItemsTableAsync(sourceSession, dbId, cancellationToken);
        for (var i = 1; i <= CommitsPerSegment; i++)
        {
            _ = await StorageBenchmarks.ExecuteWriteAsync(
                sourceSession,
                dbId,
                [StorageBenchmarks.Statement(
                    "INSERT INTO items (id, name, score, payload) VALUES (?1, ?2, ?3, ?4)",
                    SqlValue.FromIntegral(i),
                    SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"seg-{i}")),
                    SqlValue.FromIntegral(i),
                    SqlValue.FromText(PayloadText))],
                ConsistencyMode.Fast,
                cancellationToken);
        }

        SqliteStorageEngine.SealCommittedLogs(sourceSession);
        var segments = await ListSealedSegmentsAsync(sourceSession, "gru", 0, cancellationToken);
        BenchRunnerContext.AssertCondition(segments.Count > 0, "commitlog_frame_seal_open produced no sealed segment");
        var segment = segments[^1];

        byte[] segmentBytes;
        await using (var opened = await OpenSegmentAsync(sourceSession, segment, cancellationToken))
        {
            using var copy = new MemoryStream((int)segment.SizeBytes);
            await opened.CopyToAsync(copy, cancellationToken);
            segmentBytes = copy.ToArray();
        }

        var expectedFrames = (int)(segment.LastSequence - segment.FirstSequence + 1);
        _ = await ctx.MeasureAsync(
            "commitlog",
            "commitlog_frame_seal_open",
            warmup: 10,
            iterations: 200,
            async _ =>
            {
                var decoded = 0;
                using var stream = new MemoryStream(segmentBytes, writable: false);
                var status = await ReadArchivedSegmentAsync(
                    targetSession,
                    segment,
                    stream,
                    (commits, _) =>
                    {
                        decoded += commits.Count;
                        return ValueTask.FromResult(true);
                    },
                    cancellationToken);
                BenchRunnerContext.AssertCondition(
                    status && decoded == expectedFrames,
                    string.Create(CultureInfo.InvariantCulture, $"commitlog_frame_seal_open decoded {decoded} != {expectedFrames}"));
            },
            diskBytes: () => segmentBytes.Length,
            note: string.Create(CultureInfo.InvariantCulture, $"ReadArchivedSegmentAsync open+decrypt+CRC32C (sealed once in setup); {expectedFrames} frames/op; segment_b={segmentBytes.Length}; AES-GCM+CRC32C"),
            cancellationToken: cancellationToken);
    }

    private static async Task RunCommitLogAppendReadTailAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var dir = ctx.RunDir("commitlog_append_read_tail");
        var key = StorageBenchmarks.CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = StorageBenchmarks.CreateEngine(dir, fsyncCommitLog: true, region: "gru", nodeId: 1);
        await using var session = await StorageBenchmarks.CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await StorageBenchmarks.CreateItemsTableAsync(session, dbId, cancellationToken);

        _ = await ctx.MeasureAsync(
            "commitlog",
            "commitlog_append_read_tail",
            warmup: 5,
            iterations: 100,
            async index =>
            {
                var id = index + 1L;
                var written = await StorageBenchmarks.ExecuteWriteAsync(
                    session,
                    dbId,
                    [StorageBenchmarks.Statement(
                        "INSERT INTO items (id, name, score, payload) VALUES (?1, ?2, ?3, ?4)",
                        SqlValue.FromIntegral(id),
                        SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"tail-{id}")),
                        SqlValue.FromIntegral(id),
                        SqlValue.FromText(PayloadText))],
                    ConsistencyMode.Fast,
                    cancellationToken);

                var page = await ReadCommitLogAsync(session, "gru", written.Commit.Sequence, 1, cancellationToken);
                BenchRunnerContext.AssertCondition(
                    page.Entries.Count == 1
                    && page.Entries[0].Sequence == written.Commit.Sequence
                    && page.Entries[0].TxId == written.Commit.TxId,
                    "commitlog_append_read_tail tail mismatch");
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "append+read_tail; FsyncCommitLog=true",
            cancellationToken: cancellationToken);
    }

    private static async Task RunVersionVectorSnapshotSatisfiesAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var dir = ctx.RunDir("version_vector_snapshot_satisfies");
        var key = StorageBenchmarks.CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = StorageBenchmarks.CreateEngine(dir, fsyncCommitLog: false, region: "iad", nodeId: 1);
        await using var session = await StorageBenchmarks.CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await StorageBenchmarks.CreateItemsTableAsync(session, dbId, cancellationToken);

        string[] remoteRegions = ["gru", "fra", "nrt", "syd"];
        var clock = new HlcClock(nodeId: 2, TimeProvider.System);
        var rowId = 1L;
        foreach (var remote in remoteRegions)
        {
            var mutation = new RowMutation(
                "items",
                MutationOp.Insert,
                1,
                [SqlValue.FromIntegral(rowId++)],
                [
                    new ColumnChange("name", ChangeKind.Assign, 0, SqlValue.FromText(remote), 0),
                    new ColumnChange("score", ChangeKind.Assign, 0, SqlValue.FromIntegral(1), 0),
                    new ColumnChange("payload", ChangeKind.Assign, 0, SqlValue.FromText(PayloadText), 0),
                ]);
            var commit = new CommitMetadata(
                Ulid.NewUlid(),
                remote,
                1,
                clock.Tick(),
                session.SchemaEpoch,
                ConsistencyMode.Fast,
                CausalBookmark.ForRegion(remote, 1),
                MutationBatchCodec.Encode(new DeltaBatch([], [mutation])));
            var applied = await ApplyRemoteBatchAsync(session, new RemoteCommitBatch(dbId, [commit]), cancellationToken);
            BenchRunnerContext.AssertCondition(applied.Applied == 1, string.Concat("failed to seed remote origin ", remote));
        }

        var baseline = session.CurrentBookmark;
        BenchRunnerContext.AssertCondition(baseline.Entries.Count == 5, string.Create(CultureInfo.InvariantCulture, $"expected 5 origins in version vector, got {baseline.Entries.Count}"));

        _ = await ctx.MeasureAsync(
            "commitlog",
            "version_vector_snapshot_satisfies",
            warmup: 100,
            iterations: 10000,
            async _ =>
            {
                var snapshot = session.CurrentBookmark;
                var reached = await WaitForBookmarkAsync(session, snapshot, TimeSpan.FromSeconds(1), cancellationToken);
                BenchRunnerContext.AssertCondition(reached && snapshot.Entries.Count == 5 && snapshot.Dominates(baseline), "version_vector_snapshot_satisfies failed");
            },
            note: "5 regions; Snapshot()+Satisfies()",
            cancellationToken: cancellationToken);
    }

    internal static DeltaBatch BuildSampleDeltaBatch(int mutationCount)
    {
        var mutations = new RowMutation[mutationCount];
        byte[] blob = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        for (var i = 0; i < mutationCount; i++)
        {
            var op = i % 3 == 0 ? MutationOp.Insert : MutationOp.Update;
            mutations[i] = new RowMutation(
                "items",
                op,
                (ulong)(op == MutationOp.Insert ? 1 : 3),
                [SqlValue.FromIntegral(i + 1L)],
                [
                    new ColumnChange("name", ChangeKind.Assign, 0, SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"row-{i}")), 0),
                    new ColumnChange("score", ChangeKind.DeltaOp, 0, SqlValue.Null, (i % 7) - 3L),
                    new ColumnChange("ratio", ChangeKind.Assign, 1, SqlValue.FromReal(i * 0.25), 0),
                    new ColumnChange("raw", ChangeKind.AbsoluteReset, 2, SqlValue.FromBlob(blob), 0),
                ]);
        }

        return new DeltaBatch([], mutations);
    }

    private static async ValueTask<RemoteApplyOutcome> ApplyRemoteBatchAsync(
        ISqlDatabaseSession session,
        RemoteCommitBatch batch,
        CancellationToken cancellationToken)
    {
        var outcome = await session.ApplyRemoteBatchAsync(batch, cancellationToken);
        var (applied, error) = outcome switch
        {
            RemoteApplyOutcome value => (value, (string?)null),
            SchemaMismatch mismatch => (null, string.Create(CultureInfo.InvariantCulture, $"schema mismatch: expected {mismatch.ExpectedEpoch}, actual {mismatch.ActualEpoch}")),
            StorageCorrupted corrupted => (null, corrupted.Message),
            StorageBusy busy => (null, busy.Message),
        };
        return BenchRunnerContext.Require(applied, error, "ApplyRemoteBatchAsync failed");
    }

    private static async ValueTask<CommitLogPage> ReadCommitLogAsync(
        ISqlDatabaseSession session,
        string originRegion,
        long fromSequence,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        var outcome = await session.ReadCommitLogAsync(originRegion, fromSequence, maxEntries, cancellationToken);
        var (page, error) = outcome switch
        {
            CommitLogPage value => (value, (string?)null),
            CommitLogGap gap => (null, string.Create(CultureInfo.InvariantCulture, $"commit log gap: requested {gap.RequestedFrom}, oldest {gap.OldestAvailable}")),
            StorageCorrupted corrupted => (null, corrupted.Message),
            StorageBusy busy => (null, busy.Message),
        };
        return BenchRunnerContext.Require(page, error, "ReadCommitLogAsync failed");
    }

    private static async ValueTask<IReadOnlyList<CommitLogSegment>> ListSealedSegmentsAsync(
        ISqlDatabaseSession session,
        string originRegion,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        var outcome = await session.ListSealedSegmentsAsync(originRegion, afterSequence, cancellationToken);
        var (segments, error) = outcome switch
        {
            IReadOnlyList<CommitLogSegment> list => (list, (string?)null),
            StorageBusy busy => (null, busy.Message),
        };
        return BenchRunnerContext.Require(segments, error, "ListSealedSegmentsAsync failed");
    }

    private static async ValueTask<Stream> OpenSegmentAsync(
        ISqlDatabaseSession session,
        CommitLogSegment segment,
        CancellationToken cancellationToken)
    {
        var outcome = await session.OpenSegmentAsync(segment, cancellationToken);
        var (stream, error) = outcome switch
        {
            Stream opened => (opened, (string?)null),
            SegmentNotFound missing => (null, missing.Message),
            StorageBusy busy => (null, busy.Message),
        };
        return BenchRunnerContext.Require(stream, error, "OpenSegmentAsync failed");
    }

    private static async ValueTask<bool> ReadArchivedSegmentAsync(
        ISqlDatabaseSession session,
        CommitLogSegment segment,
        Stream content,
        ReplayArchivedCommitBatch replay,
        CancellationToken cancellationToken)
    {
        var outcome = await session.ReadArchivedSegmentAsync(segment, content, replay, cancellationToken);
        var (readOk, error) = outcome switch
        {
            ArchivedSegmentRead => (true, (string?)null),
            ArchivedSegmentReplayStopped => (false, "archived segment replay stopped early"),
            InvalidSegment invalid => (false, invalid.Message),
            StorageCorrupted corrupted => (false, corrupted.Message),
        };
        BenchRunnerContext.AssertCondition(readOk, error ?? "ReadArchivedSegmentAsync failed");
        return readOk;
    }

    private static async ValueTask<bool> WaitForBookmarkAsync(
        ISqlDatabaseSession session,
        CausalBookmark bookmark,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var outcome = await session.WaitForBookmarkAsync(bookmark, timeout, cancellationToken);
        var (reached, error) = outcome switch
        {
            BookmarkReached => (true, (string?)null),
            StorageTimeout timedOut => (false, timedOut.Message),
        };
        BenchRunnerContext.AssertCondition(reached, error ?? "WaitForBookmarkAsync failed");
        return reached;
    }
}
