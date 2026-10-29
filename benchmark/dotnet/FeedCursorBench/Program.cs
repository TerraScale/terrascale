using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

if (args.Length > 0 && args[0] == "readers")
{
    await FeedReadersBenchmark.RunAsync().ConfigureAwait(false);
    return;
}

var mode = args[0];
var data = Path.GetFullPath(args[1]);
const int count = 1_000_000;
var tenant = new TenantContext("00000000000000000000000001", PlanTier.Hobby,
    "00000000000000000000000002", "00000000000000000000000003");
var options = new TerraBaseOptions { DataDirectory = data, MemoryBudget = 128 * 1024 * 1024, StorageThreads = 2, StorageQueue = 1024 };
if (mode == "seed")
{
    await using (var setup = new ServiceCollection().AddTerraBaseEngine(options).BuildServiceProvider())
    {
        _ = await setup.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(tenant, CancellationToken.None).ConfigureAwait(false);
    }

    using var connection = new SqliteConnection($"Data Source={Path.Combine(data, tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId + ".db")}");
    connection.Open();
    using var transaction = connection.BeginTransaction();
    using var floor = connection.CreateCommand();
    floor.Transaction = transaction;
    floor.CommandText = "INSERT INTO _tb_feed_floor (origin, stamp, prefix) VALUES (12345, 0, zeroblob(32));";
    _ = floor.ExecuteNonQuery();
    using var insert = connection.CreateCommand();
    insert.Transaction = transaction;
    insert.CommandText = "INSERT INTO _tb_feed (seq, commit_offset, origin, stamp, collection, id, deleted, prefix) VALUES ($seq, 1, 12345, $stamp, 'items', $id, 0, $prefix);";
    var sequenceParameter = insert.Parameters.AddWithValue("$seq", 0L);
    var stampParameter = insert.Parameters.AddWithValue("$stamp", 0L);
    var idParameter = insert.Parameters.AddWithValue("$id", "");
    var prefixParameter = insert.Parameters.AddWithValue("$prefix", new byte[32]);
    var start = FeedCursor.At(DateTimeOffset.UtcNow).Value;
    var prefix = FeedPrefix.Empty(default);
    for (var i = 0; i < count; i++)
    {
        sequenceParameter.Value = (long)i + 1;
        stampParameter.Value = (long)(start + (ulong)i);
        idParameter.Value = i.ToString("D26", CultureInfo.InvariantCulture);
        prefix = prefix.Append(new Hlc(start + (ulong)i), "items", (string)idParameter.Value, deleted: false);
        prefixParameter.Value = prefix.Hash.ToArray();
        _ = insert.ExecuteNonQuery();
    }

    using var counters = connection.CreateCommand();
    counters.Transaction = transaction;
    counters.CommandText = "UPDATE _tb_feed_sequence SET seq = $seq WHERE id = 1; UPDATE commit_offset SET value = 1 WHERE id = 1;";
    counters.Parameters.AddWithValue("$seq", (long)count);
    _ = counters.ExecuteNonQuery();
    transaction.Commit();
    Console.WriteLine($"Seeded {count} events.");
    return;
}

var group = File.ReadAllLines("/proc/self/cgroup").Single(line => line.StartsWith("0::", StringComparison.Ordinal))[3..];
foreach (var file in new[] { "memory.max", "memory.swap.max", "cpu.max" })
{
    Console.WriteLine($"{file}={File.ReadAllText(Path.Combine("/sys/fs/cgroup" + group, file)).Trim()}");
}

Console.WriteLine(File.ReadAllLines("/proc/self/status").Single(line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal)));

await using var provider = new ServiceCollection().AddTerraBaseEngine(options).BuildServiceProvider();
var store = await provider.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None).ConfigureAwait(false)
    ?? throw new InvalidOperationException("Seed the fixture first.");
await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None).ConfigureAwait(false);
var head = await read.GetFeedHeadAsync(CancellationToken.None).ConfigureAwait(false);
var now = FeedCursor.At(DateTimeOffset.UtcNow);
var build = Stopwatch.StartNew();
var proof = await read.ReadFeedAsync(new FeedCursor(head, now), null, 256, 4 * 1024 * 1024, CancellationToken.None).ConfigureAwait(false);
build.Stop();
var cursor = new FeedCursor(proof.Scanned, now) { Issuer = proof.Issuer, Prefixes = proof.Prefixes };
Console.WriteLine(FormattableString.Invariant($"proof_build_ms={build.Elapsed.TotalMilliseconds:F3}"));
Console.WriteLine("| iteration | validation ms | allocated MiB | peak RSS MiB |");
Console.WriteLine("| ---: | ---: | ---: | ---: |");
for (var i = 0; i < 5; i++)
{
    var allocated = GC.GetTotalAllocatedBytes(precise: true);
    var timer = Stopwatch.StartNew();
    var page = await read.ReadFeedAsync(cursor, null, 256, 4 * 1024 * 1024, CancellationToken.None).ConfigureAwait(false);
    timer.Stop();
    if (page.Expired || page.Events.Count != 0)
    {
        throw new InvalidOperationException("Matching prefix did not resume at its head.");
    }

    using var process = Process.GetCurrentProcess();
    Console.WriteLine(FormattableString.Invariant($"| {i} | {timer.Elapsed.TotalMilliseconds:F3} | {(GC.GetTotalAllocatedBytes(precise: true) - allocated) / 1048576.0:F3} | {process.PeakWorkingSet64 / 1048576.0:F3} |"));
}
