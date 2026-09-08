// zonetree-bench — the raw ZoneTree LSM reference for the point-op lane
// contract, mirroring terrabase-bench-lane's sqlite/pg drivers.
//
// The "async runtime" comparison is ZoneTree's write-ahead log mode:
//
//   --mode sync   WriteAheadLogMode.Sync — the plain synchronous WAL stream;
//                 the caller waits until the record is in the WAL, the same
//                 caller-waited durability class as SQLite synchronous=FULL.
//   --mode async  WriteAheadLogMode.AsyncCompressed — the library default:
//                 compressed WAL records through a background path. Higher
//                 throughput, durability deferred to the background writer.
//
// Everything else is the lane contract verbatim: 1,000 seeds / 10,000 ops
// per sample / 5 samples / concurrency 16 / 1,000 warm-up ops excluded from
// every measured sample, barriers of size concurrency+1 around each sample,
// per-operation latencies, and the same document shape (tenant, id, name,
// group, 256-byte payload). The indexed (tenant, name) shape is a secondary
// ZoneTree the write lane maintains explicitly — the KV-store analogue of
// SQLite's second B-tree.
//
// Usage (repository root, pinned to two CPUs like the other references):
//
//   taskset -c 0,1 bench/zonetree/bin/Release/net11.0/zonetree-bench \
//     --mode sync --workload read --out bench/results

using System.Diagnostics;
using System.Text.Json;
using ZoneTree;
using ZoneTree.Options;

var mode = "sync";
var workload = "read";
var outDir = "bench/results";
var dataDir = "";
var seedDocuments = 1_000;
var operations = 10_000;
var samples = 5;
var concurrency = 16;
var warmupOperations = 1_000;

var arguments = args.AsSpan;
for (var i = 0; i < arguments.Length; i++)
{
    string Value() => i + 1 < arguments.Length ? arguments[++i] : throw new ArgumentException($"--{arguments[i]} requires a value");
    switch (arguments[i])
    {
        case "--mode": mode = Value(); break;
        case "--workload": workload = Value(); break;
        case "--out": outDir = Value(); break;
        case "--data-dir": dataDir = Value(); break;
        case "--seed": seedDocuments = int.Parse(Value()); break;
        case "--ops": operations = int.Parse(Value()); break;
        case "--samples": samples = int.Parse(Value()); break;
        case "--concurrency": concurrency = int.Parse(Value()); break;
        case "--warmup": warmupOperations = int.Parse(Value()); break;
        default: throw new ArgumentException($"unknown argument: {arguments[i]}");
    }
}

if (mode is not ("sync" or "async"))
    throw new ArgumentException($"--mode must be sync or async, got {mode}");
if (workload is not ("read" or "write" or "query" or "mixed"))
    throw new ArgumentException($"--workload must be read, write, query, or mixed, got {workload}");
if (seedDocuments < 1 || operations < 1 || samples < 1 || concurrency < 1 || warmupOperations < 1)
    throw new ArgumentException("seed, ops, samples, concurrency, and warmup must be positive");

var walMode = mode == "sync" ? WriteAheadLogMode.Sync : WriteAheadLogMode.AsyncCompressed;
dataDir = dataDir.Length == 0 ? $"bench/target/zonetree-{mode}" : dataDir;

var startedAt = Stopwatch.GetTimestamp();

// A fresh store per run, the same discipline as the sqlite driver.
if (Directory.Exists(dataDir))
    Directory.Delete(dataDir, recursive: true);
Directory.CreateDirectory(dataDir);

var zoneTreeVersion = typeof(ZoneTreeFactory<,>).Assembly.GetName().Version?.ToString() ?? "unknown";

var primary = OpenZone(Path.Combine(dataDir, "primary"));
var secondary = OpenZone(Path.Combine(dataDir, "secondary"));

IZoneTree<string, string> OpenZone(string directory) =>
    new ZoneTreeFactory<string, string>()
        .SetDataDirectory(directory)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = walMode)
        .OpenOrCreate();

// Seeding, like the sqlite driver's admin loop: 1,000 documents in both trees.
var payload = new string('x', 256);
for (var index = 0; index < seedDocuments; index++)
{
    primary.Upsert(PrimaryKey(Document.SeededId(index)), $"{Document.SeededName(index)}|{index % 100}|{payload}");
    secondary.Upsert(SecondaryKey(Document.SeededName(index)), Document.SeededId(index));
}

// The lane executor, mirroring bench/src/lane.rs: one thread per concurrency
// slot, warm-up excluded, every measured sample between barriers of size
// concurrency+1, per-operation latencies, wall clock from the coordinator.
var warmupEnd = new Barrier(concurrency + 1);
var sampleStart = new Barrier(concurrency + 1);
var sampleEnd = new Barrier(concurrency + 1);
var failed = 0;
var firstError = (string?)null;

var perSlotWarmup = warmupOperations / concurrency;
var warmupRemainder = warmupOperations % concurrency;
var perSlotOperations = operations / concurrency;
var operationRemainder = operations % concurrency;

var threads = new Thread[concurrency];
var threadSamples = new List<double>?[concurrency];
var threadErrors = new string?[concurrency];

for (var slot = 0; slot < concurrency; slot++)
{
    var slotIndex = slot;
    threads[slot] = new Thread(() =>
    {
        List<double>[]? mySamples = null;
        string? error = null;
        try
        {
            // Warm-up: real work, excluded from every measured sample.
            var warmupOffset = slotIndex * perSlotWarmup + Math.Min(slotIndex, warmupRemainder);
            var slotWarmup = perSlotWarmup + (slotIndex < warmupRemainder ? 1 : 0);
            for (var index = 0; index < slotWarmup; index++)
            {
                if (!OneOp(warmupOffset + index, out var warmupError))
                {
                    error = $"warm-up operation {index} failed: {warmupError}";
                    Interlocked.CompareExchange(ref failed, 1, 0);
                    break;
                }
            }

            warmupEnd.SignalAndWait();
            if (Volatile.Read(ref failed) != 0)
                return;

            var slotOperations = perSlotOperations + (slotIndex < operationRemainder ? 1 : 0);
            var operationOffset = slotIndex * perSlotOperations + Math.Min(slotIndex, operationRemainder);
            mySamples = new List<double>[samples];
            for (var sample = 0; sample < samples; sample++)
            {
                sampleStart.SignalAndWait();
                var latencies = new List<double>(slotOperations);
                var aborted = false;
                for (var index = 0; index < slotOperations; index++)
                {
                    var windowIndex = sample * operations + operationOffset + index;
                    var before = Stopwatch.GetTimestamp();
                    if (!OneOp(windowIndex, out var operationError))
                    {
                        error = $"measured operation {windowIndex} failed: {operationError}";
                        Interlocked.CompareExchange(ref failed, 1, 0);
                        aborted = true;
                        break;
                    }
                    latencies.Add((Stopwatch.GetTimestamp() - before) * 1_000_000.0 / Stopwatch.Frequency);
                }
                sampleEnd.SignalAndWait();
                if (Volatile.Read(ref failed) != 0)
                    return;
                mySamples[sample] = latencies;
                _ = aborted;
            }
        }
        catch (Exception exception)
        {
            error = $"slot {slotIndex} failed: {exception}";
            Interlocked.CompareExchange(ref failed, 1, 0);
            warmupEnd.RemoveParticipant();
        }
        finally
        {
            threadSamples[slotIndex] = mySamples;
            threadErrors[slotIndex] = error;
        }
    })
    {
        IsBackground = true,
    };
}

bool OneOp(int index, out string? error)
{
    try
    {
        error = null;
        switch (workload)
        {
            case "read":
                if (!primary.TryGet(PrimaryKey(Document.SeededId(index % Math.Max(seedDocuments, 1))), out _))
                    throw new InvalidOperationException("a seeded read must return a document");
                return true;
            case "write":
                var id = Document.MeasuredId((uint)index);
                primary.Upsert(PrimaryKey(id), $"{Document.MeasuredName}|{Document.MeasuredGroup}|{payload}");
                secondary.Upsert(SecondaryKey(Document.MeasuredName), id);
                return true;
            case "query":
                if (!secondary.TryGet(SecondaryKey(Document.SeededName(index % Math.Max(seedDocuments, 1))), out _))
                    throw new InvalidOperationException("an indexed equality query must return a document");
                return true;
            default:
                // V1's mixed ratio: index % 10 < 5 read, < 8 write, else query.
                var step = index % 10;
                if (step < 5)
                {
                    if (!primary.TryGet(PrimaryKey(Document.SeededId(index % Math.Max(seedDocuments, 1))), out _))
                        throw new InvalidOperationException("a seeded read must return a document");
                }
                else if (step < 8)
                {
                    var id = Document.MeasuredId((uint)index);
                    primary.Upsert(PrimaryKey(id), $"{Document.MeasuredName}|{Document.MeasuredGroup}|{payload}");
                    secondary.Upsert(SecondaryKey(Document.MeasuredName), id);
                }
                else if (!secondary.TryGet(SecondaryKey(Document.SeededName(index % Math.Max(seedDocuments, 1))), out _))
                {
                    throw new InvalidOperationException("an indexed equality query must return a document");
                }
                return true;
        }
    }
    catch (Exception exception)
    {
        error = exception.Message;
        return false;
    }
}

static class Document
{
    public const string Tenant = "benchmark-tenant";
    public const string MeasuredName = "measured";
    public const int MeasuredGroup = 42;

    public static string SeededName(int index) => $"user-{index}";
    public static string SeededId(int index) => $"seed-{index}";
    public static string MeasuredId(uint index) => $"measured-{index}";
    public static string PrimaryKey(string id) => $"{Tenant}|{id}";
    public static string SecondaryKey(string name) => $"{Tenant}|{name}";
}

foreach (var thread in threads)
    thread.Start();

// Coordinator: same barrier discipline as lane.rs. Every worker reaches the
// end barrier even after an error, so a failure can never strand the sampler.
warmupEnd.SignalAndWait();
var walls = new double[samples];
for (var sample = 0; sample < samples; sample++)
{
    sampleStart.SignalAndWait();
    var before = Stopwatch.GetTimestamp();
    sampleEnd.SignalAndWait();
    walls[sample] = (Stopwatch.GetTimestamp() - before) * 1.0 / Stopwatch.Frequency;
    if (Volatile.Read(ref failed) != 0)
        break;
}

foreach (var thread in threads)
    thread.Join();

var firstErrorText = threadErrors.FirstOrDefault(error => error is not null);

var environment = new
{
    observed_identity = $"ZoneTree {zoneTreeVersion}; WAL={walMode}; mutable segment default 1,000,000 items",
    endpoint = $"file://{Path.GetFullPath(dataDir)}",
    client_transport = $"embedded ZoneTree {zoneTreeVersion}, one shared LSM per tree, {walMode} write-ahead log",
    unix_seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    kernel = File.Exists("/proc/sys/kernel/osrelease")
        ? $"Linux {File.ReadAllText("/proc/sys/kernel/osrelease").Trim()}"
        : "unknown",
    logical_cpus = Environment.ProcessorCount,
};

var limits = new[]
{
    "embedded reference: the measured member is this driver's own process, so the load generator and the database share one resource envelope",
    $"LSM with the default 1,000,000-item mutable segment: the whole working set stays in the mutable segment, writes are {walMode} WAL appends, and no disk segment forms during measurement",
    "the write lane maintains the primary zone and the secondary (tenant, name) zone explicitly — the KV-store analogue of SQLite's two B-trees per indexed upsert",
    walMode == WriteAheadLogMode.AsyncCompressed
        ? "async mode: recent writes can be lost if the process terminates before the background WAL work is durable; durability is deferred, not caller-waited"
        : "sync mode: the caller waits for each record in the plain WAL stream; full power-loss durability still depends on OS, filesystem, and device",
};

Directory.CreateDirectory(outDir);
string receiptPath;
if (firstErrorText is not null)
{
    var failure = new
    {
        status = "failed",
        target = $"zonetree-{mode}",
        workload,
        config = new { seed_documents = seedDocuments, operations, samples, concurrency, warmup_operations = warmupOperations },
        error = firstErrorText,
        environment,
        samples = Array.Empty<object>(),
        limits,
    };
    receiptPath = WriteReceipt(JsonSerializer.Serialize(failure, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine($"the reference run failed: {firstErrorText}");
    Console.Error.WriteLine($"failed result retained: {receiptPath}");
    Environment.Exit(1);
}

var measuredSamples = walls
    .Select((wall, sample) =>
    {
        var latencies = new List<double>(operations);
        foreach (var slotSamples in threadSamples)
            latencies.AddRange(slotSamples![sample]);
        latencies.Sort();
        double Percentile(double fraction) =>
            latencies.Count == 0 ? 0.0 : latencies[Math.Min((int)Math.Round((latencies.Count - 1) * fraction), latencies.Count - 1)];
        return new
        {
            operations = (ulong)operations,
            wall_seconds = wall,
            throughput_ops_per_s = operations / Math.Max(wall, double.Epsilon),
            p50_us = Percentile(0.50),
            p95_us = Percentile(0.95),
            p99_us = Percentile(0.99),
        };
    })
    .ToArray();

var receipt = new
{
    status = "complete",
    target = $"zonetree-{mode}",
    workload,
    config = new { seed_documents = seedDocuments, operations, samples, concurrency, warmup_operations = warmupOperations },
    environment,
    samples = measuredSamples,
    limits,
};

Console.WriteLine($"target: zonetree-{mode} workload: {workload} samples: {measuredSamples.Length}");
foreach (var (sample, measured) in measuredSamples.Index())
    Console.WriteLine(
        $"  sample {sample}: {measured.operations} ops in {measured.wall_seconds:F3}s = {measured.throughput_ops_per_s:F0} ops/s, " +
        $"p50 {measured.p50_us:F1}us p95 {measured.p95_us:F1}us p99 {measured.p99_us:F1}us");
Console.WriteLine($"elapsed: {(Stopwatch.GetTimestamp() - startedAt) * 1.0 / Stopwatch.Frequency:F1}s");
receiptPath = WriteReceipt(JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"written: {receiptPath}");
Environment.Exit(0);

string WriteReceipt(string json)
{
    var stamp = DateTimeOffset.UtcNow.UtcDateTime.ToUnixTimeMilliseconds() * 1_000_000L;
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var path = Path.Combine(outDir, $"zonetree-{mode}-{workload}-{stamp}-{Environment.ProcessId}-{attempt}.json");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew);
            File.WriteAllText(path, json);
            return path;
        }
        catch (IOException) when (File.Exists(path))
        {
            // attempt name taken; try the next
        }
    }
    throw new IOException("could not allocate a unique benchmark result name");
}
