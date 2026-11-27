using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Continuity;
using TerraBase.Storage;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.Storage;

namespace TerraBase.Benchmarks;

public static class ContinuityBenchmarks
{
    private static readonly IReadOnlyDictionary<string, SqlValue> NoNamedParams = FrozenDictionary<string, SqlValue>.Empty;

    public static async Task RunAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ctx.ShouldRun("continuity"))
        {
            return;
        }

        if (ctx.ShouldRunBenchmark("continuity", "archived_segment_validate_replay"))
        {
            await RunArchivedSegmentValidateReplayAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark("continuity", "session_archiver_seal_and_upload"))
        {
            await RunSessionArchiverSealAndUploadAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark("continuity", "changefeed_fanout_4_subscribers"))
        {
            await RunChangefeedFanoutFourSubscribersAsync(ctx, cancellationToken);
        }
    }

    private static async Task RunArchivedSegmentValidateReplayAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int commitCount = 500;
        var databaseId = Ulid.NewUlid();
        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

        var sourceOptions = new StorageEngineOptions(ctx.RunDir("archived-segment-source"), "us-east-1", 1, 1, new DeltaColumnRegistry())
        {
            SegmentMaxBytes = 32 * 1024 * 1024,
            FsyncCommitLog = false,
        };
        var targetOptions = new StorageEngineOptions(ctx.RunDir("archived-segment-target"), "sa-east-1", 2, 2, new DeltaColumnRegistry())
        {
            SegmentMaxBytes = 32 * 1024 * 1024,
            FsyncCommitLog = false,
        };

        await using var sourceEngine = new SqliteStorageEngine(sourceOptions, TimeProvider.System);
        await using var targetEngine = new SqliteStorageEngine(targetOptions, TimeProvider.System);
        await using var sourceSession = RequireCreated(await sourceEngine.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));
        await using var targetSession = RequireCreated(await targetEngine.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));

        var ddl = RequireWrite(await sourceSession.ExecuteWriteAsync(
            new SqlStatementRequest(
                databaseId,
                [new SqlStatement("CREATE TABLE records (id INTEGER PRIMARY KEY, payload TEXT);", [], NoNamedParams)],
                SqlAccess.Write,
                ConsistencyMode.Strong,
                CausalBookmark.Empty),
            cancellationToken));
        if (ddl.Commit.Sequence != 1)
        {
            throw new InvalidOperationException("Initial DDL commit sequence was not 1.");
        }

        var rowPayload = new string('a', 256);
        for (var sequence = 2; sequence <= commitCount; sequence++)
        {
            var write = RequireWrite(await sourceSession.ExecuteWriteAsync(
                new SqlStatementRequest(
                    databaseId,
                    [new SqlStatement("INSERT INTO records (id, payload) VALUES (?, ?);", [SqlValue.FromIntegral(sequence), SqlValue.FromText(rowPayload)], NoNamedParams)],
                    SqlAccess.Write,
                    ConsistencyMode.Fast,
                    CausalBookmark.Empty),
                cancellationToken));
            if (write.Commit.Sequence != sequence)
            {
                throw new InvalidOperationException("Unexpected commit sequence while generating 500-commit segment.");
            }
        }

        SqliteStorageEngine.SealCommittedLogs(sourceSession);
        var sealedSegments = RequireListedSegments(await sourceSession.ListSealedSegmentsAsync("us-east-1", 0, cancellationToken));
        var segment = sealedSegments.FirstOrDefault(static item => item.FirstSequence == 1 && item.LastSequence == commitCount)
            ?? throw new InvalidOperationException("Expected a single sealed segment covering sequences 1..500.");

        byte[] segmentBytes;
        await using (var opened = RequireOpenedSegment(await sourceSession.OpenSegmentAsync(segment, cancellationToken)))
        {
            using var copy = new MemoryStream((int)segment.SizeBytes);
            await opened.CopyToAsync(copy, cancellationToken);
            segmentBytes = copy.ToArray();
        }

        if (segmentBytes.Length != segment.SizeBytes)
        {
            throw new InvalidOperationException("Sealed segment byte length did not match segment metadata.");
        }

        await ctx.MeasureAsync(
            "continuity",
            "archived_segment_validate_replay",
            5,
            30,
            async _ =>
            {
                using var stream = new MemoryStream(segmentBytes, writable: false);
                var expectedSequence = 1L;
                var replayedCount = 0;
                var outcome = await targetSession.ReadArchivedSegmentAsync(
                    segment,
                    stream,
                    (commits, _) =>
                    {
                        foreach (var commit in commits)
                        {
                            if (commit.Sequence != expectedSequence
                                || !string.Equals(commit.OriginRegion, "us-east-1", StringComparison.Ordinal)
                                || commit.DeltaPayload.IsEmpty)
                            {
                                return ValueTask.FromResult(false);
                            }

                            expectedSequence++;
                            replayedCount++;
                        }

                        return ValueTask.FromResult(true);
                    },
                    cancellationToken);
                RequireArchivedSegmentRead(outcome);
                if (replayedCount != commitCount || expectedSequence != commitCount + 1)
                {
                    throw new InvalidOperationException("Archived segment replay did not yield all 500 contiguous commits.");
                }
            },
            () => segmentBytes.Length,
            "500-commit sealed segment CRC32C + AES-GCM validate & replay",
            cancellationToken);
    }

    private static async Task RunSessionArchiverSealAndUploadAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int commitsPerPass = 16;
        var databaseId = Ulid.NewUlid();
        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

        var storageDir = ctx.RunDir("session-archiver");
        var storageOptions = new StorageEngineOptions(storageDir, "us-east-1", 3, 3, new DeltaColumnRegistry())
        {
            FsyncCommitLog = false,
        };

        await using var endpoint = LoopbackArchiveEndpoint.Start("bench-backups");
        await using var engine = new SqliteStorageEngine(storageOptions, TimeProvider.System);
        await using var session = RequireCreated(await engine.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));

        var catalog = new BenchmarkContinuityCatalog(databaseId, rawKey);
        var continuityOptions = new ContinuityOptions
        {
            Archive = new S3ArchiveSettings(endpoint.BaseUri, "us-east-1", "bench-backups", "bench-access", "bench-secret"),
        }.Validate();

        using var meterFactory = new BenchMeterFactory();
        var metrics = new ContinuityMetrics(meterFactory, continuityOptions);
        using var store = new S3ArchiveStore(continuityOptions, NullLogger<S3ArchiveStore>.Instance);
        var manifests = new ArchiveManifests(store, metrics, TimeProvider.System, NullLogger<ArchiveManifests>.Instance);
        var sessions = new ContinuitySessions(engine, catalog, TimeProvider.System, NullLogger<ContinuitySessions>.Instance);
        var restorer = new DatabaseRestorer(sessions, storageOptions, manifests, TimeProvider.System, NullLogger<DatabaseRestorer>.Instance);
        var continuity = new BackupContinuityService(sessions, manifests, restorer, continuityOptions, metrics, TimeProvider.System);

        var ddl = RequireWrite(await session.ExecuteWriteAsync(
            new SqlStatementRequest(
                databaseId,
                [new SqlStatement("CREATE TABLE events (id INTEGER PRIMARY KEY, body TEXT);", [], NoNamedParams)],
                SqlAccess.Write,
                ConsistencyMode.Strong,
                CausalBookmark.Empty),
            cancellationToken));
        var sequence = ddl.Commit.Sequence;

        var initialStatus = RequireBackupStatus(await continuity.ArchiveNowAsync(databaseId, cancellationToken));
        if (initialStatus.PendingSegments != 0 || initialStatus.ArchivedThrough.Get("us-east-1") < sequence)
        {
            throw new InvalidOperationException("Initial baseline archive pass did not archive database.");
        }

        var rowBody = new string('b', 256);
        var lastUploadedDelta = 0L;
        await ctx.MeasureAsync(
            "continuity",
            "session_archiver_seal_and_upload",
            3,
            25,
            async _ =>
            {
                for (var step = 0; step < commitsPerPass; step++)
                {
                    var nextId = sequence + 1;
                    var written = RequireWrite(await session.ExecuteWriteAsync(
                        new SqlStatementRequest(
                            databaseId,
                            [new SqlStatement("INSERT INTO events (id, body) VALUES (?, ?);", [SqlValue.FromIntegral(nextId), SqlValue.FromText(rowBody)], NoNamedParams)],
                            SqlAccess.Write,
                            ConsistencyMode.Fast,
                            CausalBookmark.Empty),
                        cancellationToken));
                    sequence = written.Commit.Sequence;
                }

                var bytesBefore = endpoint.TotalPutBytes;
                var putsBefore = endpoint.PutCount;
                var status = RequireBackupStatus(await continuity.ArchiveAsync(databaseId, forceSnapshot: false, cancellationToken));
                lastUploadedDelta = endpoint.TotalPutBytes - bytesBefore;

                if (status.PendingSegments != 0
                    || status.ArchivedThrough.Get("us-east-1") != sequence
                    || endpoint.PutCount < putsBefore + 2)
                {
                    throw new InvalidOperationException("SessionArchiver seal-and-upload pass did not archive the new segment and manifest.");
                }
            },
            () => lastUploadedDelta,
            "16 commits -> SealCommittedLogs + S3 segment PUT + CAS manifest PUT",
            cancellationToken);
    }

    private static async Task RunChangefeedFanoutFourSubscribersAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        const int subscriberCount = 4;
        const int payloadBytes = 512;
        var databaseId = Ulid.NewUlid();
        var rawKey = RandomNumberGenerator.GetBytes(DatabaseOpenRequest.RawKeyLength);

        var storageOptions = new StorageEngineOptions(ctx.RunDir("changefeed-fanout"), "us-east-1", 4, 4, new DeltaColumnRegistry())
        {
            FsyncCommitLog = false,
        };
        await using var engine = new SqliteStorageEngine(storageOptions, TimeProvider.System);
        await using var session = RequireCreated(await engine.CreateAsync(new DatabaseCreateRequest(databaseId, rawKey, false), cancellationToken));

        var ddl = RequireWrite(await session.ExecuteWriteAsync(
            new SqlStatementRequest(
                databaseId,
                [new SqlStatement("CREATE TABLE events (id INTEGER PRIMARY KEY, data TEXT);", [], NoNamedParams)],
                SqlAccess.Write,
                ConsistencyMode.Strong,
                CausalBookmark.Empty),
            cancellationToken));
        var startBookmark = ddl.Commit.Bookmark;
        var nextSequence = ddl.Commit.Sequence;

        var catalog = new BenchmarkContinuityCatalog(databaseId, rawKey);
        var continuityOptions = new ContinuityOptions { ChangefeedSubscriberCapacity = 1024 }.Validate();
        using var meterFactory = new BenchMeterFactory();
        var metrics = new ContinuityMetrics(meterFactory, continuityOptions);
        var sessions = new ContinuitySessions(engine, catalog, TimeProvider.System, NullLogger<ContinuitySessions>.Instance);
        var hub = new ChangefeedHub(sessions, continuityOptions, metrics, NullLogger<ChangefeedHub>.Instance);

        using var subscriberCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = 0;
        var currentGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribers = new Task[subscriberCount];
        var deliveredPerSubscriber = new long[subscriberCount];

        var enumerators = new IAsyncEnumerator<ChangefeedEntry>[subscriberCount];
        var firstMoves = new Task<bool>[subscriberCount];
        for (var index = 0; index < subscriberCount; index++)
        {
            enumerators[index] = hub.SubscribeAsync(new ChangefeedSubscription(databaseId, startBookmark), subscriberCts.Token)
                .GetAsyncEnumerator(subscriberCts.Token);
            firstMoves[index] = enumerators[index].MoveNextAsync().AsTask();
        }

        for (var index = 0; index < subscriberCount; index++)
        {
            var slot = index;
            var enumerator = enumerators[index];
            var firstMove = firstMoves[index];
            var expectedSequence = nextSequence + 1;
            subscribers[index] = Task.Run(async () =>
            {
                await using (enumerator)
                {
                    var hasMore = await firstMove;
                    while (hasMore)
                    {
                        var entry = enumerator.Current;
                        if (entry.DatabaseId != databaseId
                            || entry.Sequence != expectedSequence
                            || entry.DeltaPayload.Length != payloadBytes)
                        {
                            throw new InvalidOperationException("Changefeed subscriber received mismatched or out-of-order entry.");
                        }

                        expectedSequence++;
                        deliveredPerSubscriber[slot]++;
                        if (Interlocked.Decrement(ref remaining) == 0)
                        {
                            _ = Volatile.Read(ref currentGate).TrySetResult();
                        }

                        hasMore = await enumerator.MoveNextAsync();
                    }
                }
            }, CancellationToken.None);
        }

        var payload = new byte[payloadBytes];
        RandomNumberGenerator.Fill(payload);

        await PublishAndAwaitFanoutAsync();

        await ctx.MeasureAsync(
            "continuity",
            "changefeed_fanout_4_subscribers",
            25,
            250,
            async _ => await PublishAndAwaitFanoutAsync(),
            () => payloadBytes * subscriberCount,
            "ChangefeedHub PublishAsync -> 4 concurrent SubscribeAsync consumers",
            cancellationToken);

        await subscriberCts.CancelAsync();
        await Task.WhenAll(subscribers);

        var expectedTotal = nextSequence - ddl.Commit.Sequence;
        for (var index = 0; index < subscriberCount; index++)
        {
            if (deliveredPerSubscriber[index] != expectedTotal)
            {
                throw new InvalidOperationException("Changefeed subscriber missed published entries.");
            }
        }

        async ValueTask PublishAndAwaitFanoutAsync()
        {
            var seq = ++nextSequence;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref currentGate, gate);
            Volatile.Write(ref remaining, subscriberCount);
            var entry = new ChangefeedEntry(
                databaseId,
                Ulid.NewUlid(),
                "us-east-1",
                seq,
                1,
                ConsistencyMode.Fast,
                payload,
                TimeProvider.System.GetUtcNow());
            await hub.PublishAsync(entry, cancellationToken);
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
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

    private static IReadOnlyList<CommitLogSegment> RequireListedSegments(ListSegmentsOutcome outcome)
    {
        var (segments, error) = outcome switch
        {
            IReadOnlyList<CommitLogSegment> listed => (listed, (string?)null),
            StorageBusy busy => ((IReadOnlyList<CommitLogSegment>?)null, busy.Message),
        };
        if (error is not null || segments is not { } ok)
        {
            throw new InvalidOperationException(error);
        }

        return ok;
    }

    private static Stream RequireOpenedSegment(OpenSegmentOutcome outcome)
    {
        var (stream, error) = outcome switch
        {
            Stream opened => (opened, (string?)null),
            SegmentNotFound missing => ((Stream?)null, missing.Message),
            StorageBusy busy => ((Stream?)null, busy.Message),
        };
        if (error is not null || stream is not { } ok)
        {
            throw new InvalidOperationException(error);
        }

        return ok;
    }

    private static void RequireArchivedSegmentRead(ReadArchivedSegmentOutcome outcome)
    {
        var error = outcome switch
        {
            ArchivedSegmentRead => (string?)null,
            ArchivedSegmentReplayStopped => "archived segment replay stopped early",
            InvalidSegment invalid => invalid.Message,
            StorageCorrupted corrupted => corrupted.Message,
        };
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    private static BackupStatus RequireBackupStatus(BackupStatusOutcome outcome)
    {
        var (status, error) = outcome switch
        {
            BackupStatus ok => (ok, (string?)null),
            ContinuityNotFound missing => ((BackupStatus?)null, missing.Message),
            ContinuityUnavailable unavailable => ((BackupStatus?)null, unavailable.Message),
        };
        if (error is not null || status is not { } ready)
        {
            throw new InvalidOperationException(error);
        }

        return ready;
    }

    private sealed class BenchmarkContinuityCatalog(Ulid databaseId, ReadOnlyMemory<byte> rawKey) : IContinuityDatabaseCatalog
    {
        public ValueTask<IReadOnlyList<Ulid>> ListActiveDatabasesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<Ulid>>([databaseId]);

        public ValueTask<ContinuityRawKeyOutcome> ResolveRawKeyAsync(Ulid id, CancellationToken cancellationToken)
        {
            ContinuityRawKeyOutcome outcome = id == databaseId
                ? new ContinuityRawKey(rawKey)
                : new ContinuityDatabaseKeyMissing(id, "database key is not registered", "not_found");
            return ValueTask.FromResult(outcome);
        }

        public ValueTask<CausalBookmark> ConfirmReplicaWatermarksAsync(Ulid id, CausalBookmark local, CancellationToken cancellationToken) =>
            ValueTask.FromResult(local);

        public ValueTask DatabaseReplacedAsync(Ulid id, CausalBookmark? rewoundTo, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<DiscardCopyTargetOutcome> DiscardCopyTargetAsync(Ulid id, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DiscardCopyTargetOutcome>(new CopyTargetDiscarded());
    }

    private sealed class LoopbackArchiveEndpoint : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _bucket;
        private readonly ConcurrentDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _stopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;
        private long _etagVersion;
        private long _putCount;
        private long _totalPutBytes;

        private LoopbackArchiveEndpoint(HttpListener listener, Uri baseUri, string bucket)
        {
            _listener = listener;
            BaseUri = baseUri;
            _bucket = bucket;
            _loop = ServeAsync();
        }

        public Uri BaseUri { get; }

        public long PutCount => Interlocked.Read(ref _putCount);

        public long TotalPutBytes => Interlocked.Read(ref _totalPutBytes);

        public static LoopbackArchiveEndpoint Start(string bucket)
        {
            var port = ReserveLoopbackPort();
            var prefix = string.Concat("http://127.0.0.1:", port.ToString(CultureInfo.InvariantCulture), "/");
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            return new LoopbackArchiveEndpoint(listener, new Uri(prefix), bucket);
        }

        public async ValueTask DisposeAsync()
        {
            _ = _stopSignal.TrySetResult();
            _listener.Stop();
            _listener.Close();
            await _loop;
        }

        private static int ReserveLoopbackPort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (!_stopSignal.Task.IsCompleted)
            {
                var next = _listener.GetContextAsync();
                var winner = await Task.WhenAny(next, _stopSignal.Task);
                if (winner != next)
                {
                    _ = next.ContinueWith(
                        static task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                    return;
                }

                if (next.IsFaulted || next.IsCanceled)
                {
                    _ = next.Exception;
                    return;
                }

                await HandleAsync(await next);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            if (request.Url is not { AbsolutePath: var rawPath })
            {
                await WriteXmlAsync(response, 400, "<Error><Code>InvalidRequest</Code></Error>");
                return;
            }

            var bucketPrefix = string.Concat("/", _bucket);
            if (!rawPath.StartsWith(bucketPrefix, StringComparison.Ordinal))
            {
                await WriteXmlAsync(response, 404, "<Error><Code>NoSuchBucket</Code><Message>bucket not found</Message></Error>");
                return;
            }

            var remainder = rawPath[bucketPrefix.Length..].TrimStart('/');
            if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal)
                && remainder.Length == 0
                && string.Equals(request.QueryString["list-type"], "2", StringComparison.Ordinal))
            {
                await HandleListAsync(request, response);
                return;
            }

            var key = Uri.UnescapeDataString(remainder);
            if (string.Equals(request.HttpMethod, "PUT", StringComparison.Ordinal))
            {
                await HandlePutAsync(request, response, key);
                return;
            }

            if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
            {
                await HandleGetAsync(response, key);
                return;
            }

            if (string.Equals(request.HttpMethod, "DELETE", StringComparison.Ordinal))
            {
                _ = _objects.TryRemove(key, out _);
                response.StatusCode = 204;
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            await WriteXmlAsync(response, 405, "<Error><Code>MethodNotAllowed</Code></Error>");
        }

        private async Task HandleListAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            var prefix = request.QueryString["prefix"] is { } requestedPrefix ? requestedPrefix : string.Empty;

            var matching = _objects
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .ToArray();
            var builder = new StringBuilder();
            _ = builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
                .Append("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">")
                .Append("<Name>").Append(SecurityElement.Escape(_bucket)).Append("</Name>")
                .Append("<Prefix>").Append(SecurityElement.Escape(prefix)).Append("</Prefix>")
                .Append("<KeyCount>").Append(matching.Length.ToString(CultureInfo.InvariantCulture)).Append("</KeyCount>")
                .Append("<MaxKeys>1000</MaxKeys>")
                .Append("<IsTruncated>false</IsTruncated>");
            foreach (var (key, item) in matching)
            {
                _ = builder.Append("<Contents><Key>")
                    .Append(SecurityElement.Escape(key))
                    .Append("</Key><Size>")
                    .Append(item.Content.Length.ToString(CultureInfo.InvariantCulture))
                    .Append("</Size><ETag>")
                    .Append(SecurityElement.Escape(item.ETag))
                    .Append("</ETag></Contents>");
            }

            _ = builder.Append("</ListBucketResult>");
            await WriteXmlAsync(response, 200, builder.ToString());
        }

        private async Task HandlePutAsync(HttpListenerRequest request, HttpListenerResponse response, string key)
        {
            using var buffer = new MemoryStream();
            await request.InputStream.CopyToAsync(buffer);
            var payload = buffer.ToArray();

            var ifNoneMatch = request.Headers["If-None-Match"];
            var ifMatch = request.Headers["If-Match"];
            var existingFound = _objects.TryGetValue(key, out var existing);
            if (string.Equals(ifNoneMatch, "*", StringComparison.Ordinal) && existingFound)
            {
                await WriteXmlAsync(response, 412, "<Error><Code>PreconditionFailed</Code><Message>object already exists</Message></Error>");
                return;
            }

            if (ifMatch is not null && (!existingFound || existing is null || !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal)))
            {
                await WriteXmlAsync(response, 412, "<Error><Code>PreconditionFailed</Code><Message>etag mismatch</Message></Error>");
                return;
            }

            var version = Interlocked.Increment(ref _etagVersion);
            var etag = string.Concat("\"e", version.ToString(CultureInfo.InvariantCulture), "\"");
            var contentType = request.ContentType is { Length: > 0 } requestedType ? requestedType : "application/octet-stream";
            _objects[key] = new StoredObject(payload, etag, contentType);
            Interlocked.Increment(ref _putCount);
            Interlocked.Add(ref _totalPutBytes, payload.Length);

            response.StatusCode = 200;
            response.Headers["ETag"] = etag;
            if (request.Headers["x-amz-checksum-crc32"] is { Length: > 0 } crc32)
            {
                response.Headers["x-amz-checksum-crc32"] = crc32;
            }

            if (request.Headers["x-amz-checksum-sha256"] is { Length: > 0 } sha256)
            {
                response.Headers["x-amz-checksum-sha256"] = sha256;
            }

            response.ContentLength64 = 0;
            response.Close();
        }

        private async Task HandleGetAsync(HttpListenerResponse response, string key)
        {
            if (!_objects.TryGetValue(key, out var stored))
            {
                await WriteXmlAsync(response, 404, "<Error><Code>NoSuchKey</Code><Message>object not found</Message></Error>");
                return;
            }

            response.StatusCode = 200;
            response.ContentType = stored.ContentType;
            response.Headers["ETag"] = stored.ETag;
            response.ContentLength64 = stored.Content.Length;
            await response.OutputStream.WriteAsync(stored.Content);
            response.Close();
        }

        private static async Task WriteXmlAsync(HttpListenerResponse response, int statusCode, string xml)
        {
            var bytes = Encoding.UTF8.GetBytes(xml);
            response.StatusCode = statusCode;
            response.ContentType = "application/xml";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private sealed record StoredObject(byte[] Content, string ETag, string ContentType);
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
