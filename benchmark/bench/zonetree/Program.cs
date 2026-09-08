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
using System.Globalization;
using System.Text;
using System.Text.Json;
using ZoneTree;
using ZoneTree.Options;

// A barrier phase that never completes within the budget means a slot died
// mid-phase; failing loudly beats deadlocking the sampler.
var barrierTimeout = TimeSpan.FromMinutes(10);

var mode = "sync";
var workload = "read";
var outDir = "bench/results";
var dataDir = "";
var seedDocuments = 1_000;
var operations = 10_000;
var samples = 5;
var concurrency = 16;
var warmupOperations = 1_000;

for (var i = 0; i + 1 < args.Length; i++)
{
    string Value() => args[++i];
    switch (args[i])
    {
        case "--mode": mode = Value(); break;
        case "--workload": workload = Value(); break;
        case "--out": outDir = Value(); break;
        case "--data-dir": dataDir = Value(); break;
        case "--seed": seedDocuments = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "--ops": operations = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "--samples": samples = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "--concurrency": concurrency = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "--warmup": warmupOperations = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        default: throw new ArgumentException($"unknown or incomplete argument: {args[i]}");
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

Zone.Primary = primary;
Zone.Secondary = secondary;
Zone.SeedDocuments = seedDocuments;
Zone.Payload = new string('x', 256);

IZoneTree<string, string> OpenZone(string directory) =>
    new ZoneTreeFactory<string, string>()
        .SetDataDirectory(directory)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = walMode)
        .OpenOrCreate();

// Seeding, like the sqlite driver's admin loop: 1,000 documents in both trees.
for (var index = 0; index < seedDocuments; index++)
{
    primary.Upsert(Zone.PrimaryKey(Zone.SeededId(index)), $"{Zone.SeededName(index)}|{index % 100}|{Zone.Payload}");
    secondary.Upsert(Zone.SecondaryKey(Zone.SeededName(index)), Zone.SeededId(index));
}

// The lane executor, mirroring bench/src/lane.rs: one thread per concurrency
// slot, warm-up excluded, every measured sample between barriers of size
// concurrency+1, per-operation latencies, wall clock from the coordinator.
// Every worker reaches the end barrier even after an operation error, so a
// failure can never strand the sampler — the same discipline the Rust lane
// enforces; barrier timeouts guard the catastrophic path instead.
var warmupEnd = new Barrier(concurrency + 1);
var sampleStart = new Barrier(concurrency + 1);
var sampleEnd = new Barrier(concurrency + 1);
var failed = 0;

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
                if (!Zone.OneOp(workload, warmupOffset + index))
                {
                    error = $"warm-up operation {index} failed: {Zone.LastError}";
                    Interlocked.CompareExchange(ref failed, 1, 0);
                    break;
                }
            }

            if (!warmupEnd.SignalAndWait(barrierTimeout))
                throw new TimeoutException("warm-up barrier timed out");
            if (Volatile.Read(ref failed) != 0)
                return;

            var slotOperations = perSlotOperations + (slotIndex < operationRemainder ? 1 : 0);
            var operationOffset = slotIndex * perSlotOperations + Math.Min(slotIndex, operationRemainder);
            mySamples = new List<double>[samples];
            for (var sample = 0; sample < samples; sample++)
            {
                if (!sampleStart.SignalAndWait(barrierTimeout))
                    throw new TimeoutException("sample start barrier timed out");
                var latencies = new List<double>(slotOperations);
                for (var index = 0; index < slotOperations; index++)
                {
                    var windowIndex = sample * operations + operationOffset + index;
                    var before = Stopwatch.GetTimestamp();
                    if (!Zone.OneOp(workload, windowIndex))
                    {
                        error = $"measured operation {windowIndex} failed: {Zone.LastError}";
                        Interlocked.CompareExchange(ref failed, 1, 0);
                        break;
                    }
                    latencies.Add((Stopwatch.GetTimestamp() - before) * 1_000_000.0 / Stopwatch.Frequency);
                }
                if (!sampleEnd.SignalAndWait(barrierTimeout))
                    throw new TimeoutException("sample end barrier timed out");
                if (Volatile.Read(ref failed) != 0)
                    return;
                mySamples[sample] = latencies;
            }
        }
        catch (Exception exception)
        {
            error = $"slot {slotIndex} failed: {exception}";
            Interlocked.CompareExchange(ref failed, 1, 0);
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

foreach (var thread in threads)
    thread.Start();

// Coordinator: times each sample from the start barrier's release to the
// last slot's arrival at the end barrier, then joins and merges.
var walls = new double[samples];
var coordinatorError = (string?)null;
try
{
    if (!warmupEnd.SignalAndWait(barrierTimeout))
        throw new TimeoutException("warm-up barrier timed out");
    if (Volatile.Read(ref failed) == 0)
    {
        for (var sample = 0; sample < samples; sample++)
        {
            if (!sampleStart.SignalAndWait(barrierTimeout))
                throw new TimeoutException("sample start barrier timed out");
            var before = Stopwatch.GetTimestamp();
            if (!sampleEnd.SignalAndWait(barrierTimeout))
                throw new TimeoutException("sample end barrier timed out");
            walls[sample] = (Stopwatch.GetTimestamp() - before) * 1.0 / Stopwatch.Frequency;
            if (Volatile.Read(ref failed) != 0)
                break;
        }
    }
}
catch (Exception exception)
{
    coordinatorError = $"coordinator failed: {exception}";
    Interlocked.CompareExchange(ref failed, 1, 0);
}

foreach (var thread in threads)
    thread.Join(barrierTimeout);

var firstError = coordinatorError ?? threadErrors.FirstOrDefault(error => error is not null);

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
if (firstError is not null)
{
    var failure = new
    {
        status = "failed",
        target = $"zonetree-{mode}",
        workload,
        config = new { seed_documents = seedDocuments, operations, samples, concurrency, warmup_operations = warmupOperations },
        error = firstError,
        environment,
        samples = Array.Empty<object>(),
        limits,
    };
    var failurePath = WriteReceipt(JsonSerializer.Serialize(failure, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine($"the reference run failed: {firstError}");
    Console.Error.WriteLine($"failed result retained: {failurePath}");
    return 1;
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
    Console.WriteLine(FormattableString.Invariant(
        $"  sample {sample}: {measured.operations} ops in {measured.wall_seconds:F3}s = {measured.throughput_ops_per_s:F0} ops/s, " +
        $"p50 {measured.p50_us:F1}us p95 {measured.p95_us:F1}us p99 {measured.p99_us:F1}us"));
Console.WriteLine(FormattableString.Invariant($"elapsed: {(Stopwatch.GetTimestamp() - startedAt) * 1.0 / Stopwatch.Frequency:F1}s"));
Console.WriteLine($"written: {WriteReceipt(JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }))}");
return 0;

string WriteReceipt(string json)
{
    var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var path = Path.Combine(outDir, $"zonetree-{mode}-{workload}-{stamp}-{Environment.ProcessId}-{attempt}.json");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                stream.Write(Encoding.UTF8.GetBytes(json));
            return path;
        }
        catch (IOException) when (File.Exists(path))
        {
            // attempt name taken; try the next
        }
    }
    throw new IOException("could not allocate a unique benchmark result name");
}

/// <summary>
/// The lane's benchmark document and its ZoneTree layout, shared by every
/// operation so the seeded and written shapes cannot drift from the sqlite
/// and postgresql drivers.
/// </summary>
static class Zone
{
    public const string Tenant = "benchmark-tenant";
    public const string MeasuredName = "measured";
    public const int MeasuredGroup = 42;

    /// <summary>The error message of the last failed operation on this thread.</summary>
    [ThreadStatic]
    public static string? LastError;

    public static IZoneTree<string, string>? Primary { get; set; }
    public static IZoneTree<string, string>? Secondary { get; set; }
    public static int SeedDocuments { get; set; }
    public static string Payload { get; set; } = "";

    public static string SeededName(int index) => $"user-{index}";
    public static string SeededId(int index) => $"seed-{index}";
    public static string MeasuredId(int index) => $"measured-{index}";
    public static string PrimaryKey(string id) => $"{Tenant}|{id}";
    public static string SecondaryKey(string name) => $"{Tenant}|{name}";

    /// <summary>Executes one workload operation; false plus <see cref="LastError"/> on failure.</summary>
    public static bool OneOp(string laneWorkload, int index)
    {
        try
        {
            LastError = null;
            // V1's mixed ratio: index % 10 < 5 read, < 8 write, else query.
            var step = laneWorkload == "mixed" ? index % 10 : -1;
            var operation = step switch
            {
                < 5 => "read",
                < 8 => "write",
                _ when laneWorkload == "mixed" => "query",
                _ => laneWorkload,
            };
            switch (operation)
            {
                case "read":
                    if (!Primary!.TryGet(PrimaryKey(SeededId(index % Math.Max(SeedDocuments, 1))), out _))
                        throw new InvalidOperationException("a seeded read must return a document");
                    return true;
                case "write":
                    var id = MeasuredId(index);
                    Primary!.Upsert(PrimaryKey(id), $"{MeasuredName}|{MeasuredGroup}|{Payload}");
                    Secondary!.Upsert(SecondaryKey(MeasuredName), id);
                    return true;
                default:
                    if (!Secondary!.TryGet(SecondaryKey(SeededName(index % Math.Max(SeedDocuments, 1))), out _))
                        throw new InvalidOperationException("an indexed equality query must return a document");
                    return true;
            }
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            return false;
        }
    }
}
