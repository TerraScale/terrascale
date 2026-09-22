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
    public static bool IsRequested(string[] args) => ReadWorkload(args) is "tiered-segment" or "rustfs-tiered";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Help)
            {
                Options.PrintHelp();
                return 0;
            }
            if (typeof(TieredRustFsQualificationWorkload).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration != "Release")
                throw new InvalidOperationException("Tiered qualification requires a Release artifact.");

            Directory.CreateDirectory(options.Output);
            var envelopePath = Path.Combine(options.Output, "resource-envelope.json");
            await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(BenchmarkCgroupSnapshot.Capture())).ConfigureAwait(false);
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
            StorageCompactionResult compaction;
            Console.WriteLine($"phase: seed {options.Records:N0} records");
            await using (var engine = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered))
            {
                for (var offset = 0; offset < options.Records; offset += options.Batch)
                {
                    var count = Math.Min(options.Batch, options.Records - offset);
                    var mutations = new StorageMutation[count];
                    for (var index = 0; index < count; index++)
                    {
                        var number = offset + index;
                        mutations[index] = StorageMutation.Upsert(
                            new DocumentKey(tenant, collection, $"doc-{number:D10}"),
                            JsonSerializer.SerializeToElement(new { number, payload }),
                            DocumentPrecondition.Missing);
                    }

                    var result = await engine.ApplyBatchAsync(mutations).ConfigureAwait(false);
                    if (result.Outcome != StorageBatchOutcome.Committed)
                    {
                        throw new InvalidDataException($"Batch at {offset} did not commit: {result.Outcome}.");
                    }
                }

                writeTimer.Stop();
                Console.WriteLine("phase: compact and publish to S3");
                compactionTimer.Start();
                compaction = await engine.CompactAsync().ConfigureAwait(false);
                compactionTimer.Stop();
            }

            writeTimer.Stop();
            var cacheFiles = Directory.Exists(cache)
                ? Directory.EnumerateFiles(cache, "*.seg", SearchOption.AllDirectories).ToArray()
                : [];
            foreach (var file in cacheFiles)
            {
                File.Delete(file);
            }

            var reopenTimer = Stopwatch.StartNew();
            Console.WriteLine("phase: remote reopen and exact scan");
            var first = new DocumentKey(tenant, collection, "doc-0000000000");
            var last = new DocumentKey(tenant, collection, $"doc-{options.Records - 1:D10}");
            await using var reopened = new DurableStorageEngine(root, new LocalStorageTierPlacement(root), StorageWalDurability.Synced, tiered);
            var firstDocument = await reopened.ReadAsync(first).ConfigureAwait(false)
                ?? throw new InvalidDataException("The first document was not recovered from RustFS.");
            var lastDocument = await reopened.ReadAsync(last).ConfigureAwait(false)
                ?? throw new InvalidDataException("The last document was not recovered from RustFS.");
            var scanCount = 0;
            await foreach (var document in reopened.ScanAsync(tenant, collection).ConfigureAwait(false))
            {
                if (document.Key.DocumentId != $"doc-{scanCount:D10}"
                    || document.Version != 1
                    || document.Body.GetProperty("number").GetInt32() != scanCount
                    || document.Body.GetProperty("payload").GetString() != payload)
                    throw new InvalidDataException($"Recovered document at position {scanCount} does not match the seeded data.");
                scanCount++;
            }
            if (scanCount != options.Records
                || firstDocument.Body.GetProperty("number").GetInt32() != 0
                || lastDocument.Body.GetProperty("number").GetInt32() != options.Records - 1)
                throw new InvalidDataException("Remote reopen did not recover the complete corpus.");

            reopenTimer.Stop();
            var envelope = BenchmarkCgroupSnapshot.Capture();
            await File.WriteAllTextAsync(envelopePath, JsonSerializer.Serialize(envelope)).ConfigureAwait(false);
            var elapsedWrite = Stopwatch.GetElapsedTime(started);
            var summary = Path.Combine(options.Output, "tiered-rustfs-summary.md");
            await File.WriteAllTextAsync(summary, $"""
# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `{endpoint}` |
| Bucket | `{options.Bucket}` |
| Durability | Synced WAL, one device barrier per batch of {options.Batch} |
| Records (exact bodies and versions verified) | {options.Records:N0} |
| Repeated-character payload bytes per record | {options.PayloadBytes:N0} |
| Minimum logical payload bytes | {(long)options.Records * options.PayloadBytes:N0} |
| Observed cgroup memory.max | {envelope.MemoryMax ?? "unavailable"} |
| Observed cgroup memory.peak | {envelope.MemoryPeak ?? "unavailable"} |
| Observed cgroup memory.swap.max | {envelope.SwapMax ?? "unavailable"} |
| Published segments | {compaction.PublishedSegments.Count} |
| Compaction records | {compaction.RecordsWritten:N0} |
| Synced seed time | {writeTimer.Elapsed.TotalSeconds:F3} s |
| Synced writes/sec | {options.Records / writeTimer.Elapsed.TotalSeconds:F0} |
| Compaction and verified object publication | {compactionTimer.Elapsed.TotalSeconds:F3} s |
| Reopen and exact scan after SSD cache deletion | {reopenTimer.Elapsed.TotalSeconds:F3} s |
| Exact scan count | {scanCount:N0} |
| Total interval | {elapsedWrite.TotalSeconds:F3} s |
| Peak working set | {Process.GetCurrentProcess().PeakWorkingSet64:N0} bytes |
| Idle SSD cache budget (active leases require working space) | {options.CacheBytes:N0} bytes |

The segment manifest was committed only after the RustFS object upload, then
the local SSD copies were removed. Reopen therefore verified the read-through
cache refill and segment checksum validation against RustFS.
Current document and receipt bodies use disk-backed derived indexes. Revision
and history metadata still scale with the corpus; general snapshot and backup
metadata are not yet bounded. The resource envelope is recorded separately.
This run does not qualify 100 million records or arbitrary dataset sizes.
""").ConfigureAwait(false);
            Console.WriteLine($"Tiered RustFS summary: {summary}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tiered RustFS qualification failed: {exception}");
            return 1;
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
        public int Records { get; private set; } = 10_000;
        public int Batch { get; private set; } = 256;
        public int PayloadBytes { get; private set; } = 128;
        public long CacheBytes { get; private set; } = 256L * 1024 * 1024;
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
                    case "records": result.Records = ParsePositive(value, name); break;
                    case "batch": result.Batch = ParsePositive(value, name); break;
                    case "payload-bytes": result.PayloadBytes = ParsePositive(value, name); break;
                    case "cache-bytes": result.CacheBytes = ParseLong(value, name); break;
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

            return result;
        }

        public static void PrintHelp() => Console.WriteLine("--workload tiered-segment --records N --batch N --payload-bytes N --s3-endpoint URL --s3-bucket NAME --output DIR (credentials: TERRABASE_S3_ACCESS_KEY / TERRABASE_S3_SECRET_KEY)");

        private static int ParsePositive(string value, string name) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"--{name} must be a positive integer.");

        private static long ParseLong(string value, string name) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"--{name} must be a non-negative integer.");
    }
}
