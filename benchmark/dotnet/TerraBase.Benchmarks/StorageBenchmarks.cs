using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using TerraBase.Storage;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Storage;

namespace TerraBase.Benchmarks;

public static class StorageBenchmarks
{
    private const string Region = "gru";
    private const string PayloadText = "terrabase-benchmark-payload-value-0123456789abcdefghijklmnopqrstuvwxyz-0123456789abcdefghijklmnopqrstuvwxyz";
    private static readonly IReadOnlyDictionary<string, SqlValue> EmptyNamed = new Dictionary<string, SqlValue>(StringComparer.Ordinal);
    private static readonly IReadOnlyList<SqlValue> EmptyPositional = [];

    public static async Task RunAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.ShouldRun("storage"))
        {
            if (ctx.ShouldRunBenchmark("storage", "db_open_cold"))
            {
                await RunDbOpenColdAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "db_create_close"))
            {
                await RunDbCreateCloseAsync(ctx, cancellationToken);
            }

            if (ctx.HasMatchingBenchmark("storage", "read_point", "read_range_100", "read_concurrent_16"))
            {
                await RunReadSuiteAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "write_single_insert"))
            {
                await RunWriteSingleInsertAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "write_single_update_hot"))
            {
                await RunWriteSingleUpdateHotAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "write_batch_25"))
            {
                await RunWriteBatch25Async(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "write_concurrent_32"))
            {
                await RunWriteConcurrent32Async(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "tx_interactive_rw"))
            {
                await RunTxInteractiveRwAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "strong_stage_commit"))
            {
                await RunStrongStageCommitAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "branch_live"))
            {
                await RunBranchLiveAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "snapshot_backup"))
            {
                await RunSnapshotBackupAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "mixed_rw_contention_16r_4w"))
            {
                await RunMixedRwContention16R4WAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("storage", "large_blob_rw_64kb"))
            {
                await RunLargeBlobRw64KbAsync(ctx, cancellationToken);
            }
        }

        if (ctx.ShouldRun("resources"))
        {
            if (ctx.ShouldRunBenchmark("resources", "multi_db_50_open_rss_kib"))
            {
                await RunMultiDb50OpenRssAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("resources", "multi_db_200_active_density"))
            {
                await RunMultiDb200ActiveDensityAsync(ctx, cancellationToken);
            }

            if (ctx.ShouldRunBenchmark("resources", "disk_footprint_1k_rows"))
            {
                await RunDiskFootprint1kRowsAsync(ctx, cancellationToken);
            }
        }
    }

    private static async Task RunDbOpenColdAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var dir = ctx.RunDir("db_open_cold");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);

        await using (var seedSession = await CreateDatabaseAsync(engine, dbId, key, cancellationToken))
        {
            await CreateItemsTableAsync(seedSession, dbId, cancellationToken);
            await SeedItemsAsync(seedSession, dbId, 100, batchSize: 50, cancellationToken);
        }

        _ = await ctx.MeasureAsync(
            "storage",
            "db_open_cold",
            warmup: 2,
            iterations: 15,
            async _ =>
            {
                await using var session = await OpenDatabaseAsync(engine, dbId, key, cancellationToken);
                var count = await CountRowsAsync(session, dbId, "items", cancellationToken);
                BenchRunnerContext.AssertCondition(count == 100, string.Create(CultureInfo.InvariantCulture, $"db_open_cold expected 100 rows, got {count}"));
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "100 seeded rows",
            cancellationToken: cancellationToken);
    }

    private static async Task RunDbCreateCloseAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var dir = ctx.RunDir("db_create_close");
        var key = CreateRawKey();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        var created = 0;

        _ = await ctx.MeasureAsync(
            "storage",
            "db_create_close",
            warmup: 2,
            iterations: 15,
            async _ =>
            {
                var dbId = Ulid.NewUlid();
                await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);
                BenchRunnerContext.AssertCondition(session.IsUsable, "db_create_close created unusable session");
                created++;
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "create+close",
            cancellationToken: cancellationToken);

        BenchRunnerContext.AssertCondition(created > 0, "db_create_close created 0 databases");
    }

    private static async Task RunReadSuiteAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int RowCount = 10_000;
        var dir = ctx.RunDir("read_suite");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: false);
        await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);

        await CreateItemsTableAsync(session, dbId, cancellationToken);
        await SeedItemsAsync(session, dbId, RowCount, batchSize: 200, cancellationToken);

        var seededCount = await CountRowsAsync(session, dbId, "items", cancellationToken);
        BenchRunnerContext.AssertCondition(seededCount == RowCount, string.Create(CultureInfo.InvariantCulture, $"read_suite expected {RowCount} rows, got {seededCount}"));

        if (ctx.ShouldRunBenchmark("storage", "read_point"))
        {
            _ = await ctx.MeasureAsync(
                "storage",
                "read_point",
                warmup: 50,
                iterations: 2000,
                async index =>
                {
                    var targetId = (index % RowCount) + 1L;
                    var result = await ExecuteReadAsync(
                        session,
                        dbId,
                        [Statement("SELECT id, name, score, payload FROM items WHERE id = ?1", SqlValue.FromIntegral(targetId))],
                        cancellationToken);
                    var rows = result.ResultSets[0].Rows;
                    BenchRunnerContext.AssertCondition(
                        rows.Count == 1 && rows[0][0].TryGetIntegral(out var id) && id == targetId,
                        string.Create(CultureInfo.InvariantCulture, $"read_point failed for id {targetId}"));
                },
                diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
                note: "10k rows",
                cancellationToken: cancellationToken);
        }

        if (ctx.ShouldRunBenchmark("storage", "read_range_100"))
        {
            _ = await ctx.MeasureAsync(
                "storage",
                "read_range_100",
                warmup: 20,
                iterations: 500,
                async index =>
                {
                    var startId = (index % (RowCount - 100)) + 1L;
                    var endId = startId + 100L;
                    var result = await ExecuteReadAsync(
                        session,
                        dbId,
                        [Statement("SELECT id, name, score, payload FROM items WHERE id >= ?1 AND id < ?2 ORDER BY id", SqlValue.FromIntegral(startId), SqlValue.FromIntegral(endId))],
                        cancellationToken);
                    var rows = result.ResultSets[0].Rows;
                    BenchRunnerContext.AssertCondition(
                        rows.Count == 100 && rows[0][0].TryGetIntegral(out var firstId) && firstId == startId,
                        string.Create(CultureInfo.InvariantCulture, $"read_range_100 expected 100 rows from {startId}, got {rows.Count}"));
                },
                diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
                note: "100 rows/scan",
                cancellationToken: cancellationToken);
        }

        if (ctx.ShouldRunBenchmark("storage", "read_concurrent_16"))
        {
            _ = await ctx.MeasureAsync(
                "storage",
                "read_concurrent_16",
                warmup: 10,
                iterations: 200,
                async index =>
                {
                    var tasks = new Task<SqlReadResult>[16];
                    for (var worker = 0; worker < tasks.Length; worker++)
                    {
                        var targetId = (((index * 16) + worker) % RowCount) + 1L;
                        tasks[worker] = ExecuteReadAsync(
                            session,
                            dbId,
                            [Statement("SELECT id, name, score, payload FROM items WHERE id = ?1", SqlValue.FromIntegral(targetId))],
                            cancellationToken).AsTask();
                    }

                    var results = await Task.WhenAll(tasks);
                    for (var worker = 0; worker < results.Length; worker++)
                    {
                        var targetId = (((index * 16) + worker) % RowCount) + 1L;
                        var rows = results[worker].ResultSets[0].Rows;
                        BenchRunnerContext.AssertCondition(
                            rows.Count == 1 && rows[0][0].TryGetIntegral(out var id) && id == targetId,
                            string.Create(CultureInfo.InvariantCulture, $"read_concurrent_16 mismatch on worker {worker}"));
                    }
                },
                diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
                note: "16 concurrent reads/op",
                cancellationToken: cancellationToken);
        }
    }

    private static async Task RunWriteSingleInsertAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var dir = ctx.RunDir("write_single_insert");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await CreateItemsTableAsync(session, dbId, cancellationToken);

        var inserted = 0L;
        _ = await ctx.MeasureAsync(
            "storage",
            "write_single_insert",
            warmup: 5,
            iterations: 100,
            async index =>
            {
                var id = index + 1L;
                var result = await ExecuteWriteAsync(
                    session,
                    dbId,
                    [Statement(
                        "INSERT INTO items (id, name, score, payload) VALUES (?1, ?2, ?3, ?4)",
                        SqlValue.FromIntegral(id),
                        SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"item-{id}")),
                        SqlValue.FromIntegral(id * 3L),
                        SqlValue.FromText(PayloadText))],
                    ConsistencyMode.Fast,
                    cancellationToken);
                BenchRunnerContext.AssertCondition(
                    result.ResultSets[0].RowsAffected == 1 && result.Commit.Sequence > 0,
                    "write_single_insert failed");
                inserted++;
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "FsyncCommitLog=true",
            cancellationToken: cancellationToken);

        var count = await CountRowsAsync(session, dbId, "items", cancellationToken);
        BenchRunnerContext.AssertCondition(count == inserted, string.Create(CultureInfo.InvariantCulture, $"write_single_insert row count {count} != {inserted}"));
    }

    private static async Task RunWriteSingleUpdateHotAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var dir = ctx.RunDir("write_single_update_hot");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await CreateItemsTableAsync(session, dbId, cancellationToken);
        await SeedItemsAsync(session, dbId, 1, batchSize: 1, cancellationToken);

        var lastScore = 0L;
        _ = await ctx.MeasureAsync(
            "storage",
            "write_single_update_hot",
            warmup: 5,
            iterations: 100,
            async index =>
            {
                var score = index + 10L;
                var result = await ExecuteWriteAsync(
                    session,
                    dbId,
                    [Statement(
                        "UPDATE items SET score = ?1, name = ?2 WHERE id = 1",
                        SqlValue.FromIntegral(score),
                        SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"hot-{score}")))],
                    ConsistencyMode.Fast,
                    cancellationToken);
                BenchRunnerContext.AssertCondition(
                    result.ResultSets[0].RowsAffected == 1 && result.Commit.Sequence > 0,
                    "write_single_update_hot failed");
                lastScore = score;
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "FsyncCommitLog=true",
            cancellationToken: cancellationToken);

        var check = await ExecuteReadAsync(session, dbId, [Statement("SELECT score FROM items WHERE id = 1")], cancellationToken);
        BenchRunnerContext.AssertCondition(
            check.ResultSets[0].Rows.Count == 1 && check.ResultSets[0].Rows[0][0].TryGetIntegral(out var stored) && stored == lastScore,
            string.Create(CultureInfo.InvariantCulture, $"write_single_update_hot final score mismatch, expected {lastScore}"));
    }

    private static async Task RunWriteBatch25Async(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int BatchSize = 25;
        var dir = ctx.RunDir("write_batch_25");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await CreateItemsTableAsync(session, dbId, cancellationToken);

        var totalRows = 0L;
        _ = await ctx.MeasureAsync(
            "storage",
            "write_batch_25",
            warmup: 5,
            iterations: 60,
            async index =>
            {
                var statements = new SqlStatement[BatchSize];
                var baseId = (index * BatchSize) + 1L;
                for (var item = 0; item < BatchSize; item++)
                {
                    var id = baseId + item;
                    statements[item] = Statement(
                        "INSERT INTO items (id, name, score, payload) VALUES (?1, ?2, ?3, ?4)",
                        SqlValue.FromIntegral(id),
                        SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"batch-{id}")),
                        SqlValue.FromIntegral(id),
                        SqlValue.FromText(PayloadText));
                }

                var result = await ExecuteWriteAsync(session, dbId, statements, ConsistencyMode.Fast, cancellationToken);
                BenchRunnerContext.AssertCondition(
                    result.ResultSets.Count == BatchSize && result.Commit.Sequence > 0,
                    "write_batch_25 failed");
                for (var item = 0; item < BatchSize; item++)
                {
                    BenchRunnerContext.AssertCondition(result.ResultSets[item].RowsAffected == 1, "write_batch_25 statement did not affect 1 row");
                }

                totalRows += BatchSize;
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "25 inserts/op; FsyncCommitLog=true",
            cancellationToken: cancellationToken);

        var count = await CountRowsAsync(session, dbId, "items", cancellationToken);
        BenchRunnerContext.AssertCondition(count == totalRows, string.Create(CultureInfo.InvariantCulture, $"write_batch_25 row count {count} != {totalRows}"));
    }

    private static async Task RunWriteConcurrent32Async(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int Writers = 32;
        var dir = ctx.RunDir("write_concurrent_32");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await CreateItemsTableAsync(session, dbId, cancellationToken);

        var expectedRows = 0L;
        _ = await ctx.MeasureAsync(
            "storage",
            "write_concurrent_32",
            warmup: 3,
            iterations: 30,
            async index =>
            {
                var tasks = new Task<SqlWriteResult>[Writers];
                var baseId = (index * Writers) + 1L;
                for (var writer = 0; writer < Writers; writer++)
                {
                    var id = baseId + writer;
                    tasks[writer] = ExecuteWriteAsync(
                        session,
                        dbId,
                        [Statement(
                            "INSERT INTO items (id, name, score, payload) VALUES (?1, ?2, ?3, ?4)",
                            SqlValue.FromIntegral(id),
                            SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"conc-{id}")),
                            SqlValue.FromIntegral(id),
                            SqlValue.FromText(PayloadText))],
                        ConsistencyMode.Fast,
                        cancellationToken).AsTask();
                }

                var results = await Task.WhenAll(tasks);
                foreach (var write in results)
                {
                    BenchRunnerContext.AssertCondition(
                        write.ResultSets[0].RowsAffected == 1 && write.Commit.Sequence > 0,
                        "write_concurrent_32 write failed");
                }

                expectedRows += Writers;
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "32 concurrent writers/op; FsyncCommitLog=true",
            cancellationToken: cancellationToken);

        var count = await CountRowsAsync(session, dbId, "items", cancellationToken);
        BenchRunnerContext.AssertCondition(count == expectedRows, string.Create(CultureInfo.InvariantCulture, $"write_concurrent_32 row count {count} != {expectedRows}"));
    }

    private static async Task RunTxInteractiveRwAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var dir = ctx.RunDir("tx_interactive_rw");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await CreateItemsTableAsync(session, dbId, cancellationToken);
        await SeedItemsAsync(session, dbId, 100, batchSize: 50, cancellationToken);

        _ = await ctx.MeasureAsync(
            "storage",
            "tx_interactive_rw",
            warmup: 5,
            iterations: 100,
            async index =>
            {
                var id = (index % 100) + 1L;
                await using var tx = await BeginTransactionAsync(
                    session,
                    new SqlTransactionOptions(ConsistencyMode.Fast, SqlAccess.Write, CausalBookmark.Empty),
                    cancellationToken);

                var readSet = await ExecuteTxAsync(
                    tx,
                    Statement("SELECT score FROM items WHERE id = ?1", SqlValue.FromIntegral(id)),
                    cancellationToken);
                if (readSet.Rows.Count != 1 || !readSet.Rows[0][0].TryGetIntegral(out var currentScore))
                {
                    throw new InvalidOperationException("tx_interactive_rw select failed");
                }

                var updateSet = await ExecuteTxAsync(
                    tx,
                    Statement(
                        "UPDATE items SET score = ?1 WHERE id = ?2",
                        SqlValue.FromIntegral(currentScore + 1L),
                        SqlValue.FromIntegral(id)),
                    cancellationToken);
                BenchRunnerContext.AssertCondition(updateSet.RowsAffected == 1, "tx_interactive_rw update failed");

                var commit = await CommitTxLocalAsync(tx, cancellationToken);
                BenchRunnerContext.AssertCondition(commit.Sequence > 0, "tx_interactive_rw commit produced sequence 0");
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "begin+select+update+commit; FsyncCommitLog=true",
            cancellationToken: cancellationToken);
    }

    private static async Task RunStrongStageCommitAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var dir = ctx.RunDir("strong_stage_commit");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await CreateItemsTableAsync(session, dbId, cancellationToken);
        await SeedItemsAsync(session, dbId, 100, batchSize: 50, cancellationToken);

        _ = await ctx.MeasureAsync(
            "storage",
            "strong_stage_commit",
            warmup: 5,
            iterations: 100,
            async index =>
            {
                var id = (index % 100) + 1L;
                var staged = await StageWriteAsync(
                    session,
                    dbId,
                    [Statement(
                        "UPDATE items SET score = ?1 WHERE id = ?2",
                        SqlValue.FromIntegral(index + 1000L),
                        SqlValue.FromIntegral(id))],
                    cancellationToken);
                BenchRunnerContext.AssertCondition(
                    staged.ResultSets.Count == 1 && staged.ResultSets[0].RowsAffected == 1 && staged.DeltaPayload.Length > 0,
                    "strong_stage_commit stage failed");

                var commit = await CommitStagedAsync(session, staged.Token, engine.Clock.Tick(), cancellationToken);
                BenchRunnerContext.AssertCondition(
                    commit.Sequence > 0 && commit.Mode == ConsistencyMode.Strong,
                    "strong_stage_commit commit failed");
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "stage+commit; FsyncCommitLog=true",
            cancellationToken: cancellationToken);
    }

    private static async Task RunBranchLiveAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int SeedRows = 500;
        var dir = ctx.RunDir("branch_live");
        var key = CreateRawKey();
        var sourceId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var source = await CreateDatabaseAsync(engine, sourceId, key, cancellationToken);
        await CreateItemsTableAsync(source, sourceId, cancellationToken);
        await SeedItemsAsync(source, sourceId, SeedRows, batchSize: 100, cancellationToken);

        _ = await ctx.MeasureAsync(
            "storage",
            "branch_live",
            warmup: 1,
            iterations: 10,
            async _ =>
            {
                var branchId = Ulid.NewUlid();
                await using var branch = await BranchDatabaseAsync(engine, sourceId, branchId, key, CausalBookmark.Empty, cancellationToken);
                var count = await CountRowsAsync(branch, branchId, "items", cancellationToken);
                BenchRunnerContext.AssertCondition(count == SeedRows, string.Create(CultureInfo.InvariantCulture, $"branch_live expected {SeedRows} rows, got {count}"));
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "500 seeded rows",
            cancellationToken: cancellationToken);
    }

    private static async Task RunSnapshotBackupAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int SeedRows = 1000;
        var dir = ctx.RunDir("snapshot_backup");
        var key = CreateRawKey();
        var sourceId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var source = await CreateDatabaseAsync(engine, sourceId, key, cancellationToken);
        await CreateItemsTableAsync(source, sourceId, cancellationToken);
        await SeedItemsAsync(source, sourceId, SeedRows, batchSize: 100, cancellationToken);

        var lastSnapshotBytes = 0L;
        _ = await ctx.MeasureAsync(
            "storage",
            "snapshot_backup",
            warmup: 1,
            iterations: 10,
            async _ =>
            {
                var snapshot = await SnapshotDatabaseAsync(engine, sourceId, cancellationToken);
                BenchRunnerContext.AssertCondition(
                    snapshot.SizeBytes > 0 && File.Exists(snapshot.Path),
                    "snapshot_backup produced missing or empty snapshot");
                lastSnapshotBytes = snapshot.SizeBytes;
            },
            diskBytes: () => lastSnapshotBytes,
            note: "1k seeded rows",
            cancellationToken: cancellationToken);
    }

    private static async Task RunMixedRwContention16R4WAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int RowCount = 10_000;
        const int Readers = 16;
        const int Writers = 4;
        var dir = ctx.RunDir("mixed_rw_contention_16r_4w");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);

        await CreateItemsTableAsync(session, dbId, cancellationToken);
        await SeedItemsAsync(session, dbId, RowCount, batchSize: 200, cancellationToken);

        var readTasks = new Task<SqlReadResult>[Readers];
        var writeTasks = new Task<SqlWriteResult>[Writers];

        _ = await ctx.MeasureAsync(
            "storage",
            "mixed_rw_contention_16r_4w",
            warmup: 5,
            iterations: 40,
            async index =>
            {
                for (var reader = 0; reader < Readers; reader++)
                {
                    var targetId = (((index * Readers) + reader) % RowCount) + 1L;
                    readTasks[reader] = ExecuteReadAsync(
                        session,
                        dbId,
                        [Statement("SELECT id, name, score, payload FROM items WHERE id = ?1", SqlValue.FromIntegral(targetId))],
                        cancellationToken).AsTask();
                }

                for (var writer = 0; writer < Writers; writer++)
                {
                    var targetId = (((index * Writers) + writer) % RowCount) + 1L;
                    var score = ((index + 1L) * 100L) + writer;
                    writeTasks[writer] = ExecuteWriteAsync(
                        session,
                        dbId,
                        [Statement(
                            "UPDATE items SET score = ?1, name = ?2 WHERE id = ?3",
                            SqlValue.FromIntegral(score),
                            SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"mixed-{score}")),
                            SqlValue.FromIntegral(targetId))],
                        ConsistencyMode.Fast,
                        cancellationToken).AsTask();
                }

                var reads = await Task.WhenAll(readTasks);
                var writes = await Task.WhenAll(writeTasks);

                for (var reader = 0; reader < reads.Length; reader++)
                {
                    var targetId = (((index * Readers) + reader) % RowCount) + 1L;
                    var rows = reads[reader].ResultSets[0].Rows;
                    BenchRunnerContext.AssertCondition(
                        rows.Count == 1 && rows[0][0].TryGetIntegral(out var id) && id == targetId,
                        string.Create(CultureInfo.InvariantCulture, $"mixed_rw_contention_16r_4w read mismatch on worker {reader}"));
                }

                for (var writer = 0; writer < writes.Length; writer++)
                {
                    BenchRunnerContext.AssertCondition(
                        writes[writer].ResultSets[0].RowsAffected == 1 && writes[writer].Commit.Sequence > 0,
                        string.Create(CultureInfo.InvariantCulture, $"mixed_rw_contention_16r_4w write failed on worker {writer}"));
                }
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "16 readers + 4 writers/op; 10k rows; FsyncCommitLog=true",
            cancellationToken: cancellationToken,
            unitsPerOp: Readers + Writers,
            unitLabel: "ops");
    }

    private static async Task RunLargeBlobRw64KbAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int BlobBytes = 64 * 1024;
        const int PoolSize = 16;
        var dir = ctx.RunDir("large_blob_rw_64kb");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);

        var ddl = await ExecuteWriteAsync(
            session,
            dbId,
            [Statement("CREATE TABLE blobs (id INTEGER PRIMARY KEY, data BLOB NOT NULL)")],
            ConsistencyMode.Strong,
            cancellationToken);
        BenchRunnerContext.AssertCondition(ddl.Commit.Sequence > 0, "large_blob_rw_64kb DDL failed");

        var blobs = new byte[PoolSize][];
        for (var i = 0; i < PoolSize; i++)
        {
            blobs[i] = new byte[BlobBytes];
            RandomNumberGenerator.Fill(blobs[i]);
        }

        var writtenCount = 0L;
        _ = await ctx.MeasureAsync(
            "storage",
            "large_blob_rw_64kb",
            warmup: 5,
            iterations: 60,
            async index =>
            {
                var id = index + 1L;
                var expected = blobs[index % PoolSize];
                var written = await ExecuteWriteAsync(
                    session,
                    dbId,
                    [Statement("INSERT INTO blobs (id, data) VALUES (?1, ?2)", SqlValue.FromIntegral(id), SqlValue.FromBlob(expected))],
                    ConsistencyMode.Fast,
                    cancellationToken);
                BenchRunnerContext.AssertCondition(
                    written.ResultSets[0].RowsAffected == 1 && written.Commit.Sequence > 0,
                    "large_blob_rw_64kb insert failed");

                var read = await ExecuteReadAsync(
                    session,
                    dbId,
                    [Statement("SELECT data FROM blobs WHERE id = ?1", SqlValue.FromIntegral(id))],
                    cancellationToken);
                var rows = read.ResultSets[0].Rows;
                BenchRunnerContext.AssertCondition(
                    rows.Count == 1
                    && rows[0][0].TryGetBlob(out var actual)
                    && actual.Length == BlobBytes
                    && actual.Span.SequenceEqual(expected),
                    string.Create(CultureInfo.InvariantCulture, $"large_blob_rw_64kb verification failed for id {id}"));
                writtenCount++;
            },
            diskBytes: () => BenchRunnerContext.DirectorySizeBytes(dir),
            note: "64KiB random BLOB write+read+verify; FsyncCommitLog=true",
            cancellationToken: cancellationToken,
            unitsPerOp: 2,
            unitLabel: "rw");

        var count = await CountRowsAsync(session, dbId, "blobs", cancellationToken);
        BenchRunnerContext.AssertCondition(count == writtenCount, string.Create(CultureInfo.InvariantCulture, $"large_blob_rw_64kb row count {count} != {writtenCount}"));
    }

    private static async Task RunMultiDb50OpenRssAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int DatabaseCount = 50;
        const double BytesPerMiB = 1024.0 * 1024.0;
        var dir = ctx.RunDir("multi_db_50_open_rss_kib");
        var key = CreateRawKey();
        var ids = new Ulid[DatabaseCount];
        await using var engine = CreateEngine(dir, fsyncCommitLog: false);

        for (var i = 0; i < DatabaseCount; i++)
        {
            ids[i] = Ulid.NewUlid();
            await using var created = await CreateDatabaseAsync(engine, ids[i], key, cancellationToken);
            await CreateItemsTableAsync(created, ids[i], cancellationToken);
            await SeedItemsAsync(created, ids[i], 10, batchSize: 10, cancellationToken);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = ctx.CaptureSnapshot();
        var sessions = new List<ISqlDatabaseSession>(DatabaseCount);
        try
        {
            for (var i = 0; i < DatabaseCount; i++)
            {
                var session = await OpenDatabaseAsync(engine, ids[i], key, cancellationToken);
                sessions.Add(session);
                var count = await CountRowsAsync(session, ids[i], "items", cancellationToken);
                BenchRunnerContext.AssertCondition(count == 10, string.Create(CultureInfo.InvariantCulture, $"multi_db_50_open_rss_kib db {i} expected 10 rows, got {count}"));
            }

            var after = ctx.CaptureSnapshot();
            var rssDeltaKiB = (after.RssBytes - before.RssBytes) / 1024.0;
            var rssKiBPerDb = rssDeltaKiB / DatabaseCount;
            var fdDelta = after.OpenFdCount - before.OpenFdCount;
            var fdsPerDb = (double)fdDelta / DatabaseCount;
            var managedHeapMiB = after.ManagedHeapBytes / BytesPerMiB;
            var nativeRssMiB = Math.Max(0.0, (after.RssBytes - after.ManagedHeapBytes) / BytesPerMiB);
            var diskBytes = BenchRunnerContext.DirectorySizeBytes(dir);

            _ = ctx.RecordSnapshotMetric(
                "resources",
                "multi_db_50_open_rss_kib",
                meanUs: rssKiBPerDb,
                workingSetMiB: after.RssBytes / BytesPerMiB,
                diskBytes: diskBytes,
                note: string.Create(
                    CultureInfo.InvariantCulture,
                    $"dbs={DatabaseCount} rss_delta_kib={rssDeltaKiB:F1} rss_kib_per_db={rssKiBPerDb:F1} fd_delta={fdDelta} fds_per_db={fdsPerDb:F2}"),
                managedHeapMiB: managedHeapMiB,
                nativeRssMiB: nativeRssMiB,
                openFdCount: after.OpenFdCount,
                openFdDelta: fdDelta);
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }

    private static async Task RunMultiDb200ActiveDensityAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int DatabaseCount = 200;
        const double BytesPerMiB = 1024.0 * 1024.0;
        var dir = ctx.RunDir("multi_db_200_active_density");
        var key = CreateRawKey();
        var ids = new Ulid[DatabaseCount];
        await using var engine = CreateEngine(dir, fsyncCommitLog: false);

        for (var i = 0; i < DatabaseCount; i++)
        {
            ids[i] = Ulid.NewUlid();
            await using var created = await CreateDatabaseAsync(engine, ids[i], key, cancellationToken);
            await CreateItemsTableAsync(created, ids[i], cancellationToken);
            await SeedItemsAsync(created, ids[i], 10, batchSize: 10, cancellationToken);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = ctx.CaptureSnapshot();
        var sessions = new List<ISqlDatabaseSession>(DatabaseCount);
        try
        {
            for (var i = 0; i < DatabaseCount; i++)
            {
                var session = await OpenDatabaseAsync(engine, ids[i], key, cancellationToken);
                sessions.Add(session);
            }

            var activeOps = 0;
            var activeStart = Stopwatch.GetTimestamp();
            for (var i = 0; i < DatabaseCount; i++)
            {
                var session = sessions[i];
                var dbId = ids[i];
                var targetId = (i % 10) + 1L;
                var updatedScore = 1000L + i;

                var readBefore = await ExecuteReadAsync(
                    session,
                    dbId,
                    [Statement("SELECT id, score FROM items WHERE id = ?1", SqlValue.FromIntegral(targetId))],
                    cancellationToken);
                BenchRunnerContext.AssertCondition(readBefore.ResultSets[0].Rows.Count == 1, "multi_db_200_active_density read failed");
                activeOps++;

                var written = await ExecuteWriteAsync(
                    session,
                    dbId,
                    [Statement(
                        "UPDATE items SET score = ?1, name = ?2 WHERE id = ?3",
                        SqlValue.FromIntegral(updatedScore),
                        SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"active-{i}")),
                        SqlValue.FromIntegral(targetId))],
                    ConsistencyMode.Fast,
                    cancellationToken);
                BenchRunnerContext.AssertCondition(written.ResultSets[0].RowsAffected == 1 && written.Commit.Sequence > 0, "multi_db_200_active_density write failed");
                activeOps++;

                var readAfter = await ExecuteReadAsync(
                    session,
                    dbId,
                    [Statement("SELECT score FROM items WHERE id = ?1", SqlValue.FromIntegral(targetId))],
                    cancellationToken);
                BenchRunnerContext.AssertCondition(
                    readAfter.ResultSets[0].Rows.Count == 1
                    && readAfter.ResultSets[0].Rows[0][0].TryGetIntegral(out var stored)
                    && stored == updatedScore,
                    "multi_db_200_active_density verify failed");
                activeOps++;
            }

            var activeElapsed = Stopwatch.GetElapsedTime(activeStart).TotalSeconds;
            var activeOpsPerSec = activeElapsed > 0 ? activeOps / activeElapsed : 0.0;

            var after = ctx.CaptureSnapshot();
            var rssDeltaKiB = (after.RssBytes - before.RssBytes) / 1024.0;
            var rssKiBPerDb = rssDeltaKiB / DatabaseCount;
            var managedDeltaKiB = (after.ManagedHeapBytes - before.ManagedHeapBytes) / 1024.0;
            var managedKiBPerDb = managedDeltaKiB / DatabaseCount;
            var nativeBefore = Math.Max(0L, before.RssBytes - before.ManagedHeapBytes);
            var nativeAfter = Math.Max(0L, after.RssBytes - after.ManagedHeapBytes);
            var nativeDeltaKiB = (nativeAfter - nativeBefore) / 1024.0;
            var nativeKiBPerDb = nativeDeltaKiB / DatabaseCount;
            var fdDelta = after.OpenFdCount - before.OpenFdCount;
            var fdsPerDb = (double)fdDelta / DatabaseCount;
            var diskBytes = BenchRunnerContext.DirectorySizeBytes(dir);

            _ = ctx.RecordSnapshotMetric(
                "resources",
                "multi_db_200_active_density",
                opsPerSec: activeOpsPerSec,
                meanUs: rssKiBPerDb,
                workingSetMiB: after.RssBytes / BytesPerMiB,
                diskBytes: diskBytes,
                note: string.Create(
                    CultureInfo.InvariantCulture,
                    $"dbs={DatabaseCount} rss_kib_per_db={rssKiBPerDb:F1} managed_kib_per_db={managedKiBPerDb:F1} native_kib_per_db={nativeKiBPerDb:F1} fd_delta={fdDelta} fds_per_db={fdsPerDb:F2} active_ops={activeOps}"),
                managedHeapMiB: after.ManagedHeapBytes / BytesPerMiB,
                nativeRssMiB: nativeAfter / BytesPerMiB,
                openFdCount: after.OpenFdCount,
                openFdDelta: fdDelta,
                unitsPerOp: DatabaseCount,
                unitLabel: "dbs");
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }

    private static async Task RunDiskFootprint1kRowsAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int RowCount = 1000;
        var dir = ctx.RunDir("disk_footprint_1k_rows");
        var key = CreateRawKey();
        var dbId = Ulid.NewUlid();
        await using var engine = CreateEngine(dir, fsyncCommitLog: true);
        await using var session = await CreateDatabaseAsync(engine, dbId, key, cancellationToken);
        await CreateItemsTableAsync(session, dbId, cancellationToken);
        await SeedItemsAsync(session, dbId, RowCount, batchSize: 50, cancellationToken);

        var count = await CountRowsAsync(session, dbId, "items", cancellationToken);
        BenchRunnerContext.AssertCondition(count == RowCount, string.Create(CultureInfo.InvariantCulture, $"disk_footprint_1k_rows expected {RowCount}, got {count}"));

        var dbBytes = 0L;
        var walBytes = 0L;
        var logBytes = 0L;
        var totalBytes = 0L;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                continue;
            }

            totalBytes += info.Length;
            if (file.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                dbBytes += info.Length;
            }
            else if (file.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || file.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            {
                walBytes += info.Length;
            }
            else if (file.EndsWith(".tblog", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tbseg", StringComparison.OrdinalIgnoreCase))
            {
                logBytes += info.Length;
            }
        }

        _ = ctx.RecordSnapshotMetric(
            "resources",
            "disk_footprint_1k_rows",
            diskBytes: totalBytes,
            note: string.Create(
                CultureInfo.InvariantCulture,
                $"rows={RowCount} db_b={dbBytes} wal_b={walBytes} log_b={logBytes} b_per_row={totalBytes / (double)RowCount:F1}"));
    }

    internal static byte[] CreateRawKey()
    {
        var key = new byte[DatabaseOpenRequest.RawKeyLength];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    internal static SqliteStorageEngine CreateEngine(string rootDirectory, bool fsyncCommitLog, string region = Region, ushort nodeId = 1, DeltaColumnRegistry? deltaColumns = null) =>
        new(
            new StorageEngineOptions(rootDirectory, region, nodeId, 1, deltaColumns ?? new DeltaColumnRegistry())
            {
                FsyncCommitLog = fsyncCommitLog,
            },
            TimeProvider.System);

    internal static SqlStatement Statement(string sql, params SqlValue[] positional) =>
        new(sql, positional.Length == 0 ? EmptyPositional : positional, EmptyNamed);

    internal static async Task CreateItemsTableAsync(ISqlDatabaseSession session, Ulid dbId, CancellationToken cancellationToken)
    {
        var result = await ExecuteWriteAsync(
            session,
            dbId,
            [Statement("CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score INTEGER NOT NULL, payload TEXT NOT NULL)")],
            ConsistencyMode.Strong,
            cancellationToken);
        BenchRunnerContext.AssertCondition(result.Commit.Sequence > 0, "CREATE TABLE did not produce a commit");
    }

    internal static async Task SeedItemsAsync(ISqlDatabaseSession session, Ulid dbId, int count, int batchSize, CancellationToken cancellationToken)
    {
        var id = 1L;
        while (id <= count)
        {
            var chunk = (int)Math.Min(batchSize, count - id + 1);
            var statements = new SqlStatement[chunk];
            for (var i = 0; i < chunk; i++)
            {
                var rowId = id++;
                statements[i] = Statement(
                    "INSERT INTO items (id, name, score, payload) VALUES (?1, ?2, ?3, ?4)",
                    SqlValue.FromIntegral(rowId),
                    SqlValue.FromText(string.Create(CultureInfo.InvariantCulture, $"item-{rowId}")),
                    SqlValue.FromIntegral(rowId * 10L),
                    SqlValue.FromText(PayloadText));
            }

            var written = await ExecuteWriteAsync(session, dbId, statements, ConsistencyMode.Fast, cancellationToken);
            BenchRunnerContext.AssertCondition(written.ResultSets.Count == chunk, "SeedItemsAsync batch mismatch");
        }
    }

    internal static async Task<long> CountRowsAsync(ISqlDatabaseSession session, Ulid dbId, string table, CancellationToken cancellationToken)
    {
        var result = await ExecuteReadAsync(
            session,
            dbId,
            [Statement(string.Concat("SELECT COUNT(*) FROM ", table))],
            cancellationToken);
        if (result.ResultSets.Count != 1
            || result.ResultSets[0].Rows.Count != 1
            || !result.ResultSets[0].Rows[0][0].TryGetIntegral(out var count))
        {
            throw new InvalidOperationException("CountRowsAsync did not return a count");
        }

        return count;
    }

    internal static async ValueTask<ISqlDatabaseSession> CreateDatabaseAsync(SqliteStorageEngine engine, Ulid dbId, ReadOnlyMemory<byte> rawKey, CancellationToken cancellationToken)
    {
        var outcome = await engine.CreateAsync(new DatabaseCreateRequest(dbId, rawKey, Strict: false), cancellationToken);
        var (session, error) = outcome switch
        {
            ISqlDatabaseSession created => (created, (string?)null),
            DatabaseAlreadyExists exists => (null, exists.Message),
            InvalidDatabaseRequest invalid => (null, invalid.Message),
            StorageBusy busy => (null, busy.Message),
        };
        return BenchRunnerContext.Require(session, error, "CreateAsync failed");
    }

    internal static async ValueTask<ISqlDatabaseSession> OpenDatabaseAsync(SqliteStorageEngine engine, Ulid dbId, ReadOnlyMemory<byte> rawKey, CancellationToken cancellationToken)
    {
        var outcome = await engine.OpenAsync(new DatabaseOpenRequest(dbId, rawKey), cancellationToken);
        var (session, error) = outcome switch
        {
            ISqlDatabaseSession opened => (opened, (string?)null),
            DatabaseNotFound missing => (null, missing.Message),
            InvalidDatabaseRequest invalid => (null, invalid.Message),
            StorageCorrupted corrupted => (null, corrupted.Message),
            StorageBusy busy => (null, busy.Message),
        };
        return BenchRunnerContext.Require(session, error, "OpenAsync failed");
    }

    internal static async ValueTask<ISqlDatabaseSession> BranchDatabaseAsync(
        SqliteStorageEngine engine,
        Ulid sourceId,
        Ulid branchId,
        ReadOnlyMemory<byte> rawKey,
        CausalBookmark at,
        CancellationToken cancellationToken)
    {
        var outcome = await engine.BranchAsync(new DatabaseBranchRequest(sourceId, branchId, rawKey, at), cancellationToken);
        var (session, error) = outcome switch
        {
            ISqlDatabaseSession branched => (branched, (string?)null),
            DatabaseNotFound missing => (null, missing.Message),
            DatabaseAlreadyExists exists => (null, exists.Message),
            InvalidDatabaseRequest invalid => (null, invalid.Message),
            StorageBusy busy => (null, busy.Message),
        };
        return BenchRunnerContext.Require(session, error, "BranchAsync failed");
    }

    internal static async ValueTask<DatabaseSnapshot> SnapshotDatabaseAsync(SqliteStorageEngine engine, Ulid dbId, CancellationToken cancellationToken)
    {
        var outcome = await engine.SnapshotAsync(dbId, cancellationToken);
        var (snapshot, error) = outcome switch
        {
            DatabaseSnapshot captured => (captured, (string?)null),
            DatabaseNotFound missing => (null, missing.Message),
            StorageBusy busy => (null, busy.Message),
        };
        return BenchRunnerContext.Require(snapshot, error, "SnapshotAsync failed");
    }

    internal static async ValueTask<SqlReadResult> ExecuteReadAsync(
        ISqlDatabaseSession session,
        Ulid dbId,
        IReadOnlyList<SqlStatement> statements,
        CancellationToken cancellationToken)
    {
        var outcome = await session.ExecuteReadAsync(
            new SqlStatementRequest(dbId, statements, SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty),
            cancellationToken);
        var (result, error) = outcome switch
        {
            SqlReadResult read => (read, (string?)null),
            SqlError sqlError => (null, string.Concat(sqlError.Code, ": ", sqlError.Message)),
            StorageBusy busy => (null, busy.Message),
            StorageTimeout timeout => (null, timeout.Message),
        };
        return BenchRunnerContext.Require(result, error, "ExecuteReadAsync failed");
    }

    internal static async ValueTask<SqlWriteResult> ExecuteWriteAsync(
        ISqlDatabaseSession session,
        Ulid dbId,
        IReadOnlyList<SqlStatement> statements,
        ConsistencyMode mode,
        CancellationToken cancellationToken)
    {
        var outcome = await session.ExecuteWriteAsync(
            new SqlStatementRequest(dbId, statements, SqlAccess.Write, mode, CausalBookmark.Empty),
            cancellationToken);
        var (result, error) = outcome switch
        {
            SqlWriteResult written => (written, (string?)null),
            SqlError sqlError => (null, string.Concat(sqlError.Code, ": ", sqlError.Message)),
            StrongRequired strong => (null, string.Concat("strong required: ", StrongReasonText(strong.Reason))),
            SchemaMismatch mismatch => (null, string.Create(CultureInfo.InvariantCulture, $"schema mismatch: expected {mismatch.ExpectedEpoch}, actual {mismatch.ActualEpoch}")),
            StorageBusy busy => (null, busy.Message),
            StorageTimeout timeout => (null, timeout.Message),
        };
        return BenchRunnerContext.Require(result, error, "ExecuteWriteAsync failed");
    }

    internal static async ValueTask<StagedWrite> StageWriteAsync(
        ISqlDatabaseSession session,
        Ulid dbId,
        IReadOnlyList<SqlStatement> statements,
        CancellationToken cancellationToken)
    {
        var outcome = await session.StageWriteAsync(
            new SqlStatementRequest(dbId, statements, SqlAccess.Write, ConsistencyMode.Strong, CausalBookmark.Empty),
            cancellationToken);
        var (staged, error) = outcome switch
        {
            StagedWrite value => (value, (string?)null),
            SqlError sqlError => (null, string.Concat(sqlError.Code, ": ", sqlError.Message)),
            SchemaMismatch mismatch => (null, string.Create(CultureInfo.InvariantCulture, $"schema mismatch: expected {mismatch.ExpectedEpoch}, actual {mismatch.ActualEpoch}")),
            StorageBusy busy => (null, busy.Message),
            StorageTimeout timeout => (null, timeout.Message),
        };
        return BenchRunnerContext.Require(staged, error, "StageWriteAsync failed");
    }

    internal static async ValueTask<CommitMetadata> CommitStagedAsync(
        ISqlDatabaseSession session,
        StagedWriteToken token,
        HlcTimestamp commitHlc,
        CancellationToken cancellationToken)
    {
        var outcome = await session.CommitStagedAsync(token, commitHlc, leaseDeadline: null, cancellationToken);
        var (commit, error) = outcome switch
        {
            CommitMetadata committed => (committed, (string?)null),
            StagedWriteMissing missing => (null, missing.Message),
            ReadSetConflict => (null, "read set conflict"),
            SchemaMismatch mismatch => (null, string.Create(CultureInfo.InvariantCulture, $"schema mismatch: expected {mismatch.ExpectedEpoch}, actual {mismatch.ActualEpoch}")),
            StorageBusy busy => (null, busy.Message),
            StorageTimeout timeout => (null, timeout.Message),
        };
        return BenchRunnerContext.Require(commit, error, "CommitStagedAsync failed");
    }

    internal static async ValueTask<ISqlTransaction> BeginTransactionAsync(
        ISqlDatabaseSession session,
        SqlTransactionOptions options,
        CancellationToken cancellationToken)
    {
        var outcome = await session.BeginTransactionAsync(options, cancellationToken);
        var (tx, error) = outcome switch
        {
            ISqlTransaction transaction => (transaction, (string?)null),
            StorageBusy busy => (null, busy.Message),
            StorageTimeout timeout => (null, timeout.Message),
        };
        return BenchRunnerContext.Require(tx, error, "BeginTransactionAsync failed");
    }

    internal static async ValueTask<SqlResultSet> ExecuteTxAsync(
        ISqlTransaction tx,
        SqlStatement statement,
        CancellationToken cancellationToken)
    {
        var outcome = await tx.ExecuteAsync(statement, cancellationToken);
        var (result, error) = outcome switch
        {
            SqlResultSet resultSet => (resultSet, (string?)null),
            SqlError sqlError => (null, string.Concat(sqlError.Code, ": ", sqlError.Message)),
            StorageBusy busy => (null, busy.Message),
            StorageTimeout timeout => (null, timeout.Message),
        };
        return BenchRunnerContext.Require(result, error, "Transaction.ExecuteAsync failed");
    }

    internal static async ValueTask<CommitMetadata> CommitTxLocalAsync(ISqlTransaction tx, CancellationToken cancellationToken)
    {
        var outcome = await tx.CommitLocalAsync(cancellationToken);
        var (commit, error) = outcome switch
        {
            CommitMetadata committed => (committed, (string?)null),
            StrongRequired strong => (null, string.Concat("strong required: ", StrongReasonText(strong.Reason))),
            SchemaMismatch mismatch => (null, string.Create(CultureInfo.InvariantCulture, $"schema mismatch: expected {mismatch.ExpectedEpoch}, actual {mismatch.ActualEpoch}")),
            StorageBusy busy => (null, busy.Message),
            StorageTimeout timeout => (null, timeout.Message),
        };
        return BenchRunnerContext.Require(commit, error, "Transaction.CommitLocalAsync failed");
    }

    private static string StrongReasonText(StrongRequiredReason reason) => reason switch
    {
        StrongRequiredReason.SchemaChange => "schema_change",
        StrongRequiredReason.SecondaryUniqueIndex => "secondary_unique_index",
        StrongRequiredReason.StrictTable => "strict_table",
        _ => throw new UnreachableException(),
    };
}
