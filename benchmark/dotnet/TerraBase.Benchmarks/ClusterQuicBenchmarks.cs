using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
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
using QuicWireReader = TerraScale.QuicRpc.WireReader;

namespace TerraBase.Benchmarks;

public static class ClusterQuicBenchmarks
{
    private const ushort BenchUnaryMessageType = 0x0601;
    private const ushort BenchPushMessageType = 0x0602;
    private const int UnaryPayloadBytes = 1024;
    private const int PushPayloadBytes = 16 * 1024;

    private static readonly IReadOnlyDictionary<string, SqlValue> NoNamedParams = FrozenDictionary<string, SqlValue>.Empty;

    public static async Task RunAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        QuicPlatform.EnsureInitialized();
        if (!QuicPlatform.IsSupported)
        {
            throw new PlatformNotSupportedException("System.Net.Quic is required for cluster and QUIC benchmarks.");
        }

        if (ctx.ShouldRun("cluster"))
        {
            RunCodecCommitsRoundtrip(ctx, cancellationToken);
            RunStrongLockTableAcquireRelease(ctx, cancellationToken);
        }

        if (ctx.ShouldRun("quic"))
        {
            await RunQuicLoopbackBenchmarksAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRun("cluster"))
        {
            await RunDeltaReplicatorApplyLoopbackAsync(ctx, cancellationToken);
            await RunRaftSingleVsThreeNodeCommitAsync(ctx, cancellationToken);
        }
    }

    private static void RunCodecCommitsRoundtrip(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int commitCount = 16;
        var databaseId = Ulid.NewUlid();
        var commits = new CommitMetadata[commitCount];
        for (var index = 0; index < commitCount; index++)
        {
            var payload = new byte[UnaryPayloadBytes];
            RandomNumberGenerator.Fill(payload);
            var bookmark = CausalBookmark.Create(
            [
                new("us-east-1", index + 1),
                new("sa-east-1", 100 + index),
                new("eu-west-1", 42),
            ]);
            commits[index] = new CommitMetadata(
                Ulid.NewUlid(),
                "us-east-1",
                index + 1,
                new HlcTimestamp(1_760_000_000_000L + index, (ushort)index, 1),
                3,
                (index & 1) == 0 ? ConsistencyMode.Fast : ConsistencyMode.Strong,
                bookmark,
                payload);
        }

        var batch = new DeltaPushMessage(databaseId, "node-a", commits, true);
        var buffer = new ArrayBufferWriter<byte>(commitCount * (UnaryPayloadBytes + 128));
        var encodedBytes = 0L;

        _ = ctx.MeasureSync(
            "cluster",
            "codec_commits_roundtrip",
            50,
            500,
            _ =>
            {
                buffer.ResetWrittenCount();
                batch.WriteTo(buffer);
                encodedBytes = buffer.WrittenCount;
                if (!BinaryMessage.TryDecode<DeltaPushMessage>(buffer.WrittenSpan, out var decoded))
                {
                    throw new InvalidOperationException("DeltaPushMessage ClusterCodec decoding failed.");
                }

                if (decoded.DatabaseId != databaseId
                    || !string.Equals(decoded.SenderNodeId, "node-a", StringComparison.Ordinal)
                    || !decoded.AckRequested
                    || decoded.Commits.Count != commitCount
                    || decoded.Commits[0].Sequence != 1
                    || decoded.Commits[^1].Sequence != commitCount
                    || decoded.Commits[^1].DeltaPayload.Length != UnaryPayloadBytes
                    || decoded.Commits[^1].Bookmark.Get("eu-west-1") != 42)
                {
                    throw new InvalidOperationException("Decoded commit batch did not match original payload.");
                }
            },
            () => encodedBytes,
            "16 commits x 1 KiB ClusterCodec DeltaPushMessage encode/decode",
            cancellationToken);
    }

    private static void RunStrongLockTableAcquireRelease(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int baselineCount = 64;
        var timeProvider = TimeProvider.System;
        var table = new StrongLockTable(timeProvider);
        var databaseId = Ulid.NewUlid();
        var baselineTxIds = new Ulid[baselineCount];
        var longDeadline = table.LeaseDeadline(TimeSpan.FromMinutes(10));
        var emptyRows = FrozenSet<RowKey>.Empty;
        var emptyTables = FrozenSet<string>.Empty;
        var accountsTable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "accounts" };
        var ordersTable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };
        var ledgerTable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ledger" };

        for (var index = 0; index < baselineCount; index++)
        {
            var txId = Ulid.NewUlid();
            baselineTxIds[index] = txId;
            var holder = new StrongLockHolder(
                databaseId,
                txId,
                new HlcTimestamp(1_000_000L + index, 0, 1),
                "coord-seed",
                new HashSet<RowKey> { new("accounts", string.Concat("seed-", index.ToString(CultureInfo.InvariantCulture))) },
                emptyRows,
                accountsTable,
                emptyTables,
                false,
                longDeadline);
            RequireGranted(table.TryAcquire(holder), "baseline seed holder");
        }

        if (table.ActiveHolders != baselineCount)
        {
            throw new InvalidOperationException("StrongLockTable did not hold expected 64 baseline locks.");
        }

        var step = 0L;
        _ = ctx.MeasureSync(
            "cluster",
            "strong_lock_table_acquire_release",
            50,
            500,
            _ =>
            {
                var current = ++step;
                var deadline = table.LeaseDeadline(TimeSpan.FromSeconds(30));

                var freeTx = Ulid.NewUlid();
                var freeHolder = new StrongLockHolder(
                    databaseId,
                    freeTx,
                    new HlcTimestamp(1_500_000L + current, 0, 1),
                    "coord-a",
                    new HashSet<RowKey> { new("accounts", string.Concat("free-", current.ToString(CultureInfo.InvariantCulture))) },
                    emptyRows,
                    accountsTable,
                    emptyTables,
                    false,
                    deadline);
                RequireGranted(table.TryAcquire(freeHolder), "uncontended lock");
                if (table.ActiveHolders != baselineCount + 1 || !table.Release(databaseId, freeTx))
                {
                    throw new InvalidOperationException("Uncontended lock acquire/release invariant failed.");
                }

                var hotKey = new HashSet<RowKey> { new("orders", "hot-row") };
                var victimTx = Ulid.NewUlid();
                var victimHolder = new StrongLockHolder(
                    databaseId,
                    victimTx,
                    new HlcTimestamp(3_000_000L + current, 0, 2),
                    "coord-b",
                    hotKey,
                    emptyRows,
                    ordersTable,
                    emptyTables,
                    false,
                    deadline);
                RequireGranted(table.TryAcquire(victimHolder), "wound victim");

                var winnerTx = Ulid.NewUlid();
                var winnerHolder = new StrongLockHolder(
                    databaseId,
                    winnerTx,
                    new HlcTimestamp(2_000_000L + current, 0, 1),
                    "coord-a",
                    hotKey,
                    emptyRows,
                    ordersTable,
                    emptyTables,
                    false,
                    deadline);
                var victims = RequireWound(table.TryAcquire(winnerHolder));
                if (victims.Count != 1 || victims[0].TxId != victimTx)
                {
                    throw new InvalidOperationException("Wound conflict did not identify expected victim.");
                }

                var woundsBefore = table.Wounds;
                table.Wound(victims[0]);
                if (table.Wounds != woundsBefore + 1)
                {
                    throw new InvalidOperationException("StrongLockTable did not increment wound count.");
                }

                RequireGranted(table.TryAcquire(winnerHolder), "post-wound winner");
                if (!table.Release(databaseId, winnerTx))
                {
                    throw new InvalidOperationException("Post-wound winner lock release failed.");
                }

                var expiringKey = new HashSet<RowKey> { new("ledger", "expiring-row") };
                var expiredTx = Ulid.NewUlid();
                var expiredHolder = new StrongLockHolder(
                    databaseId,
                    expiredTx,
                    new HlcTimestamp(4_000_000L + current, 0, 3),
                    "coord-c",
                    expiringKey,
                    emptyRows,
                    ledgerTable,
                    emptyTables,
                    false,
                    timeProvider.GetTimestamp() - 1);
                RequireGranted(table.TryAcquire(expiredHolder), "expiring holder");

                var expiredBefore = table.ExpiredLeases;
                var replacementTx = Ulid.NewUlid();
                var replacementHolder = new StrongLockHolder(
                    databaseId,
                    replacementTx,
                    new HlcTimestamp(5_000_000L + current, 0, 4),
                    "coord-d",
                    expiringKey,
                    emptyRows,
                    ledgerTable,
                    emptyTables,
                    false,
                    deadline);
                RequireGranted(table.TryAcquire(replacementHolder), "post-expiry replacement");
                if (table.ExpiredLeases != expiredBefore + 1 || !table.Release(databaseId, replacementTx))
                {
                    throw new InvalidOperationException("Expired lease eviction invariant failed.");
                }

                if (table.ActiveHolders != baselineCount)
                {
                    throw new InvalidOperationException("StrongLockTable active holder count drifted from 64.");
                }
            },
            null,
            "64 active holders + acquire/release + wound + expiry",
            cancellationToken);

        foreach (var txId in baselineTxIds)
        {
            if (!table.Release(databaseId, txId))
            {
                throw new InvalidOperationException("Failed to release baseline holder.");
            }
        }

        if (table.ActiveHolders != 0)
        {
            throw new InvalidOperationException("StrongLockTable did not drain to zero active holders.");
        }
    }

    private static async Task RunQuicLoopbackBenchmarksAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var serverCerts = QuicMeshPki.DeriveNode(secret, "bench-server", "us-east-1", TimeProvider.System);
        var clientCerts = QuicMeshPki.DeriveNode(secret, "bench-client", "us-east-1", TimeProvider.System);
        using var meterFactory = new BenchMeterFactory();
        var metrics = new QuicMeshMetrics(meterFactory);
        var serverOptions = new QuicMeshOptions
        {
            NodeId = "bench-server",
            Region = "us-east-1",
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Certificate = serverCerts.Certificate,
            TrustedAuthorities = serverCerts.TrustedAuthorities,
            IntermediateCertificates = serverCerts.Intermediates,
            CertificateRotator = serverCerts.Rotator,
        };
        var clientOptions = new QuicMeshOptions
        {
            NodeId = "bench-client",
            Region = "us-east-1",
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Certificate = clientCerts.Certificate,
            TrustedAuthorities = clientCerts.TrustedAuthorities,
            IntermediateCertificates = clientCerts.Intermediates,
            CertificateRotator = clientCerts.Rotator,
        };

        var pushGate = new Lock();
        var expectedPushSeq = 0;
        TaskCompletionSource<int>? pendingPush = null;
        var unaryBytes = new byte[UnaryPayloadBytes];
        var pushBytes = new byte[PushPayloadBytes];
        RandomNumberGenerator.Fill(unaryBytes);
        RandomNumberGenerator.Fill(pushBytes);

        await using var host = new QuicRpcHost(serverOptions, metrics, TimeProvider.System, NullLogger<QuicRpcHost>.Instance);
        _ = host.MapUnary<BlobFrameMessage, BlobFrameMessage>(
            BenchUnaryMessageType,
            static (_, request, _) =>
            {
                if (request.Payload.Length != UnaryPayloadBytes)
                {
                    return ValueTask.FromResult<QuicHandlerOutcome<BlobFrameMessage>>(new QuicRemoteError("bad_size", "unexpected unary payload size"));
                }

                return ValueTask.FromResult<QuicHandlerOutcome<BlobFrameMessage>>(new BlobFrameMessage(request.Sequence, request.Payload));
            });
        _ = host.MapPush<BlobFrameMessage>(
            BenchPushMessageType,
            (_, message, _) =>
            {
                if (message.Payload.Length != PushPayloadBytes)
                {
                    return ValueTask.FromResult<PushDispatchOutcome>(new QuicRemoteError("bad_size", "unexpected push payload size"));
                }

                TaskCompletionSource<int>? signal = null;
                lock (pushGate)
                {
                    if (pendingPush is not null && message.Sequence == expectedPushSeq)
                    {
                        signal = pendingPush;
                        pendingPush = null;
                    }
                }

                _ = signal?.TrySetResult(message.Payload.Length);
                return ValueTask.FromResult<PushDispatchOutcome>(new QuicSent());
            });

        var started = await host.StartAsync(cancellationToken);
        var boundEndPoint = RequireHostStarted(started);

        await using var client = new QuicRpcClient(clientOptions, metrics, TimeProvider.System, NullLogger<QuicRpcClient>.Instance);
        client.UpsertPeer(new QuicPeer("bench-server", "us-east-1", boundEndPoint));

        var unarySeq = 0;
        await ctx.MeasureAsync(
            "quic",
            "quic_unary_rpc_1kb",
            20,
            200,
            async _ =>
            {
                var seq = ++unarySeq;
                var outcome = await client.CallAsync<BlobFrameMessage, BlobFrameMessage>(
                    "bench-server",
                    BenchUnaryMessageType,
                    new BlobFrameMessage(seq, unaryBytes),
                    cancellationToken);
                var reply = RequireCallReply(outcome);
                if (reply.Sequence != seq || reply.Payload.Length != UnaryPayloadBytes)
                {
                    throw new InvalidOperationException("QUIC unary echo reply did not match request.");
                }
            },
            () => UnaryPayloadBytes,
            "loopback mTLS QUIC unary RPC 1 KiB echo",
            cancellationToken);

        var pushSeq = 0;
        _ = await ctx.MeasureAsync(
            "quic",
            "quic_push_stream_16kb",
            20,
            200,
            async _ =>
            {
                var seq = ++pushSeq;
                var arrived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (pushGate)
                {
                    expectedPushSeq = seq;
                    pendingPush = arrived;
                }

                var pushed = await client.PushAsync(
                    "bench-server",
                    BenchPushMessageType,
                    new BlobFrameMessage(seq, pushBytes),
                    "bench-stream",
                    cancellationToken);
                RequirePushed(pushed);
                var receivedBytes = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                if (receivedBytes != PushPayloadBytes)
                {
                    throw new InvalidOperationException("QUIC push stream server did not receive expected 16 KiB payload.");
                }
            },
            () => PushPayloadBytes,
            "loopback mTLS QUIC ordered push stream 16 KiB",
            cancellationToken);

        DisposeCertificates(serverCerts);
        DisposeCertificates(clientCerts);
    }

    private static async Task RunDeltaReplicatorApplyLoopbackAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int warmup = 10;
        const int iterations = 100;
        var totalCommits = warmup + (ctx.Rounds * ctx.ScaleIterations(iterations));
        var secret = RandomNumberGenerator.GetBytes(32);
        var databaseId = Ulid.NewUlid();
        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

        var dirA = ctx.RunDir("delta-node-a");
        var dirB = ctx.RunDir("delta-node-b");
        var certsA = QuicMeshPki.DeriveNode(secret, "node-a", "us-east-1", TimeProvider.System);
        var certsB = QuicMeshPki.DeriveNode(secret, "node-b", "sa-east-1", TimeProvider.System);

        using var meterFactory = new BenchMeterFactory();
        var metrics = new QuicMeshMetrics(meterFactory);

        var storageA = new StorageEngineOptions(dirA, "us-east-1", 1, 1, new DeltaColumnRegistry()) { FsyncCommitLog = false };
        var storageB = new StorageEngineOptions(dirB, "sa-east-1", 2, 2, new DeltaColumnRegistry()) { FsyncCommitLog = false };

        await using var engineA = new SqliteStorageEngine(storageA, TimeProvider.System);
        await using var engineB = new SqliteStorageEngine(storageB, TimeProvider.System);
        await using var sessionA = RequireCreated(await engineA.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));
        await using var sessionB = RequireCreated(await engineB.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));

        var optionsA = new ClusterOptions
        {
            NodeId = "node-a",
            Region = "us-east-1",
            DataDirectory = Path.Combine(dirA, "cluster"),
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Peers = [new PeerNodeInfo("node-b", "sa-east-1", "127.0.0.1", 1, 0)],
            Certificates = certsA,
        }.Validate();
        var optionsB = new ClusterOptions
        {
            NodeId = "node-b",
            Region = "sa-east-1",
            DataDirectory = Path.Combine(dirB, "cluster"),
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Peers = [new PeerNodeInfo("node-a", "us-east-1", "127.0.0.1", 2, 0)],
            Certificates = certsB,
        }.Validate();

        var databasesA = new ClusterDatabases((id, token) => ResolveDatabaseAsync(engineA, id, rawKey, token), TimeProvider.System);
        var databasesB = new ClusterDatabases((id, token) => ResolveDatabaseAsync(engineB, id, rawKey, token), TimeProvider.System);
        databasesA.Track(databaseId);
        databasesB.Track(databaseId);

        var changefeed = new ChangefeedPublisher([], TimeProvider.System);
        var locksA = new StrongLockTable(TimeProvider.System);
        var locksB = new StrongLockTable(TimeProvider.System);

        await using var hostA = new QuicRpcHost(optionsA.ToMeshOptions(), metrics, TimeProvider.System, NullLogger<QuicRpcHost>.Instance);
        await using var hostB = new QuicRpcHost(optionsB.ToMeshOptions(), metrics, TimeProvider.System, NullLogger<QuicRpcHost>.Instance);
        await using var clientA = new QuicRpcClient(optionsA.ToMeshOptions(), metrics, TimeProvider.System, NullLogger<QuicRpcClient>.Instance);
        await using var clientB = new QuicRpcClient(optionsB.ToMeshOptions(), metrics, TimeProvider.System, NullLogger<QuicRpcClient>.Instance);
        await using var replicatorA = new DeltaReplicator(new ClusterTopology(optionsA), clientA, databasesA, changefeed, locksA, TimeProvider.System, NullLogger<DeltaReplicator>.Instance);
        await using var replicatorB = new DeltaReplicator(new ClusterTopology(optionsB), clientB, databasesB, changefeed, locksB, TimeProvider.System, NullLogger<DeltaReplicator>.Instance);

        replicatorA.Map(hostA);
        replicatorB.Map(hostB);

        var boundA = RequireHostStarted(await hostA.StartAsync(cancellationToken));
        var boundB = RequireHostStarted(await hostB.StartAsync(cancellationToken));
        clientA.UpsertPeer(new QuicPeer("node-b", "sa-east-1", boundB));
        clientB.UpsertPeer(new QuicPeer("node-a", "us-east-1", boundA));

        var ddl = RequireWrite(await sessionA.ExecuteWriteAsync(
            new SqlStatementRequest(
                databaseId,
                [new SqlStatement("CREATE TABLE kv (id INTEGER PRIMARY KEY, val TEXT);", [], NoNamedParams)],
                SqlAccess.Write,
                ConsistencyMode.Strong,
                CausalBookmark.Empty),
            cancellationToken));
        replicatorA.PublishFast(databaseId, ddl.Commit, ackRequested: true);
        RequireBookmarkReached(await sessionB.WaitForBookmarkAsync(ddl.Commit.Bookmark, TimeSpan.FromSeconds(10), cancellationToken));

        var commits = new CommitMetadata[totalCommits];
        var payloadText = new string('x', 512);
        var totalPayloadBytes = 0L;
        for (var index = 0; index < totalCommits; index++)
        {
            var write = RequireWrite(await sessionA.ExecuteWriteAsync(
                new SqlStatementRequest(
                    databaseId,
                    [new SqlStatement("INSERT INTO kv (id, val) VALUES (?, ?);", [SqlValue.FromIntegral(index + 1), SqlValue.FromText(payloadText)], NoNamedParams)],
                    SqlAccess.Write,
                    ConsistencyMode.Fast,
                    CausalBookmark.Empty),
                cancellationToken));
            commits[index] = write.Commit;
            totalPayloadBytes += write.Commit.DeltaPayload.Length;
        }

        var nextIndex = 0;
        await ctx.MeasureAsync(
            "cluster",
            "delta_replicator_apply_loopback",
            warmup,
            iterations,
            async _ =>
            {
                var commit = commits[nextIndex++];
                replicatorA.PublishFast(databaseId, commit, ackRequested: true);
                RequireBookmarkReached(await sessionB.WaitForBookmarkAsync(commit.Bookmark, TimeSpan.FromSeconds(10), cancellationToken));
            },
            () => totalPayloadBytes / totalCommits,
            "2-node QUIC DeltaReplicator -> remote SqliteStorageEngine apply",
            cancellationToken);

        if (replicatorA.DroppedPushes != 0 || replicatorB.AppliedRemoteCommits != totalCommits + 1)
        {
            throw new InvalidOperationException("DeltaReplicator apply count or dropped push invariant failed.");
        }

        var read = RequireRead(await sessionB.ExecuteReadAsync(
            new SqlStatementRequest(
                databaseId,
                [new SqlStatement("SELECT COUNT(*) FROM kv;", [], NoNamedParams)],
                SqlAccess.Read,
                ConsistencyMode.Fast,
                commits[^1].Bookmark),
            cancellationToken));
        if (!read.ResultSets[0].Rows[0][0].TryGetIntegral(out var rowCount) || rowCount != totalCommits)
        {
            throw new InvalidOperationException("Remote SqliteStorageEngine row count did not match replicated commits.");
        }

        DisposeCertificates(certsA);
        DisposeCertificates(certsB);
    }

    private static async Task RunRaftSingleVsThreeNodeCommitAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int warmup = 5;
        const int iterations = 40;
        var secret = RandomNumberGenerator.GetBytes(32);
        var databaseId = Ulid.NewUlid();
        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

        using var meterFactory = new BenchMeterFactory();
        var metrics = new QuicMeshMetrics(meterFactory);

        var singleDir = ctx.RunDir("raft-single");
        var singleCerts = QuicMeshPki.DeriveNode(secret, "raft-solo", "us-east-1", TimeProvider.System);
        var singleOptions = new ClusterOptions
        {
            NodeId = "raft-solo",
            Region = "us-east-1",
            DataDirectory = Path.Combine(singleDir, "cluster"),
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Peers = [],
            Certificates = singleCerts,
            RaftShardCount = 1,
        }.Validate();

        var singleStorage = new StorageEngineOptions(singleDir, singleOptions.StorageOrigin, 10, 10, new DeltaColumnRegistry()) { FsyncCommitLog = false };
        await using var singleEngine = new SqliteStorageEngine(singleStorage, TimeProvider.System);
        await using var singleSession = RequireCreated(await singleEngine.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));
        var singleDatabases = new ClusterDatabases((id, token) => ResolveDatabaseAsync(singleEngine, id, rawKey, token), TimeProvider.System);
        singleDatabases.Track(databaseId);
        var emptyChangefeed = new ChangefeedPublisher([], TimeProvider.System);
        var singleLocks = new StrongLockTable(TimeProvider.System);
        await using var singleClient = new QuicRpcClient(singleOptions.ToMeshOptions(), metrics, TimeProvider.System, NullLogger<QuicRpcClient>.Instance);
        await using var singleReplicator = new DeltaReplicator(new ClusterTopology(singleOptions), singleClient, singleDatabases, emptyChangefeed, singleLocks, TimeProvider.System, NullLogger<DeltaReplicator>.Instance);
        await using var singleRaft = new InRegionRaftRouter(singleOptions, new ClusterTopology(singleOptions), singleClient, singleReplicator, NullLoggerFactory.Instance);
        var singleFinalizer = new CommitFinalizer(singleRaft, singleReplicator, singleDatabases, emptyChangefeed, singleLocks, NullLogger<CommitFinalizer>.Instance);
        await singleRaft.StartAsync(cancellationToken);

        _ = RequireWrite(await singleSession.ExecuteWriteAsync(
            new SqlStatementRequest(
                databaseId,
                [new SqlStatement("CREATE TABLE entries (id INTEGER PRIMARY KEY, body TEXT);", [], NoNamedParams)],
                SqlAccess.Write,
                ConsistencyMode.Strong,
                CausalBookmark.Empty),
            cancellationToken));

        var singleRowId = 0L;
        var singleStopwatch = Stopwatch.StartNew();
        await ctx.MeasureAsync(
            "cluster",
            "raft_single_node_commit",
            warmup,
            iterations,
            async _ =>
            {
                var id = ++singleRowId;
                var written = RequireWrite(await singleSession.ExecuteWriteAsync(
                    new SqlStatementRequest(
                        databaseId,
                        [new SqlStatement("INSERT INTO entries (id, body) VALUES (?, ?);", [SqlValue.FromIntegral(id), SqlValue.FromText("single-node")], NoNamedParams)],
                        SqlAccess.Write,
                        ConsistencyMode.Fast,
                        CausalBookmark.Empty),
                    cancellationToken));
                RequireInRegionReplicated(await singleFinalizer.FinalizeFastAsync(databaseId, written.Commit, cancellationToken));
            },
            null,
            "single-node CommitFinalizer baseline (IsReplicated=false)",
            cancellationToken);
        singleStopwatch.Stop();

        DisposeCertificates(singleCerts);

        string[] nodeIds = ["raft-1", "raft-2", "raft-3"];
        var nodes = new RaftBenchNode[3];
        try
        {
            for (var index = 0; index < nodeIds.Length; index++)
            {
                var nodeId = nodeIds[index];
                var nodeDir = ctx.RunDir(nodeId);
                var certs = QuicMeshPki.DeriveNode(secret, nodeId, "us-east-1", TimeProvider.System);
                List<PeerNodeInfo> peers = [];
                for (var peerIndex = 0; peerIndex < nodeIds.Length; peerIndex++)
                {
                    if (peerIndex != index)
                    {
                        peers.Add(new PeerNodeInfo(nodeIds[peerIndex], "us-east-1", "127.0.0.1", 11000 + peerIndex, 0));
                    }
                }

                var options = new ClusterOptions
                {
                    NodeId = nodeId,
                    Region = "us-east-1",
                    DataDirectory = Path.Combine(nodeDir, "cluster"),
                    ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                    Peers = peers,
                    Certificates = certs,
                    RaftShardCount = 1,
                    RaftElectionTimeoutLower = TimeSpan.FromMilliseconds(150),
                    RaftElectionTimeoutUpper = TimeSpan.FromMilliseconds(300),
                    RaftCommitTimeout = TimeSpan.FromSeconds(10),
                }.Validate();

                var storage = new StorageEngineOptions(nodeDir, options.StorageOrigin, (ushort)(20 + index), (byte)(20 + index), new DeltaColumnRegistry()) { FsyncCommitLog = false };
                var engine = new SqliteStorageEngine(storage, TimeProvider.System);
                var session = RequireCreated(await engine.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));
                var databases = new ClusterDatabases((id, token) => ResolveDatabaseAsync(engine, id, rawKey, token), TimeProvider.System);
                databases.Track(databaseId);
                var locks = new StrongLockTable(TimeProvider.System);
                var topology = new ClusterTopology(options);
                var host = new QuicRpcHost(options.ToMeshOptions(), metrics, TimeProvider.System, NullLogger<QuicRpcHost>.Instance);
                var client = new QuicRpcClient(options.ToMeshOptions(), metrics, TimeProvider.System, NullLogger<QuicRpcClient>.Instance);
                var replicator = new DeltaReplicator(topology, client, databases, emptyChangefeed, locks, TimeProvider.System, NullLogger<DeltaReplicator>.Instance);
                var raft = new InRegionRaftRouter(options, topology, client, replicator, NullLoggerFactory.Instance);
                var finalizer = new CommitFinalizer(raft, replicator, databases, emptyChangefeed, locks, NullLogger<CommitFinalizer>.Instance);
                replicator.Map(host);
                raft.Map(host);
                var bound = RequireHostStarted(await host.StartAsync(cancellationToken));
                nodes[index] = new RaftBenchNode(nodeId, certs, engine, session, host, client, replicator, raft, finalizer, bound);
            }

            for (var i = 0; i < nodes.Length; i++)
            {
                for (var j = 0; j < nodes.Length; j++)
                {
                    if (i != j)
                    {
                        nodes[i].Client.UpsertPeer(new QuicPeer(nodes[j].NodeId, "us-east-1", nodes[j].BoundEndPoint));
                    }
                }
            }

            await Task.WhenAll(nodes.Select(node => node.Raft.StartAsync(cancellationToken)));

            var ddl = RequireWrite(await nodes[0].Session.ExecuteWriteAsync(
                new SqlStatementRequest(
                    databaseId,
                    [new SqlStatement("CREATE TABLE entries (id INTEGER PRIMARY KEY, body TEXT);", [], NoNamedParams)],
                    SqlAccess.Write,
                    ConsistencyMode.Strong,
                    CausalBookmark.Empty),
                cancellationToken));
            RequireInRegionReplicated(await nodes[0].Finalizer.FinalizeFastAsync(databaseId, ddl.Commit, cancellationToken));

            var replicatedRowId = 0L;
            var lastBookmark = ddl.Commit.Bookmark;
            await ctx.MeasureAsync(
                "cluster",
                "raft_single_vs_3node_commit",
                warmup,
                iterations,
                async _ =>
                {
                    var id = ++replicatedRowId;
                    var written = RequireWrite(await nodes[0].Session.ExecuteWriteAsync(
                        new SqlStatementRequest(
                            databaseId,
                            [new SqlStatement("INSERT INTO entries (id, body) VALUES (?, ?);", [SqlValue.FromIntegral(id), SqlValue.FromText("three-node-raft")], NoNamedParams)],
                            SqlAccess.Write,
                            ConsistencyMode.Fast,
                            CausalBookmark.Empty),
                        cancellationToken));
                    RequireInRegionReplicated(await nodes[0].Finalizer.FinalizeFastAsync(databaseId, written.Commit, cancellationToken));
                    lastBookmark = written.Commit.Bookmark;
                },
                null,
                string.Concat("3-node loopback InRegionRaftRouter + CommitFinalizer (single-node total ", singleStopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture), " ms)"),
                cancellationToken);

            var appliedFollowers = 0;
            for (var index = 1; index < nodes.Length; index++)
            {
                var reached = await nodes[index].Session.WaitForBookmarkAsync(lastBookmark, TimeSpan.FromSeconds(10), cancellationToken);
                if (IsBookmarkReached(reached))
                {
                    var verified = RequireRead(await nodes[index].Session.ExecuteReadAsync(
                        new SqlStatementRequest(
                            databaseId,
                            [new SqlStatement("SELECT COUNT(*) FROM entries;", [], NoNamedParams)],
                            SqlAccess.Read,
                            ConsistencyMode.Fast,
                            lastBookmark),
                        cancellationToken));
                    if (verified.ResultSets[0].Rows[0][0].TryGetIntegral(out var followerCount) && followerCount == replicatedRowId)
                    {
                        appliedFollowers++;
                    }
                }
            }

            if (appliedFollowers < 1)
            {
                throw new InvalidOperationException("No Raft follower applied the replicated commits through the quorum state machine.");
            }
        }
        finally
        {
            foreach (var node in nodes)
            {
                if (node is not null)
                {
                    await node.Raft.DisposeAsync();
                }
            }

            foreach (var node in nodes)
            {
                if (node is not null)
                {
                    await node.Replicator.DisposeAsync();
                }
            }

            foreach (var node in nodes)
            {
                if (node is not null)
                {
                    await node.Client.DisposeAsync();
                }
            }

            foreach (var node in nodes)
            {
                if (node is not null)
                {
                    await node.Host.DisposeAsync();
                }
            }

            foreach (var node in nodes)
            {
                if (node is not null)
                {
                    await node.Session.DisposeAsync();
                    await node.Engine.DisposeAsync();
                    DisposeCertificates(node.Certificates);
                }
            }
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

    private static void RequireGranted(LockAttempt attempt, string stage)
    {
        var error = attempt switch
        {
            LockGranted => (string?)null,
            LockDenied denied => string.Concat("lock denied at ", stage, " by ", denied.Winner.TxId.ToString()),
            LockWoundRequired wound => string.Concat("unexpected wound at ", stage, " count=", wound.Victims.Count.ToString(CultureInfo.InvariantCulture)),
        };
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    private static IReadOnlyList<StrongLockHolder> RequireWound(LockAttempt attempt)
    {
        var (victims, error) = attempt switch
        {
            LockWoundRequired wound => (wound.Victims, (string?)null),
            LockGranted => ((IReadOnlyList<StrongLockHolder>?)null, "expected wound conflict but lock was granted"),
            LockDenied => ((IReadOnlyList<StrongLockHolder>?)null, "expected wound conflict but lock was denied"),
        };
        if (error is not null || victims is not { } found)
        {
            throw new InvalidOperationException(error);
        }

        return found;
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

    private static BlobFrameMessage RequireCallReply(QuicCallOutcome<BlobFrameMessage> outcome)
    {
        var (reply, error) = outcome switch
        {
            BlobFrameMessage message => (message, (string?)null),
            QuicUnavailable unavailable => ((BlobFrameMessage?)null, unavailable.Message),
            QuicTimeout timeout => ((BlobFrameMessage?)null, timeout.Message),
            QuicRemoteError remote => ((BlobFrameMessage?)null, remote.Message),
        };
        if (error is not null || reply is not { } received)
        {
            throw new InvalidOperationException(error);
        }

        return received;
    }

    private static void RequirePushed(QuicPushOutcome outcome)
    {
        var error = outcome switch
        {
            QuicSent => (string?)null,
            QuicUnavailable unavailable => unavailable.Message,
            QuicTimeout timeout => timeout.Message,
        };
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
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

    private static bool IsBookmarkReached(WaitForBookmarkOutcome outcome) => outcome switch
    {
        BookmarkReached => true,
        StorageTimeout => false,
    };

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

    private static void RequireInRegionReplicated(InRegionReplicationOutcome outcome)
    {
        var error = outcome switch
        {
            InRegionReplicated => (string?)null,
            ClusterUnavailable unavailable => unavailable.Message,
            ClusterTimeout timeout => timeout.Message,
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

    private sealed record RaftBenchNode(
        string NodeId,
        QuicMeshCertificates Certificates,
        SqliteStorageEngine Engine,
        ISqlDatabaseSession Session,
        QuicRpcHost Host,
        QuicRpcClient Client,
        DeltaReplicator Replicator,
        InRegionRaftRouter Raft,
        CommitFinalizer Finalizer,
        IPEndPoint BoundEndPoint);

    private sealed record BlobFrameMessage(int Sequence, ReadOnlyMemory<byte> Payload) : IBinaryMessage<BlobFrameMessage>
    {
        public void WriteTo(IBufferWriter<byte> writer)
        {
            writer.WriteInt32(Sequence);
            writer.WriteBytes(Payload.Span);
        }

        public static BlobFrameMessage ReadFrom(ref QuicWireReader reader)
        {
            var sequence = reader.ReadInt32();
            return new BlobFrameMessage(sequence, reader.ReadBytes());
        }
    }

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
