using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TerraBase.Backup;
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

            Directory.CreateDirectory(options.Output);
            var envelopePath = Path.Combine(options.Output, "resource-envelope.json");
            receiptPath = Path.Combine(options.Output, "tiered-rustfs-receipt.json");
            receipt = new QualificationReceiptState
            {
                Status = "running",
                Phase = "initializing",
                StartedAtUtc = DateTimeOffset.UtcNow,
                TargetRecords = options.Records,
                SubjectProject = BenchmarkAssemblyMetadata.Value("TerraBaseSubjectProject"),
                ResourceEnvelopePath = Path.GetFullPath(envelopePath),
                ProgressPath = Path.GetFullPath(Path.Combine(options.Output, "progress.jsonl")),
            };
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            var initialEnvelope = BenchmarkCgroupSnapshot.Capture();
            await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(initialEnvelope)).ConfigureAwait(false);
            receipt.LatestResources = ResourceSample.Capture("initializing", 0, initialEnvelope);
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            await using var progress = new ProgressLog(receipt.ProgressPath);
            await progress.WriteAsync("initializing", 0, options.Records, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp())).ConfigureAwait(false);
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

            var objectStore = new S3NativeBackupObjectStore(client, options.Bucket, maxAttempts: 3,
                checkpointStore: new FileBackupUploadCheckpointStore(Path.Combine(options.Output, "uploads")));
            var root = Path.Combine(options.Output, "engine");
            if (Directory.Exists(root)) throw new IOException("Use a fresh output directory for each qualification run.");
            var cache = Path.Combine(root, "ssd-cache");
            var diskUsage = new DiskUsageTracker(root, cache);
            var tiered = new TieredStorageSegmentStore(new TieredStorageSegmentStoreOptions(
                objectStore,
                cache,
                options.Prefix,
                options.CacheBytes));
            var tenant = new TenantId("tiered-qualification");
            var collection = new CollectionId("documents");
            var payload = new string('x', options.PayloadBytes);
            var writeTimer = Stopwatch.StartNew();
            var compactionTimer = new Stopwatch();
            StorageCompactionResult initialCompaction;
            var nextCheckpoint = options.CheckpointRecords;
            Console.WriteLine($"phase: seed {options.Records:N0} records");
            receipt.Phase = "seed";
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            await using (var engine = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered))
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

                    var result = await engine.ApplyBatchAsync(mutations).ConfigureAwait(false);
                    if (result.Outcome != StorageBatchOutcome.Committed)
                    {
                        throw new InvalidDataException($"Batch at {offset} did not commit: {result.Outcome}.");
                    }

                    var completed = offset + count;
                    if (completed >= nextCheckpoint || completed == options.Records)
                    {
                        diskUsage.Sample("seed", completed);
                        await WriteCheckpointAsync(diskUsage, progress, receiptPath, receipt, "seed", completed, options.Records, started).ConfigureAwait(false);
                        nextCheckpoint = completed > long.MaxValue - options.CheckpointRecords
                            ? long.MaxValue
                            : completed + options.CheckpointRecords;
                    }
                }

                writeTimer.Stop();
                Console.WriteLine("phase: compact and publish to S3");
                receipt.Phase = "initial-compaction";
                await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
                compactionTimer.Start();
                initialCompaction = await MeasureOperationAsync(
                    () => engine.CompactAsync().AsTask(), diskUsage, "initial-compaction").ConfigureAwait(false);
                compactionTimer.Stop();
            }

            var initialRemote = await ListRemoteObjectsAsync(client, options).ConfigureAwait(false);
            var initialCache = DeleteCacheFiles(cache);
            receipt.Phase = "initial-cache-evicted";
            receipt.RemoteObjects = initialRemote;
            receipt.InitialCacheEviction = initialCache;
            receipt.CacheEviction = initialCache;
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);

            var reopenTimer = Stopwatch.StartNew();
            Console.WriteLine("phase: remote reopen and exact scan");
            var first = new DocumentKey(tenant, collection, "doc-0000000000");
            var last = new DocumentKey(tenant, collection, $"doc-{options.Records - 1:D10}");
            await using (var reopened = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered))
            {
            var firstDocument = await reopened.ReadAsync(first).ConfigureAwait(false)
                ?? throw new InvalidDataException("The first document was not recovered from RustFS.");
            var lastDocument = await reopened.ReadAsync(last).ConfigureAwait(false)
                ?? throw new InvalidDataException("The last document was not recovered from RustFS.");
                VerifyBody(firstDocument, 0, payload, updated: false);
                VerifyBody(lastDocument, options.Records - 1, payload, updated: false);
                var scanCount = await VerifyScanAsync(reopened, tenant, collection, options.Records, payload, 0, 0, options.CheckpointRecords, diskUsage, progress, receiptPath, receipt, "initial-scan", started).ConfigureAwait(false);
                await VerifyPointReadsAsync(reopened, tenant, collection, options, payload, 0, 0, diskUsage, progress, receiptPath, receipt, "initial-point-reads", started).ConfigureAwait(false);
                if (scanCount != options.Records)
                    throw new InvalidDataException("Remote reopen did not recover the complete corpus.");
            }

            reopenTimer.Stop();
            _ = DirectoryBytes(cache);
            var initialEnvelopeAfterRead = BenchmarkCgroupSnapshot.Capture();
            await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(initialEnvelopeAfterRead)).ConfigureAwait(false);
            receipt.Phase = "initial-verification-complete";
            receipt.LatestResources = ResourceSample.Capture(receipt.Phase, options.Records, initialEnvelopeAfterRead);
            await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);

            var updateTimer = new Stopwatch();
            var deleteTimer = new Stopwatch();
            var finalCompactionTimer = new Stopwatch();
            StorageCompactionResult? finalCompaction = null;
            if (options.Updates > 0 || options.Deletes > 0)
            {
                Console.WriteLine($"phase: mutate {options.Updates:N0} updates and {options.Deletes:N0} deletes");
                receipt.Phase = "mutations";
                await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
                await using (var engine = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered))
                {
                    nextCheckpoint = options.CheckpointRecords;
                    updateTimer.Start();
                    await ApplyRangeAsync(engine, tenant, collection, options.Updates, options.Batch, payload, true, options.CheckpointRecords, diskUsage, progress, receiptPath, receipt, "updates", started).ConfigureAwait(false);
                    updateTimer.Stop();
                    deleteTimer.Start();
                    nextCheckpoint = options.CheckpointRecords;
                    await ApplyDeletesAsync(engine, tenant, collection, options, options.Deletes, options.CheckpointRecords, diskUsage, progress, receiptPath, receipt, started).ConfigureAwait(false);
                    deleteTimer.Stop();
                    receipt.Phase = "final-compaction";
                    await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
                    finalCompactionTimer.Start();
                    finalCompaction = await MeasureOperationAsync(
                        () => engine.CompactAsync().AsTask(), diskUsage, "final-compaction").ConfigureAwait(false);
                    finalCompactionTimer.Stop();
                }

                var finalRemote = await ListRemoteObjectsAsync(client, options).ConfigureAwait(false);
                var finalCacheEviction = DeleteCacheFiles(cache);
                receipt.Phase = "final-cache-evicted";
                receipt.RemoteObjects = finalRemote;
                receipt.FinalCacheEviction = finalCacheEviction;
                receipt.CacheEviction = finalCacheEviction;
                await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
            }

            var finalReopenTimer = Stopwatch.StartNew();
            Console.WriteLine("phase: final remote reopen and exact scan");
            await using (var reopened = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered))
            {
                var finalScanCount = await VerifyScanAsync(reopened, tenant, collection, options.Records - options.Deletes, payload, options.Updates, options.Deletes, options.CheckpointRecords, diskUsage, progress, receiptPath, receipt, "final-scan", started).ConfigureAwait(false);
                await VerifyPointReadsAsync(reopened, tenant, collection, options, payload, options.Updates, options.Deletes, diskUsage, progress, receiptPath, receipt, "final-point-reads", started).ConfigureAwait(false);
                if (finalScanCount != options.Records - options.Deletes)
                    throw new InvalidDataException($"Final remote reopen returned {finalScanCount:N0} live records, expected {options.Records - options.Deletes:N0}.");
            }
            finalReopenTimer.Stop();
            var envelope = BenchmarkCgroupSnapshot.Capture();
            await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(envelope)).ConfigureAwait(false);
            var elapsedWrite = Stopwatch.GetElapsedTime(started);
            var remote = await ListRemoteObjectsAsync(client, options).ConfigureAwait(false);
            var cacheBytes = DirectoryBytes(cache);
            var finalScanCountForSummary = options.Records - options.Deletes;
            var summary = Path.Combine(options.Output, "tiered-rustfs-summary.md");
            await File.WriteAllTextAsync(summary, $"""
# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `{endpoint}` |
| Bucket | `{options.Bucket}` |
| Source provenance | `run-receipt.json` (emitted by the benchmark runner) |
| Durability | Synced WAL, one device barrier per batch of {options.Batch} |
| Seed records (exact bodies and versions verified) | {options.Records:N0} |
| Final live records (exact bodies and versions verified) | {finalScanCountForSummary:N0} |
| Repeated-character payload bytes per record | {options.PayloadBytes:N0} |
| Minimum logical seed payload bytes | {options.Records * (long)options.PayloadBytes:N0} |
| Observed cgroup memory.max | {envelope.MemoryMax ?? "unavailable"} |
| Observed cgroup memory.peak | {envelope.MemoryPeak ?? "unavailable"} |
| Observed cgroup memory.swap.max | {envelope.SwapMax ?? "unavailable"} |
| Initial published segments | {initialCompaction.PublishedSegments.Count} |
| Initial compaction records | {initialCompaction.RecordsWritten:N0} |
| Updates applied | {options.Updates:N0} |
| Deletes applied and verified absent | {options.Deletes:N0} |
| Final published segments | {finalCompaction?.PublishedSegments.Count ?? initialCompaction.PublishedSegments.Count} |
| Final compaction records | {finalCompaction?.RecordsWritten ?? 0:N0} |
| Synced seed time | {writeTimer.Elapsed.TotalSeconds:F3} s |
| Synced writes/sec | {options.Records / writeTimer.Elapsed.TotalSeconds:F0} |
| Initial compaction and object publication | {compactionTimer.Elapsed.TotalSeconds:F3} s |
| Initial remote reopen and exact scan after SSD cache deletion | {reopenTimer.Elapsed.TotalSeconds:F3} s |
| Final remote reopen and exact scan (after final cache deletion when mutations run) | {finalReopenTimer.Elapsed.TotalSeconds:F3} s |
| Update time | {updateTimer.Elapsed.TotalSeconds:F3} s |
| Delete time | {deleteTimer.Elapsed.TotalSeconds:F3} s |
| Final compaction and object publication | {finalCompactionTimer.Elapsed.TotalSeconds:F3} s |
| Remote objects under prefix | {remote.ObjectCount:N0} ({remote.TotalBytes:N0} bytes) |
| Peak local engine bytes sampled during compaction/reopen | {diskUsage.PeakLocalBytes:N0} |
| Peak SSD cache bytes sampled during compaction/reopen | {diskUsage.PeakCacheBytes:N0} |
| Peak staging bytes sampled during compaction/reopen | {diskUsage.PeakStagingBytes:N0} |
| SSD cache bytes after final verification | {cacheBytes:N0} |
| SSD cache bytes evicted before initial reopen | {initialCache.DeletedBytes:N0} |
| Total interval | {elapsedWrite.TotalSeconds:F3} s |
| Peak working set | {Process.GetCurrentProcess().PeakWorkingSet64:N0} bytes |
| Idle SSD cache budget (active leases require working space) | {options.CacheBytes:N0} bytes |

The segment manifest was committed only after the RustFS object upload, then
the local SSD copies were removed. Reopen therefore verified the read-through
cache refill and segment checksum validation against RustFS.
The direct StorageEngine surface exercised here provides point reads and
collection scans; indexed query qualification remains in E2eVolumeWorkload,
which owns the automatic-index lifecycle.
Current document and receipt bodies use disk-backed derived indexes. Revision
and history metadata still scale with the corpus; general snapshot and backup
metadata are not yet bounded. The resource envelope and checkpoint stream are
recorded separately. Backup and restore are qualified by E2eVolumeWorkload;
this segment workload intentionally does not materialize a second corpus-sized
backup artifact.
""").ConfigureAwait(false);
            receipt.Status = "complete";
            receipt.Phase = "complete";
            receipt.FinishedAtUtc = DateTimeOffset.UtcNow;
            receipt.CompletedRecords = options.Records;
            receipt.LatestResources = ResourceSample.Capture(receipt.Phase, options.Records, envelope);
            receipt.RemoteObjects = remote;
            receipt.CacheBytesAfterVerification = cacheBytes;
            receipt.PeakLocalStorageBytes = diskUsage.PeakLocalBytes;
            receipt.PeakCacheBytes = diskUsage.PeakCacheBytes;
            receipt.PeakStagingBytes = diskUsage.PeakStagingBytes;
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
                try { await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false); }
                catch (Exception receiptException) { Console.Error.WriteLine($"Could not write qualification receipt: {receiptException.Message}"); }
            }
            Console.Error.WriteLine($"Tiered RustFS qualification failed: {exception}");
            return 1;
        }
    }

    private static JsonElement CreateBody(long number, string payload, bool updated) =>
        JsonSerializer.SerializeToElement(new { number, payload, updated });

    private static void VerifyBody(Document document, long number, string payload, bool updated)
    {
        if (document.Version != (updated ? 2UL : 1UL)
            || document.Key.DocumentId != $"doc-{number:D10}"
            || document.Body.GetProperty("number").GetInt64() != number
            || document.Body.GetProperty("payload").GetString() != payload
            || document.Body.GetProperty("updated").GetBoolean() != updated)
        {
            throw new InvalidDataException($"Document doc-{number:D10} does not match the expected version or body.");
        }
    }

    private static async Task<long> VerifyScanAsync(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId collection,
        long expectedLive,
        string payload,
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
        string payload,
        long updates,
        long deletes,
        DiskUsageTracker diskUsage,
        ProgressLog progress,
        string receiptPath,
        QualificationReceiptState receipt,
        string phase,
        long started)
    {
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
        string payload,
        bool updated,
        long checkpointInterval,
        DiskUsageTracker diskUsage,
        ProgressLog progress,
        string receiptPath,
        QualificationReceiptState receipt,
        string phase,
        long started)
    {
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

            var result = await engine.ApplyBatchAsync(mutations).ConfigureAwait(false);
            if (result.Outcome != StorageBatchOutcome.Committed)
                throw new InvalidDataException($"{phase} batch at {offset} did not commit: {result.Outcome}.");

            var completed = offset + batchCount;
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
        long started)
    {
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

            var result = await engine.ApplyBatchAsync(mutations).ConfigureAwait(false);
            if (result.Outcome != StorageBatchOutcome.Committed)
                throw new InvalidDataException($"deletes batch at {offset} did not commit: {result.Outcome}.");

            var completed = offset + batchCount;
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
        diskUsage.Sample(phase, completed);
        receipt.LatestResources = ResourceSample.Capture(phase, completed, BenchmarkCgroupSnapshot.Capture());
        await progress.WriteAsync(phase, completed, total, Stopwatch.GetElapsedTime(started)).ConfigureAwait(false);
        await SaveReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
    }

    private static async Task<SegmentObjectTotals> ListRemoteObjectsAsync(AmazonS3Client client, Options options)
    {
        var request = new ListObjectsV2Request { BucketName = options.Bucket, Prefix = options.Prefix.TrimEnd('/') + "/" };
        long count = 0;
        long bytes = 0;
        do
        {
            var response = await client.ListObjectsV2Async(request).ConfigureAwait(false);
            foreach (var item in response.S3Objects)
            {
                count++;
                bytes += item.Size.GetValueOrDefault();
            }

            request.ContinuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (request.ContinuationToken is not null);

        return new SegmentObjectTotals(count, bytes);
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
        foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            bytes += new FileInfo(file).Length;
        return bytes;
    }

    private static async Task<T> MeasureOperationAsync<T>(
        Func<Task<T>> operation,
        DiskUsageTracker diskUsage,
        string phase)
    {
        diskUsage.Sample(phase, 0);
        var task = operation();
        while (!task.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            diskUsage.Sample(phase, 0);
        }

        var result = await task.ConfigureAwait(false);
        diskUsage.Sample(phase, 0);
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

        public async Task WriteAsync(string phase, long completed, long total, TimeSpan elapsed)
        {
            var checkpoint = new
            {
                utc = DateTimeOffset.UtcNow,
                phase,
                completed,
                total,
                elapsedSeconds = elapsed.TotalSeconds,
                workingSetBytes = Process.GetCurrentProcess().WorkingSet64,
                managedBytes = GC.GetTotalMemory(forceFullCollection: false),
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

    private sealed class QualificationReceiptState
    {
        public string Status { get; set; } = "running";
        public string Phase { get; set; } = "initializing";
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public long TargetRecords { get; set; }
        public long CompletedRecords { get; set; }
        public string? SubjectProject { get; set; }
        public string? ResourceEnvelopePath { get; set; }
        public string? ProgressPath { get; set; }
        public ResourceSample? LatestResources { get; set; }
        public SegmentObjectTotals? RemoteObjects { get; set; }
        public CacheEvictionStats? CacheEviction { get; set; }
        public CacheEvictionStats? InitialCacheEviction { get; set; }
        public CacheEvictionStats? FinalCacheEviction { get; set; }
        public long CacheBytesAfterVerification { get; set; }
        public long PeakLocalStorageBytes { get; set; }
        public long PeakCacheBytes { get; set; }
        public long PeakStagingBytes { get; set; }
        public string? Error { get; set; }
    }

    private sealed record ResourceSample(
        string Phase,
        long CompletedRecords,
        long WorkingSetBytes,
        long PeakWorkingSetBytes,
        long ManagedBytes,
        BenchmarkCgroupSnapshot Cgroup)
    {
        public static ResourceSample Capture(string phase, long completed, BenchmarkCgroupSnapshot cgroup) =>
            new(phase, completed, Process.GetCurrentProcess().WorkingSet64, Process.GetCurrentProcess().PeakWorkingSet64,
                GC.GetTotalMemory(forceFullCollection: false), cgroup);
    }

    private sealed record SegmentObjectTotals(long ObjectCount, long TotalBytes);

    private sealed record CacheEvictionStats(long DeletedFiles, long DeletedBytes);

    private sealed class DiskUsageTracker(string root, string cache)
    {
        public long PeakLocalBytes { get; private set; }
        public long PeakCacheBytes { get; private set; }
        public long PeakStagingBytes { get; private set; }

        public void Sample(string _, long __)
        {
            var local = DirectoryBytes(root);
            var cached = DirectoryBytes(cache);
            var staging = DirectoryBytes(root, "*.tmp");
            PeakLocalBytes = Math.Max(PeakLocalBytes, local);
            PeakCacheBytes = Math.Max(PeakCacheBytes, cached);
            PeakStagingBytes = Math.Max(PeakStagingBytes, staging);
        }
    }

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
        public long CacheBytes { get; private set; } = 256L * 1024 * 1024;
        public long Updates { get; private set; }
        public long Deletes { get; private set; }
        public int PointReads { get; private set; } = 512;
        public long CheckpointRecords { get; private set; } = 1_000_000;
        public int Seed { get; private set; } = 1337;
        public bool Help { get; private set; }

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
                    case "cache-bytes": result.CacheBytes = ParseLong(value, name); break;
                    case "updates": result.Updates = ParseNonNegativeLong(value, name); break;
                    case "deletes": result.Deletes = ParseNonNegativeLong(value, name); break;
                    case "point-reads": result.PointReads = ParseNonNegativeInt(value, name); break;
                    case "checkpoint-records": result.CheckpointRecords = ParsePositiveLong(value, name); break;
                    case "seed": result.Seed = ParseInt(value, name); break;
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

            return result;
        }

        public static void PrintHelp() => Console.WriteLine("--workload tiered-segment --records N --batch N --payload-bytes N --updates N --deletes N --point-reads N --checkpoint-records N --seed N --cache-bytes N --s3-endpoint URL --s3-bucket NAME --output DIR (credentials: TERRABASE_S3_ACCESS_KEY / TERRABASE_S3_SECRET_KEY)");

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
