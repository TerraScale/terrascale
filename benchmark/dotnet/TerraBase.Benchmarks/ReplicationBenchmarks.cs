using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Cluster;
using TerraBase.Storage;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Storage;
using TerraScale.QuicRpc;

namespace TerraBase.Benchmarks;

public static class ReplicationBenchmarks
{
    private const string Suite = "replication";
    private const double PageFrameBytes = 4096.0;
    private const double ReferenceConcurrentTps = 9500.0;

    private static readonly IReadOnlyDictionary<string, SqlValue> NoNamedParams = FrozenDictionary<string, SqlValue>.Empty;
    private static readonly string SamplePayload = new('r', 128);

    public static async Task RunAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        QuicPlatform.EnsureInitialized();
        if (!QuicPlatform.IsSupported)
        {
            throw new PlatformNotSupportedException("System.Net.Quic is required for replication benchmarks.");
        }

        await RunSingleCommitE2ELagAsync(ctx, cancellationToken);
        await RunStream1000CommitsThroughputAsync(ctx, cancellationToken);
        await RunFanout3NodeThroughputAsync(ctx, cancellationToken);
        await RunCatchup5000RowsAsync(ctx, cancellationToken);
        await RunConcurrent32WritersReplicatedAsync(ctx, fsyncCommitLog: false, cancellationToken);
        await RunConcurrent32WritersReplicatedAsync(ctx, fsyncCommitLog: true, cancellationToken);
    }

    private static async Task RunSingleCommitE2ELagAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var databaseId = Ulid.NewUlid();
        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

        await using var cluster = await ReplCluster.StartAsync(
            ctx,
            "repl-e2e",
            [("gru-1", "gru", 1), ("fra-1", "fra", 2)],
            [databaseId],
            rawKey,
            fsyncCommitLog: false,
            cancellationToken);

        var primary = cluster.Nodes[0];
        var follower = cluster.Nodes[1];
        var primarySession = primary.Sessions[databaseId];
        var followerSession = follower.Sessions[databaseId];

        await BootstrapTableAsync(
            primary,
            [follower],
            databaseId,
            "CREATE TABLE items (id INTEGER PRIMARY KEY, payload TEXT);",
            cancellationToken);

        var rowId = 0L;
        var probeId = ++rowId;
        var probeValue = string.Create(CultureInfo.InvariantCulture, $"e2e-{probeId}-{SamplePayload}");
        var probeWrite = RequireClusterWrite(await primary.Coordinator.ExecuteWriteAsync(
            primarySession,
            InsertStatement(databaseId, "INSERT INTO items (id, payload) VALUES (?, ?);", probeId, probeValue),
            cancellationToken));
        RequireBookmarkReached(await followerSession.WaitForBookmarkAsync(probeWrite.Commit.Bookmark, TimeSpan.FromSeconds(10), cancellationToken));
        VerifyStringValue(
            RequireRead(await followerSession.ExecuteReadAsync(
                SelectByIdStatement(databaseId, "SELECT payload FROM items WHERE id = ?;", probeId, probeWrite.Commit.Bookmark),
                cancellationToken)),
            probeValue,
            "e2e probe select");

        var wireBytesPerCommit = MeasureWireBytes(databaseId, primary.NodeId, [probeWrite.Commit]);
        var totalWireBytes = 0L;
        var measuredCommits = 0L;

        var metrics = await ctx.MeasureAsync(
            Suite,
            "repl_single_commit_e2e_lag",
            15,
            120,
            async _ =>
            {
                var id = ++rowId;
                var expected = string.Create(CultureInfo.InvariantCulture, $"e2e-{id}-{SamplePayload}");
                var written = RequireClusterWrite(await primary.Coordinator.ExecuteWriteAsync(
                    primarySession,
                    InsertStatement(databaseId, "INSERT INTO items (id, payload) VALUES (?, ?);", id, expected),
                    cancellationToken));
                totalWireBytes += written.Commit.DeltaPayload.Length;
                measuredCommits++;

                RequireBookmarkReached(await followerSession.WaitForBookmarkAsync(written.Commit.Bookmark, TimeSpan.FromSeconds(10), cancellationToken));
                var verified = RequireRead(await followerSession.ExecuteReadAsync(
                    SelectByIdStatement(databaseId, "SELECT payload FROM items WHERE id = ?;", id, written.Commit.Bookmark),
                    cancellationToken));
                VerifyStringValue(verified, expected, "e2e follower select");
            },
            () => measuredCommits > 0 ? totalWireBytes / measuredCommits : wireBytesPerCommit,
            string.Create(
                CultureInfo.InvariantCulture,
                $"2-node mTLS QUIC gru->fra write+push+apply+SELECT; FsyncCommitLog=false, loopback; wire_b_per_commit={wireBytesPerCommit}B vs ~1-5ms LAN / periodic pull sync"),
            cancellationToken);

        BenchRunnerContext.AssertCondition(primary.Replicator.DroppedPushes == 0, "Single-commit E2E replication dropped pushes.");
        VerifyRowCount(
            RequireRead(await followerSession.ExecuteReadAsync(
                CountStatement(databaseId, "SELECT COUNT(*) FROM items;", primarySession.CurrentBookmark),
                cancellationToken)),
            rowId,
            "repl_single_commit_e2e_lag follower count");

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  -> repl_single_commit_e2e_lag (FsyncCommitLog=false, loopback): p50={metrics.P50Us:F1}us ({metrics.P50Us / 1000.0:F3}ms) p99={metrics.P99Us:F1}us wire={wireBytesPerCommit}B/commit"));
    }

    private static async Task RunStream1000CommitsThroughputAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int commitsPerBurst = 250;
        var databaseId = Ulid.NewUlid();
        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

        await using var cluster = await ReplCluster.StartAsync(
            ctx,
            "repl-stream",
            [("gru-1", "gru", 1), ("fra-1", "fra", 2)],
            [databaseId],
            rawKey,
            fsyncCommitLog: false,
            cancellationToken);

        var primary = cluster.Nodes[0];
        var follower = cluster.Nodes[1];
        var primarySession = primary.Sessions[databaseId];
        var followerSession = follower.Sessions[databaseId];

        await BootstrapTableAsync(
            primary,
            [follower],
            databaseId,
            "CREATE TABLE stream_items (id INTEGER PRIMARY KEY, payload TEXT);",
            cancellationToken);

        var rowId = 0L;
        var probeCommits = new CommitMetadata[16];
        for (var index = 0; index < probeCommits.Length; index++)
        {
            var id = ++rowId;
            var written = RequireClusterWrite(await primary.Coordinator.ExecuteWriteAsync(
                primarySession,
                InsertStatement(databaseId, "INSERT INTO stream_items (id, payload) VALUES (?, ?);", id, SamplePayload),
                cancellationToken));
            probeCommits[index] = written.Commit;
        }

        RequireBookmarkReached(await followerSession.WaitForBookmarkAsync(probeCommits[^1].Bookmark, TimeSpan.FromSeconds(15), cancellationToken));
        var wireBytesPerCommit = MeasureWireBytes(databaseId, primary.NodeId, probeCommits) / (double)probeCommits.Length;
        var compressionVsPageFrame = PageFrameBytes / Math.Max(1.0, wireBytesPerCommit);

        var totalPayloadBytes = 0L;
        var totalCommits = 0L;

        var metrics = await ctx.MeasureAsync(
            Suite,
            "repl_stream_1000_commits_throughput",
            1,
            4,
            async _ =>
            {
                var lastBookmark = CausalBookmark.Empty;
                for (var index = 0; index < commitsPerBurst; index++)
                {
                    var id = ++rowId;
                    var written = RequireClusterWrite(await primary.Coordinator.ExecuteWriteAsync(
                        primarySession,
                        InsertStatement(databaseId, "INSERT INTO stream_items (id, payload) VALUES (?, ?);", id, SamplePayload),
                        cancellationToken));
                    lastBookmark = written.Commit.Bookmark;
                    totalPayloadBytes += written.Commit.DeltaPayload.Length;
                    totalCommits++;
                }

                RequireBookmarkReached(await followerSession.WaitForBookmarkAsync(lastBookmark, TimeSpan.FromSeconds(20), cancellationToken));
                VerifyRowCount(
                    RequireRead(await followerSession.ExecuteReadAsync(
                        CountStatement(databaseId, "SELECT COUNT(*) FROM stream_items;", lastBookmark),
                        cancellationToken)),
                    rowId,
                    "repl_stream_1000_commits_throughput follower count");
            },
            () => totalCommits > 0 ? totalPayloadBytes / totalCommits : (long)Math.Round(wireBytesPerCommit),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{commitsPerBurst} commits/op (1000/round) gru->fra mTLS QUIC; FsyncCommitLog=false, loopback; wire_b_per_commit={wireBytesPerCommit:F1}B ({compressionVsPageFrame:F1}x smaller than 4096B page frame); commits/s=ops/s*{commitsPerBurst}"),
            cancellationToken);

        BenchRunnerContext.AssertCondition(primary.Replicator.DroppedPushes == 0, "Streaming replication dropped pushes.");
        var commitsPerSec = metrics.OpsPerSec * commitsPerBurst;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  -> repl_stream_1000_commits_throughput (FsyncCommitLog=false, loopback): {commitsPerSec:F1} commits/s | {wireBytesPerCommit:F1} B/commit ({compressionVsPageFrame:F1}x smaller than 4096B page frame)"));
    }

    private static async Task RunFanout3NodeThroughputAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int commitsPerBurst = 100;
        var databaseId = Ulid.NewUlid();
        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

        await using var cluster = await ReplCluster.StartAsync(
            ctx,
            "repl-fanout3",
            [("gru-1", "gru", 1), ("fra-1", "fra", 2), ("sjc-1", "sjc", 3)],
            [databaseId],
            rawKey,
            fsyncCommitLog: false,
            cancellationToken);

        var primary = cluster.Nodes[0];
        var followerFra = cluster.Nodes[1];
        var followerSjc = cluster.Nodes[2];
        var primarySession = primary.Sessions[databaseId];
        var fraSession = followerFra.Sessions[databaseId];
        var sjcSession = followerSjc.Sessions[databaseId];

        await BootstrapTableAsync(
            primary,
            [followerFra, followerSjc],
            databaseId,
            "CREATE TABLE fanout_items (id INTEGER PRIMARY KEY, payload TEXT);",
            cancellationToken);

        var rowId = 0L;
        var probeWrite = RequireClusterWrite(await primary.Coordinator.ExecuteWriteAsync(
            primarySession,
            InsertStatement(databaseId, "INSERT INTO fanout_items (id, payload) VALUES (?, ?);", ++rowId, SamplePayload),
            cancellationToken));
        await WaitFollowersAsync([fraSession, sjcSession], probeWrite.Commit.Bookmark, TimeSpan.FromSeconds(15), cancellationToken);
        var wireBytesPerCommit = MeasureWireBytes(databaseId, primary.NodeId, [probeWrite.Commit]);

        var totalPayloadBytes = 0L;
        var totalCommits = 0L;

        var metrics = await ctx.MeasureAsync(
            Suite,
            "repl_fanout_3node_throughput",
            1,
            4,
            async _ =>
            {
                var lastBookmark = CausalBookmark.Empty;
                for (var index = 0; index < commitsPerBurst; index++)
                {
                    var id = ++rowId;
                    var written = RequireClusterWrite(await primary.Coordinator.ExecuteWriteAsync(
                        primarySession,
                        InsertStatement(databaseId, "INSERT INTO fanout_items (id, payload) VALUES (?, ?);", id, SamplePayload),
                        cancellationToken));
                    lastBookmark = written.Commit.Bookmark;
                    totalPayloadBytes += written.Commit.DeltaPayload.Length;
                    totalCommits++;
                }

                await WaitFollowersAsync([fraSession, sjcSession], lastBookmark, TimeSpan.FromSeconds(20), cancellationToken);
                VerifyRowCount(
                    RequireRead(await fraSession.ExecuteReadAsync(
                        CountStatement(databaseId, "SELECT COUNT(*) FROM fanout_items;", lastBookmark),
                        cancellationToken)),
                    rowId,
                    "repl_fanout_3node_throughput fra count");
                VerifyRowCount(
                    RequireRead(await sjcSession.ExecuteReadAsync(
                        CountStatement(databaseId, "SELECT COUNT(*) FROM fanout_items;", lastBookmark),
                        cancellationToken)),
                    rowId,
                    "repl_fanout_3node_throughput sjc count");
            },
            () => totalCommits > 0 ? totalPayloadBytes / totalCommits : wireBytesPerCommit,
            string.Create(
                CultureInfo.InvariantCulture,
                $"3-node parallel fanout gru->(fra+sjc) {commitsPerBurst} commits/op; FsyncCommitLog=false, loopback; wire_b_per_commit={wireBytesPerCommit}B; commits/s=ops/s*{commitsPerBurst}"),
            cancellationToken);

        BenchRunnerContext.AssertCondition(primary.Replicator.DroppedPushes == 0, "3-node fanout replication dropped pushes.");
        var commitsPerSec = metrics.OpsPerSec * commitsPerBurst;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  -> repl_fanout_3node_throughput (FsyncCommitLog=false, loopback): {commitsPerSec:F1} primary commits/s ({commitsPerSec * 2.0:F1} follower-applies/s across fra+sjc) | {wireBytesPerCommit} B/commit"));
    }

    private static async Task RunCatchup5000RowsAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int warmup = 1;
        const int iterations = 2;
        const int batchCount = 50;
        const int rowsPerBatch = 100;
        const int totalRows = batchCount * rowsPerBatch;

        var totalRuns = warmup + (ctx.Rounds * ctx.ScaleIterations(iterations));
        var databaseIds = new Ulid[totalRuns];
        for (var index = 0; index < totalRuns; index++)
        {
            databaseIds[index] = Ulid.NewUlid();
        }

        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

        await using var cluster = await ReplCluster.StartAsync(
            ctx,
            "repl-catchup",
            [("gru-1", "gru", 1), ("fra-1", "fra", 2)],
            databaseIds,
            rawKey,
            fsyncCommitLog: false,
            cancellationToken);

        var primary = cluster.Nodes[0];
        var follower = cluster.Nodes[1];
        var seededCommits = new CommitMetadata[totalRuns][];
        var totalWireBytesAllRuns = 0L;

        for (var run = 0; run < totalRuns; run++)
        {
            var databaseId = databaseIds[run];
            var primarySession = primary.Sessions[databaseId];
            var commits = new CommitMetadata[batchCount + 1];

            var ddl = RequireStorageWrite(await primarySession.ExecuteWriteAsync(
                new SqlStatementRequest(
                    databaseId,
                    [new SqlStatement("CREATE TABLE catchup_rows (id INTEGER PRIMARY KEY, val TEXT);", [], NoNamedParams)],
                    SqlAccess.Write,
                    ConsistencyMode.Strong,
                    CausalBookmark.Empty),
                cancellationToken));
            commits[0] = ddl.Commit;

            var rowId = 0L;
            for (var batch = 0; batch < batchCount; batch++)
            {
                var statements = new SqlStatement[rowsPerBatch];
                for (var item = 0; item < rowsPerBatch; item++)
                {
                    var id = ++rowId;
                    statements[item] = new SqlStatement(
                        "INSERT INTO catchup_rows (id, val) VALUES (?, ?);",
                        [SqlValue.FromIntegral(id), SqlValue.FromText(SamplePayload)],
                        NoNamedParams);
                }

                var written = RequireStorageWrite(await primarySession.ExecuteWriteAsync(
                    new SqlStatementRequest(
                        databaseId,
                        statements,
                        SqlAccess.Write,
                        ConsistencyMode.Fast,
                        CausalBookmark.Empty),
                    cancellationToken));
                commits[batch + 1] = written.Commit;
            }

            seededCommits[run] = commits;
            totalWireBytesAllRuns += MeasureWireBytes(databaseId, primary.NodeId, commits);
        }

        var wireBytesPerRun = totalWireBytesAllRuns / (double)totalRuns;
        var wireBytesPerRow = wireBytesPerRun / totalRows;
        var wireOverheadPct = ((wireBytesPerRow - SamplePayload.Length) / SamplePayload.Length) * 100.0;

        var runCursor = 0;
        var metrics = await ctx.MeasureAsync(
            Suite,
            "repl_catchup_5000_rows",
            warmup,
            iterations,
            async _ =>
            {
                var run = runCursor++;
                var databaseId = databaseIds[run];
                var commits = seededCommits[run];
                var followerSession = follower.Sessions[databaseId];

                for (var index = 0; index < commits.Length; index++)
                {
                    primary.Replicator.PublishFast(databaseId, commits[index], ackRequested: false);
                }

                var targetBookmark = commits[^1].Bookmark;
                RequireBookmarkReached(await followerSession.WaitForBookmarkAsync(targetBookmark, TimeSpan.FromSeconds(20), cancellationToken));
                VerifyRowCount(
                    RequireRead(await followerSession.ExecuteReadAsync(
                        CountStatement(databaseId, "SELECT COUNT(*) FROM catchup_rows;", targetBookmark),
                        cancellationToken)),
                    totalRows,
                    "repl_catchup_5000_rows follower count");
            },
            () => (long)Math.Round(wireBytesPerRun),
            string.Create(
                CultureInfo.InvariantCulture,
                $"cold follower catchup {totalRows} rows ({batchCount}x{rowsPerBatch}); FsyncCommitLog=false, loopback; wire={wireBytesPerRun / 1024.0:F1}KiB ({wireBytesPerRow:F1}B/row, +{wireOverheadPct:F1}% over raw payload vs 4KiB page frames); rows/s=ops/s*{totalRows}"),
            cancellationToken);

        BenchRunnerContext.AssertCondition(primary.Replicator.DroppedPushes == 0, "Catchup 5000 rows replication dropped pushes.");
        var rowsPerSec = metrics.OpsPerSec * totalRows;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  -> repl_catchup_5000_rows (FsyncCommitLog=false, loopback): {rowsPerSec:F0} rows/s | p50={metrics.P50Us / 1000.0:F2}ms for 5000 rows | wire={wireBytesPerRun / 1024.0:F1}KiB ({wireBytesPerRow:F1} B/row)"));
    }

    private static async Task RunConcurrent32WritersReplicatedAsync(
        BenchRunnerContext ctx,
        bool fsyncCommitLog,
        CancellationToken cancellationToken)
    {
        const int writers = 32;
        var benchmarkId = fsyncCommitLog
            ? "repl_concurrent_32_writers_replicated_durable"
            : "repl_concurrent_32_writers_replicated";
        var prefix = fsyncCommitLog ? "repl-conc32-durable" : "repl-conc32";
        var databaseId = Ulid.NewUlid();
        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

        await using var cluster = await ReplCluster.StartAsync(
            ctx,
            prefix,
            [("gru-1", "gru", 1), ("fra-1", "fra", 2)],
            [databaseId],
            rawKey,
            fsyncCommitLog,
            cancellationToken);

        var primary = cluster.Nodes[0];
        var follower = cluster.Nodes[1];
        var primarySession = primary.Sessions[databaseId];
        var followerSession = follower.Sessions[databaseId];

        await BootstrapTableAsync(
            primary,
            [follower],
            databaseId,
            "CREATE TABLE conc_items (id INTEGER PRIMARY KEY, payload TEXT);",
            cancellationToken);

        var rowId = 0L;
        var lastBookmark = CausalBookmark.Empty;
        var tasks = new Task<SqlWriteResult>[writers];

        var note = fsyncCommitLog
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"32 writers, FsyncCommitLog=true (durable), loopback, mTLS QUIC + ChaCha20 SQLite; replicated_tx/s=ops/s*{writers} vs 9,500 TPS reference concurrent writes")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"32 concurrent writers + live mTLS QUIC replication gru->fra; FsyncCommitLog=false, loopback; replicated_tx/s=ops/s*{writers} vs 9,500 TPS reference concurrent writes");

        var metrics = await ctx.MeasureAsync(
            Suite,
            benchmarkId,
            5,
            40,
            async _ =>
            {
                var baseId = Interlocked.Add(ref rowId, writers) - writers;
                for (var writer = 0; writer < writers; writer++)
                {
                    var id = baseId + writer + 1;
                    tasks[writer] = ExecuteReplicatedWriteTaskAsync(
                        primary.Coordinator,
                        primarySession,
                        InsertStatement(databaseId, "INSERT INTO conc_items (id, payload) VALUES (?, ?);", id, SamplePayload),
                        cancellationToken);
                }

                var results = await Task.WhenAll(tasks);
                var maxBookmark = results[0].Commit.Bookmark;
                var maxSequence = results[0].Commit.Sequence;
                for (var index = 1; index < results.Length; index++)
                {
                    var commit = results[index].Commit;
                    if (commit.Sequence > maxSequence)
                    {
                        maxSequence = commit.Sequence;
                        maxBookmark = commit.Bookmark;
                    }
                }

                lastBookmark = maxBookmark;
                RequireBookmarkReached(await followerSession.WaitForBookmarkAsync(maxBookmark, TimeSpan.FromSeconds(20), cancellationToken));
            },
            null,
            note,
            cancellationToken);

        BenchRunnerContext.AssertCondition(primary.Replicator.DroppedPushes == 0, string.Concat(benchmarkId, " dropped pushes."));
        VerifyRowCount(
            RequireRead(await followerSession.ExecuteReadAsync(
                CountStatement(databaseId, "SELECT COUNT(*) FROM conc_items;", lastBookmark),
                cancellationToken)),
            Volatile.Read(ref rowId),
            string.Concat(benchmarkId, " follower count"));
        var effectiveTps = metrics.OpsPerSec * writers;
        var vsReferenceRatio = effectiveTps / ReferenceConcurrentTps;
        var modeLabel = fsyncCommitLog ? "FsyncCommitLog=true (durable), loopback" : "FsyncCommitLog=false, loopback";
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  -> {benchmarkId} ({modeLabel}): {effectiveTps:F1} replicated tx/s ({vsReferenceRatio:F2}x 9,500 TPS reference)"));
    }

    private static async Task<SqlWriteResult> ExecuteReplicatedWriteTaskAsync(
        ClusterCoordinator coordinator,
        ISqlDatabaseSession session,
        SqlStatementRequest request,
        CancellationToken cancellationToken) =>
        RequireClusterWrite(await coordinator.ExecuteWriteAsync(session, request, cancellationToken));

    private static async Task BootstrapTableAsync(
        ReplNode primary,
        ReplNode[] followers,
        Ulid databaseId,
        string ddlSql,
        CancellationToken cancellationToken)
    {
        var ddl = RequireClusterWrite(await primary.Coordinator.ExecuteLocalBootstrapWriteAsync(
            primary.Sessions[databaseId],
            new SqlStatementRequest(
                databaseId,
                [new SqlStatement(ddlSql, [], NoNamedParams)],
                SqlAccess.Write,
                ConsistencyMode.Strong,
                CausalBookmark.Empty),
            cancellationToken));

        var followerSessions = new ISqlDatabaseSession[followers.Length];
        for (var index = 0; index < followers.Length; index++)
        {
            followerSessions[index] = followers[index].Sessions[databaseId];
        }

        await WaitFollowersAsync(followerSessions, ddl.Commit.Bookmark, TimeSpan.FromSeconds(15), cancellationToken);
    }

    private static Task WaitFollowersAsync(
        ISqlDatabaseSession[] followers,
        CausalBookmark bookmark,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (followers.Length == 1)
        {
            return WaitSingleFollowerAsync(followers[0], bookmark, timeout, cancellationToken);
        }

        var waits = new Task[followers.Length];
        for (var index = 0; index < followers.Length; index++)
        {
            waits[index] = WaitSingleFollowerAsync(followers[index], bookmark, timeout, cancellationToken);
        }

        return Task.WhenAll(waits);
    }

    private static async Task WaitSingleFollowerAsync(
        ISqlDatabaseSession session,
        CausalBookmark bookmark,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RequireBookmarkReached(await session.WaitForBookmarkAsync(bookmark, timeout, cancellationToken));

    private static SqlStatementRequest InsertStatement(Ulid databaseId, string sql, long id, string payload) =>
        new(
            databaseId,
            [new SqlStatement(sql, [SqlValue.FromIntegral(id), SqlValue.FromText(payload)], NoNamedParams)],
            SqlAccess.Write,
            ConsistencyMode.Fast,
            CausalBookmark.Empty);

    private static SqlStatementRequest SelectByIdStatement(Ulid databaseId, string sql, long id, CausalBookmark after) =>
        new(
            databaseId,
            [new SqlStatement(sql, [SqlValue.FromIntegral(id)], NoNamedParams)],
            SqlAccess.Read,
            ConsistencyMode.Fast,
            after);

    private static SqlStatementRequest CountStatement(Ulid databaseId, string sql, CausalBookmark after) =>
        new(
            databaseId,
            [new SqlStatement(sql, [], NoNamedParams)],
            SqlAccess.Read,
            ConsistencyMode.Fast,
            after);

    private static long MeasureWireBytes(Ulid databaseId, string senderNodeId, IReadOnlyList<CommitMetadata> commits)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        var message = new DeltaPushMessage(databaseId, senderNodeId, commits, false);
        message.WriteTo(buffer);
        return buffer.WrittenCount;
    }

    private static void VerifyStringValue(SqlReadResult read, string expected, string stage)
    {
        if (read.ResultSets.Count != 1
            || read.ResultSets[0].Rows.Count != 1
            || !read.ResultSets[0].Rows[0][0].TryGetText(out var actual)
            || !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(string.Concat("Follower verification failed at ", stage, "."));
        }
    }

    private static void VerifyRowCount(SqlReadResult read, long expected, string stage)
    {
        if (read.ResultSets.Count != 1
            || read.ResultSets[0].Rows.Count != 1
            || !read.ResultSets[0].Rows[0][0].TryGetIntegral(out var actual)
            || actual != expected)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Follower row count mismatch at {stage}: expected {expected}."));
        }
    }

    private static async ValueTask<DatabaseResolveOutcome> ResolveDatabaseAsync(
        SqliteStorageEngine engine,
        Ulid databaseId,
        ReadOnlyMemory<byte> rawKey,
        CancellationToken cancellationToken)
    {
        var opened = await engine.OpenAsync(new DatabaseOpenRequest(databaseId, rawKey), cancellationToken);
        return opened switch
        {
            ISqlDatabaseSession session => session,
            DatabaseNotFound missing => missing,
            InvalidDatabaseRequest invalid => new ClusterUnavailable(invalid.Message, invalid.Code),
            StorageCorrupted corrupted => new ClusterUnavailable(corrupted.Message, "storage_corrupted"),
            StorageBusy busy => new ClusterUnavailable(busy.Message, "storage_busy"),
        };
    }

    private static IPEndPoint RequireHostStarted(QuicHostStartOutcome outcome)
    {
        var (bound, error) = outcome switch
        {
            IPEndPoint endPoint => (endPoint, (string?)null),
            QuicUnavailable unavailable => ((IPEndPoint?)null, unavailable.Message),
        };
        if (error is not null || bound is not { } started)
        {
            throw new InvalidOperationException(error);
        }

        return started;
    }

    private static ISqlDatabaseSession RequireCreated(CreateDatabaseOutcome outcome)
    {
        var (session, error) = outcome switch
        {
            ISqlDatabaseSession created => (created, (string?)null),
            DatabaseAlreadyExists exists => ((ISqlDatabaseSession?)null, exists.Message),
            InvalidDatabaseRequest invalid => ((ISqlDatabaseSession?)null, invalid.Message),
            StorageBusy busy => ((ISqlDatabaseSession?)null, busy.Message),
        };
        if (error is not null || session is not { } ready)
        {
            throw new InvalidOperationException(error);
        }

        return ready;
    }

    private static SqlWriteResult RequireStorageWrite(SqlWriteOutcome outcome)
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
        if (error is not null || result is not { } ok)
        {
            throw new InvalidOperationException(error);
        }

        return ok;
    }

    private static SqlWriteResult RequireClusterWrite(ClusterWriteOutcome outcome)
    {
        var (result, error) = outcome switch
        {
            SqlWriteResult written => (written, (string?)null),
            SqlError sql => ((SqlWriteResult?)null, sql.Message),
            SchemaMismatch mismatch => ((SqlWriteResult?)null, string.Concat("schema mismatch: ", mismatch.ActualEpoch.ToString(CultureInfo.InvariantCulture))),
            StorageBusy busy => ((SqlWriteResult?)null, busy.Message),
            SerializationConflict conflict => ((SqlWriteResult?)null, conflict.Reason),
            ClusterUnavailable unavailable => ((SqlWriteResult?)null, unavailable.Message),
            ClusterTimeout timeout => ((SqlWriteResult?)null, timeout.Message),
        };
        if (error is not null || result is not { } ok)
        {
            throw new InvalidOperationException(error);
        }

        return ok;
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
        if (error is not null || result is not { } ok)
        {
            throw new InvalidOperationException(error);
        }

        return ok;
    }

    private static void RequireBookmarkReached(WaitForBookmarkOutcome outcome)
    {
        var error = outcome switch
        {
            BookmarkReached => (string?)null,
            StorageTimeout timeout => timeout.Message,
        };
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    private static void DisposeCertificates(QuicMeshCertificates certificates)
    {
        certificates.Certificate.Dispose();
        foreach (var authority in certificates.TrustedAuthorities)
        {
            authority.Dispose();
        }

        foreach (var intermediate in certificates.Intermediates)
        {
            intermediate.Dispose();
        }
    }

    private sealed class ReplCluster(BenchMeterFactory meterFactory, ReplNode[] nodes) : IAsyncDisposable
    {
        public ReplNode[] Nodes => nodes;

        public static async Task<ReplCluster> StartAsync(
            BenchRunnerContext ctx,
            string prefix,
            (string NodeId, string Region, ushort NumericId)[] specs,
            Ulid[] databaseIds,
            ReadOnlyMemory<byte> rawKey,
            bool fsyncCommitLog,
            CancellationToken cancellationToken)
        {
            var secret = RandomNumberGenerator.GetBytes(32);
            var meterFactory = new BenchMeterFactory();
            var metrics = new QuicMeshMetrics(meterFactory);
            var emptyChangefeed = new ChangefeedPublisher([], TimeProvider.System);
            ArchiveCatchUp noArchive = static (_, _, _) => ValueTask.FromResult<ArchiveCatchUpOutcome>(new ArchiveCatchUpProgress(0));

            var nodes = new ReplNode[specs.Length];
            for (var index = 0; index < specs.Length; index++)
            {
                var (nodeId, region, numericId) = specs[index];
                var dir = ctx.RunDir(string.Concat(prefix, "-", nodeId));
                var certs = QuicMeshPki.DeriveNode(secret, nodeId, region, TimeProvider.System);
                List<PeerNodeInfo> peers = [];
                for (var peerIndex = 0; peerIndex < specs.Length; peerIndex++)
                {
                    if (peerIndex != index)
                    {
                        var peer = specs[peerIndex];
                        peers.Add(new PeerNodeInfo(peer.NodeId, peer.Region, "127.0.0.1", 12000 + peerIndex, 0));
                    }
                }

                var options = new ClusterOptions
                {
                    NodeId = nodeId,
                    Region = region,
                    DataDirectory = Path.Combine(dir, "cluster"),
                    ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                    Peers = peers,
                    Certificates = certs,
                    RaftShardCount = 1,
                }.Validate();

                var storageOptions = new StorageEngineOptions(dir, options.StorageOrigin, numericId, (byte)numericId, new DeltaColumnRegistry())
                {
                    FsyncCommitLog = fsyncCommitLog,
                };
                var engine = new SqliteStorageEngine(storageOptions, TimeProvider.System);
                var sessions = new Dictionary<Ulid, ISqlDatabaseSession>(databaseIds.Length);
                var databases = new ClusterDatabases((id, token) => ResolveDatabaseAsync(engine, id, rawKey, token), TimeProvider.System);
                for (var dbIndex = 0; dbIndex < databaseIds.Length; dbIndex++)
                {
                    var dbId = databaseIds[dbIndex];
                    var session = RequireCreated(await engine.CreateAsync(new DatabaseCreateRequest(dbId, rawKey, false), cancellationToken));
                    sessions[dbId] = session;
                    databases.Track(dbId);
                }

                var topology = new ClusterTopology(options);
                var host = new QuicRpcHost(options.ToMeshOptions(), metrics, TimeProvider.System, NullLogger<QuicRpcHost>.Instance);
                var client = new QuicRpcClient(options.ToMeshOptions(), metrics, TimeProvider.System, NullLogger<QuicRpcClient>.Instance);
                var locks = new StrongLockTable(TimeProvider.System);
                var registry = new StrongTransactionRegistry();
                var health = new PeerHealthMonitor(options, topology, client, databases, TimeProvider.System, NullLogger<PeerHealthMonitor>.Instance);
                var replicator = new DeltaReplicator(topology, client, databases, emptyChangefeed, locks, TimeProvider.System, NullLogger<DeltaReplicator>.Instance);
                var raft = new InRegionRaftRouter(options, topology, client, replicator, NullLoggerFactory.Instance);
                var finalizer = new CommitFinalizer(raft, replicator, databases, emptyChangefeed, locks, NullLogger<CommitFinalizer>.Instance);
                var antiEntropy = new AntiEntropyService(options, topology, databases, noArchive, replicator, client, health, TimeProvider.System, NullLogger<AntiEntropyService>.Instance);
                var participant = new StrongParticipant(options, topology, databases, locks, registry, replicator, antiEntropy, client, engine.Clock, NullLogger<StrongParticipant>.Instance);
                var votes = new StrongVoteCollector(options, topology, health, participant, client, TimeProvider.System);
                var recovery = new StrongCommitRecovery(TimeProvider.System, NullLogger<StrongCommitRecovery>.Instance);
                var strong = new StrongCoordinator(topology, databases, votes, registry, finalizer, antiEntropy, recovery, engine.Clock, TimeProvider.System, NullLogger<StrongCoordinator>.Instance);
                var coordinator = new ClusterCoordinator(topology, databases, finalizer, strong, health);

                replicator.Map(host);
                participant.Map(host);
                antiEntropy.Map(host);
                raft.Map(host);

                var bound = RequireHostStarted(await host.StartAsync(cancellationToken));
                await raft.StartAsync(cancellationToken);

                nodes[index] = new ReplNode(
                    nodeId,
                    region,
                    certs,
                    engine,
                    sessions,
                    host,
                    client,
                    replicator,
                    raft,
                    coordinator,
                    bound);
            }

            for (var i = 0; i < nodes.Length; i++)
            {
                for (var j = 0; j < nodes.Length; j++)
                {
                    if (i != j)
                    {
                        nodes[i].Client.UpsertPeer(new QuicPeer(nodes[j].NodeId, nodes[j].Region, nodes[j].BoundEndPoint));
                    }
                }
            }

            return new ReplCluster(meterFactory, nodes);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in nodes)
            {
                await node.Raft.DisposeAsync();
            }

            foreach (var node in nodes)
            {
                await node.Replicator.DisposeAsync();
            }

            foreach (var node in nodes)
            {
                await node.Client.DisposeAsync();
            }

            foreach (var node in nodes)
            {
                await node.Host.DisposeAsync();
            }

            foreach (var node in nodes)
            {
                foreach (var session in node.Sessions.Values)
                {
                    await session.DisposeAsync();
                }

                await node.Engine.DisposeAsync();
                DisposeCertificates(node.Certificates);
            }

            meterFactory.Dispose();
        }
    }

    private sealed record ReplNode(
        string NodeId,
        string Region,
        QuicMeshCertificates Certificates,
        SqliteStorageEngine Engine,
        IReadOnlyDictionary<Ulid, ISqlDatabaseSession> Sessions,
        QuicRpcHost Host,
        QuicRpcClient Client,
        DeltaReplicator Replicator,
        InRegionRaftRouter Raft,
        ClusterCoordinator Coordinator,
        IPEndPoint BoundEndPoint);

    private sealed class BenchMeterFactory : IMeterFactory
    {
        private readonly Lock _gate = new();
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            lock (_gate)
            {
                _meters.Add(meter);
            }

            return meter;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var meter in _meters)
                {
                    meter.Dispose();
                }

                _meters.Clear();
            }
        }
    }
}
