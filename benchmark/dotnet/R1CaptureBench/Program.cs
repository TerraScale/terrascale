// Real engine writes and backup capture, excluding HTTP, startup, fixture creation and output formatting.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

var label = args.Length > 0 ? args[0] : "capture";
var commits = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1000;
var batch = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 8;
ArgumentOutOfRangeException.ThrowIfLessThan(commits, 1);
ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
ArgumentOutOfRangeException.ThrowIfGreaterThan(batch, 1024);
var directory = Directory.CreateTempSubdirectory("r1-capture-").FullName;
try
{
    Console.Error.WriteLine($"host={Environment.MachineName} runtime={Environment.Version} label={label}");
    Console.Error.WriteLine(File.ReadLines("/proc/self/status").Single(line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal)));
    var group = File.ReadLines("/proc/self/cgroup").Single(line => line.StartsWith("0::", StringComparison.Ordinal))[3..];
    foreach (var name in new[] { "cpu.max", "memory.max", "memory.swap.max" })
    {
        var path = "/sys/fs/cgroup" + group + "/" + name;
        Console.Error.WriteLine($"{name}={(File.Exists(path) ? File.ReadAllText(path).Trim() : "unavailable")}");
    }

    var options = new TerraBaseOptions { DataDirectory = directory, MemoryBudget = 128 * 1024 * 1024, StorageThreads = 2, StorageQueue = 1024 };
    await using var provider = new ServiceCollection().AddTerraBaseEngine(options).BuildServiceProvider();
    var engine = provider.GetRequiredService<IStorageEngine>();
    var tenant = new TenantContext(Ulid.NewUlid().ToString(), PlanTier.Hobby, Ulid.NewUlid().ToString(), Ulid.NewUlid().ToString());
    if (!await engine.CreateDatabaseAsync(tenant, CancellationToken.None).ConfigureAwait(false))
    {
        throw new InvalidOperationException("The benchmark database was not created.");
    }

    var store = await engine.OpenDatabaseAsync(tenant, CancellationToken.None).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The benchmark database did not open.");
    await using (var setup = await store.BeginTransactionAsync(write: true, CancellationToken.None).ConfigureAwait(false))
    {
        await setup.CreateCollectionAsync(new CollectionInfo("items", 0, Ulid.NewUlid().ToString()), CancellationToken.None).ConfigureAwait(false);
        _ = await setup.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    await using var tap = await provider.GetRequiredService<IWalBackup>().OpenTapAsync(tenant, CancellationToken.None).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The real backup tap did not open.");
    var body = Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', 1012) + "\"}");
    var ids = Enumerable.Range(0, checked((commits + 20) * batch)).Select(_ => Ulid.NewUlid().ToString()).ToArray();
    await WriteAsync(0, 20).ConfigureAwait(false);
    _ = await DrainAsync().ConfigureAwait(false);
    using var process = Process.GetCurrentProcess();
    var allocations = GC.GetTotalAllocatedBytes(precise: true);
    var cpu = process.TotalProcessorTime;
    var started = Stopwatch.GetTimestamp();
    await WriteAsync(20, commits).ConfigureAwait(false);
    var writeSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
    var writeCpu = (process.TotalProcessorTime - cpu).TotalSeconds;
    var writeAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocations;
    allocations = GC.GetTotalAllocatedBytes(precise: true);
    cpu = process.TotalProcessorTime;
    started = Stopwatch.GetTimestamp();
    var bytes = await DrainAsync().ConfigureAwait(false);
    var tapSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
    var tapCpu = (process.TotalProcessorTime - cpu).TotalSeconds;
    var tapAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocations;
    var empty = await tap.ReadAsync(Stream.Null, CancellationToken.None).ConfigureAwait(false);
    if (!empty.Continuous || empty.Bytes != 0 || empty.Remaining || bytes <= 0)
    {
        throw new InvalidOperationException("The drain did not consume a continuous nonempty backlog exactly once.");
    }

    var documents = checked(commits * batch);
    var frontier = await MeasureFrontierAsync().ConfigureAwait(false);
    var result = new CaptureResult(label, commits, documents, body.Length, bytes, writeSeconds, tapSeconds,
        commits / writeSeconds, documents / writeSeconds, documents / (writeSeconds + tapSeconds),
        writeAllocated / (double)documents, tapAllocated / (double)documents,
        writeCpu * 1000 / documents, tapCpu * 1000 / documents,
        frontier.Documents, frontier.Seconds, frontier.Documents / frontier.Seconds, frontier.AllocatedPerDocument, frontier.CpuMillisecondsPerDocument);
    Console.WriteLine(JsonSerializer.Serialize(result, CaptureJsonContext.Default.CaptureResult));

    async Task WriteAsync(int offset, int count)
    {
        for (var i = offset; i < offset + count; i++)
        {
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None).ConfigureAwait(false);
            for (var j = 0; j < batch; j++)
            {
                _ = await transaction.PutAsync("items", ids[i * batch + j], body, CancellationToken.None).ConfigureAwait(false);
            }

            _ = await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    async Task<long> DrainAsync()
    {
        long total = 0;
        WalRead read;
        do
        {
            read = await tap.ReadAsync(Stream.Null, CancellationToken.None).ConfigureAwait(false);
            if (!read.Continuous || (read.Remaining && read.Bytes == 0))
            {
                throw new InvalidOperationException("The backup tap lost continuity or made no progress.");
            }

            total += read.Bytes;
        }
        while (read.Remaining);
        return total;
    }

    async Task<FrontierResult> MeasureFrontierAsync()
    {
        const int count = 512;
        var database = new TenantContext(Ulid.NewUlid().ToString(), PlanTier.Hobby, Ulid.NewUlid().ToString(), Ulid.NewUlid().ToString());
        _ = await engine.CreateDatabaseAsync(database, CancellationToken.None).ConfigureAwait(false);
        var opened = await engine.OpenDatabaseAsync(database, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The frontier fixture did not open.");
        var json = Encoding.UTF8.GetBytes("{" + string.Join(',', Enumerable.Range(0, 1000).Select(index =>
            "\"" + new string('x', 64) + index.ToString(CultureInfo.InvariantCulture) + "\":" + index.ToString(CultureInfo.InvariantCulture))) + "}");
        await using (var setup = await opened.BeginTransactionAsync(write: true, CancellationToken.None).ConfigureAwait(false))
        {
            _ = await setup.CreateCollectionAsync(new CollectionInfo("items", 0, Ulid.NewUlid().ToString()), CancellationToken.None).ConfigureAwait(false);
            _ = await setup.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        for (var index = 0; index < count; index++)
        {
            await using var transaction = await opened.BeginTransactionAsync(write: true, CancellationToken.None).ConfigureAwait(false);
            _ = await transaction.PutAsync("items", Ulid.NewUlid().ToString(), json, CancellationToken.None).ConfigureAwait(false);
            _ = await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var source = provider.GetRequiredService<IChangeGroupSource>();
        var cursor = await source.AckedAsync(database, new ReplicaId(12345), CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The source cursor was refused.");
        var head = await source.HeadAsync(database, cursor.Incarnation, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The source head was refused.");
        IReadOnlySet<ReplicaId> held = new HashSet<ReplicaId>();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var start = Stopwatch.GetTimestamp();
        var proven = await source.ProveAsync(database, cursor.Incarnation, 0, head, held, CancellationToken.None).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (!proven || head <= 0)
        {
            throw new InvalidOperationException("The frontier proof did not cover the fixture.");
        }

        var file = Path.Combine(directory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        check.Open();
        using var command = check.CreateCommand();
        command.CommandText = "SELECT end_offset, (SELECT seq FROM _tb_feed_sequence WHERE id = 1) FROM _tb_shape_chunk WHERE handle = '~frontier' AND offset = 0;";
        using var rows = command.ExecuteReader();
        return rows.Read() && rows.GetInt64(0) > 0 && rows.GetInt64(0) == rows.GetInt64(1)
            ? new(count, elapsed, allocated / (double)count, cpuSeconds * 1000 / count)
            : throw new InvalidOperationException("The remote frontier did not reach the fixture's committed feed head.");
    }
}
finally
{
    Directory.Delete(directory, recursive: true);
}

internal sealed record CaptureResult(string Label, int Commits, int Documents, int DocumentBytes, long LogBytes,
    double WriteSeconds, double TapSeconds, double CommitsPerSecond, double DocumentsPerSecond,
    double DocumentsPerSecondIncludingDrain, double WriteAllocatedBytesPerDocument, double TapAllocatedBytesPerDocument,
    double WriteCpuMillisecondsPerDocument, double TapCpuMillisecondsPerDocument,
    int FrontierDocuments, double FrontierSeconds, double FrontierDocumentsPerSecond,
    double FrontierAllocatedBytesPerDocument, double FrontierCpuMillisecondsPerDocument);

internal sealed record FrontierResult(int Documents, double Seconds, double AllocatedPerDocument, double CpuMillisecondsPerDocument);

[JsonSerializable(typeof(CaptureResult))]
internal sealed partial class CaptureJsonContext : JsonSerializerContext;
