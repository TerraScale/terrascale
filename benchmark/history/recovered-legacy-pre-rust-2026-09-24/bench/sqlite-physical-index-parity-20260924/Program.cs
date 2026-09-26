using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

const int documents = 2_000;
const int maintenanceSeed = 100;
const int batchSize = 100;
const int samples = 30;
const int resultLimit = 100;
const int cacheKiB = 65_536;
const long mmapBytes = 67_108_864;
var shapes = new (string Name, string Path, string Value, string IndexName, int Modulo)[]
{
    ("status", "$.status", "s17", "ix_users_status", 17),
    ("nested profile.segment", "$.profile.segment", "g23", "ix_users_profile_segment", 23),
};
var sourceDirectory = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(sourceDirectory, "SqlitePhysicalIndexParity.csproj")))
{
    throw new InvalidOperationException("Run from bench/sqlite-physical-index-parity-20260924 so source hashes are unambiguous.");
}

var outputDirectory = Path.Combine(sourceDirectory, "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture));
Directory.CreateDirectory(outputDirectory);
var sourcePaths = new[]
{
    Path.Combine(sourceDirectory, "Program.cs"),
    Path.Combine(sourceDirectory, "SqlitePhysicalIndexParity.csproj"),
    Path.GetFullPath(Path.Combine(sourceDirectory, "../../terrabase/TerraBase.Benchmarks/QueryIndependentIndexWorkload.cs")),
    Path.GetFullPath(Path.Combine(sourceDirectory, "../query-independent-heldout-20260924T0358Z-trial3/query-independent-indexing-receipt.json")),
};
var sourceHashes = sourcePaths.Select(path => new SourceHash(path, Sha256File(path))).ToArray();
var results = new List<LaneResult>();
for (var trial = 1; trial <= 3; trial++)
{
    foreach (var indexed in new[] { false, true })
    {
        results.Add(await RunLane(trial, indexed));
    }
}

var sqliteVersion = results[0].SqliteVersion;
var receipt = new Receipt(
    1, DateTimeOffset.UtcNow, Environment.CommandLine,
    "SQLite embedded engine only; prepared JSONB expression queries; no advisor, inference, API, network, or replication timing",
    new Workload(documents, maintenanceSeed, batchSize, samples, resultLimit,
        new[] { "status=s17", "profile.segment=g23" }, 2),
    new ResourceSettings("WAL", "FULL", "MEMORY", cacheKiB, mmapBytes,
        "SQLite cache/mmap pragmas are soft settings; the held-out TerraBase 8 MiB physical index admission cap is not enforced on SQLite.",
        Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        GetCpuAllowedList(), GetCgroupFiles()),
    new RuntimeInfo(RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.Version.ToString(), DotnetSdkVersion(), CpuModel(), InstalledMemoryKiB(),
        sqliteVersion, typeof(SqliteConnection).Assembly.GetName().Version?.ToString() ?? "unknown",
        GetGitHead(sourceDirectory), GetGitDirty(sourceDirectory)),
    sourceHashes, results);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var rawPath = Path.Combine(outputDirectory, "sqlite-physical-index-parity-raw.json");
await File.WriteAllTextAsync(rawPath, JsonSerializer.Serialize(receipt, jsonOptions) + Environment.NewLine);
var summaryPath = Path.Combine(outputDirectory, "sqlite-physical-index-parity.md");
await File.WriteAllTextAsync(summaryPath, RenderSummary(receipt, Path.GetFileName(rawPath), Sha256File(rawPath)));
Console.WriteLine(summaryPath);
Console.WriteLine(rawPath);

async Task<LaneResult> RunLane(int trial, bool indexed)
{
    var directory = Path.Combine(outputDirectory, $"db-trial-{trial}-{(indexed ? "indexed" : "scan")}");
    Directory.CreateDirectory(directory);
    var dbPath = Path.Combine(directory, "users.sqlite");
    var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await Execute("PRAGMA journal_mode=WAL;");
    await Execute("PRAGMA synchronous=FULL;");
    await Execute("PRAGMA temp_store=MEMORY;");
    await Execute($"PRAGMA cache_size=-{cacheKiB};");
    await Execute($"PRAGMA mmap_size={mmapBytes};");
    var observedSettings = new Dictionary<string, string>
    {
        ["journal_mode"] = await Scalar("PRAGMA journal_mode;"),
        ["synchronous"] = await Scalar("PRAGMA synchronous;"),
        ["temp_store"] = await Scalar("PRAGMA temp_store;"),
        ["cache_size"] = await Scalar("PRAGMA cache_size;"),
        ["mmap_size"] = await Scalar("PRAGMA mmap_size;"),
    };
    if (observedSettings["journal_mode"] != "wal" || observedSettings["synchronous"] != "2"
        || observedSettings["temp_store"] != "2" || observedSettings["cache_size"] != $"-{cacheKiB}"
        || observedSettings["mmap_size"] != mmapBytes.ToString(CultureInfo.InvariantCulture))
    {
        throw new InvalidOperationException($"SQLite settings differ from workload: {JsonSerializer.Serialize(observedSettings)}");
    }

    await Execute("CREATE TABLE users (id TEXT PRIMARY KEY, body BLOB NOT NULL);");
    var ingestion = Stopwatch.StartNew();
    for (var start = 0; start < documents; start += batchSize)
    {
        await SeedBatch(start, Math.Min(batchSize, documents - start), false);
    }
    await SeedBatch(0, maintenanceSeed, true);
    ingestion.Stop();
    var bytesAfterSeed = DirectoryBytes(directory);

    var build = Stopwatch.StartNew();
    if (indexed)
    {
        foreach (var shape in shapes)
        {
            await Execute($"CREATE INDEX {shape.IndexName} ON users(json_extract(body, '{shape.Path}'));");
        }
    }
    build.Stop();
    var bytesAfterBuild = DirectoryBytes(directory);

    var maintenance = Stopwatch.StartNew();
    await Execute("BEGIN;");
    try
    {
        await using var update = new SqliteCommand("INSERT INTO users (id, body) VALUES (@id, jsonb(@body)) ON CONFLICT(id) DO UPDATE SET body = excluded.body;", connection);
        var id = update.Parameters.Add("@id", SqliteType.Text);
        var body = update.Parameters.Add("@body", SqliteType.Text);
        update.Prepare();
        for (var i = 0; i < maintenanceSeed; i++)
        {
            id.Value = $"doc-maint-{i:D6}";
            body.Value = DocumentJson(i, 1);
            await update.ExecuteNonQueryAsync();
        }
        await Execute("COMMIT;");
    }
    catch
    {
        await Execute("ROLLBACK;");
        throw;
    }
    maintenance.Stop();
    var bytesAfterMaintenance = DirectoryBytes(directory);

    var queryResults = new List<QueryResult>();
    foreach (var shape in shapes)
    {
        var expected = Enumerable.Range(0, documents).Where(i => i % 100 == shape.Modulo).Select(i => $"doc-{i:D6}")
            .Concat(Enumerable.Range(0, maintenanceSeed).Where(i => i % 100 == shape.Modulo).Select(i => $"doc-maint-{i:D6}"))
            .Order(StringComparer.Ordinal).ToArray();
        var plan = await Plan(shape.Path, shape.Value);
        if (indexed && !plan.Contains(shape.IndexName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"SQLite failed to select {shape.IndexName}: {plan}");
        }
        if (!indexed && plan.Contains("ix_users_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"No-index lane selected a secondary index: {plan}");
        }
        // The JSON path is literal to match the expression index. Value and
        // result limit remain bound parameters, as in the held-out workload.
        await using var literalQuery = new SqliteCommand(
            $"SELECT id FROM users WHERE json_extract(body, '{shape.Path}') = @value ORDER BY id LIMIT @limit;", connection);
        literalQuery.Parameters.AddWithValue("@value", shape.Value);
        literalQuery.Parameters.AddWithValue("@limit", resultLimit);
        literalQuery.Prepare();
        var firstWatch = Stopwatch.StartNew();
        var firstIds = await QueryIds(literalQuery);
        firstWatch.Stop();
        Verify(firstIds, expected, trial, indexed, shape.Name, "first");
        var repeat = new double[samples];
        for (var sample = 0; sample < samples; sample++)
        {
            var watch = Stopwatch.StartNew();
            var ids = await QueryIds(literalQuery);
            watch.Stop();
            Verify(ids, expected, trial, indexed, shape.Name, $"repeat {sample}");
            repeat[sample] = 1_000_000.0 * watch.ElapsedTicks / Stopwatch.Frequency;
        }
        queryResults.Add(new QueryResult(shape.Name, shape.Path, shape.Value, shape.IndexName, plan,
            expected, Sha256Text(string.Join('\n', expected) + "\n"), firstIds,
            1_000_000.0 * firstWatch.ElapsedTicks / Stopwatch.Frequency,
            repeat, Percentile(repeat, 0.5), Percentile(repeat, 0.95)));
    }
    var sqliteVersion = await Scalar("SELECT sqlite_version();");
    await connection.CloseAsync();
    // File sizes include the WAL and SHM while the connection is open; the
    // database files are removed only after all measured sizes are captured.
    Directory.Delete(directory, recursive: true);
    return new LaneResult(trial, indexed ? "two explicit physical indexes" : "no secondary index", sqliteVersion,
        observedSettings, ingestion.Elapsed.TotalMilliseconds, build.Elapsed.TotalMilliseconds,
        maintenance.Elapsed.TotalMilliseconds, bytesAfterSeed, bytesAfterBuild, bytesAfterMaintenance, queryResults);

    async Task Execute(string statement)
    {
        await using var command = new SqliteCommand(statement, connection);
        await command.ExecuteNonQueryAsync();
    }

    async Task<string> Scalar(string statement)
    {
        await using var command = new SqliteCommand(statement, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
    }

    async Task SeedBatch(int start, int count, bool isMaintenance)
    {
        await Execute("BEGIN;");
        try
        {
            await using var insert = new SqliteCommand("INSERT INTO users (id, body) VALUES (@id, jsonb(@body));", connection);
            var id = insert.Parameters.Add("@id", SqliteType.Text);
            var body = insert.Parameters.Add("@body", SqliteType.Text);
            insert.Prepare();
            for (var i = start; i < start + count; i++)
            {
                id.Value = isMaintenance ? $"doc-maint-{i:D6}" : $"doc-{i:D6}";
                body.Value = DocumentJson(i);
                await insert.ExecuteNonQueryAsync();
            }
            await Execute("COMMIT;");
        }
        catch
        {
            await Execute("ROLLBACK;");
            throw;
        }
    }

    async Task<string> Plan(string path, string value)
    {
        await using var command = new SqliteCommand(
            $"EXPLAIN QUERY PLAN SELECT id FROM users WHERE json_extract(body, '{path}') = @value ORDER BY id LIMIT @limit;", connection);
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@limit", resultLimit);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(3));
        return string.Join("; ", lines);
    }
}

static async Task<string[]> QueryIds(SqliteCommand command)
{
    await using var reader = await command.ExecuteReaderAsync();
    var ids = new List<string>();
    while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
    return ids.ToArray();
}

static void Verify(string[] actual, string[] expected, int trial, bool indexed, string shape, string phase)
{
    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        throw new InvalidOperationException($"Trial {trial} indexed={indexed} {shape} {phase}: result ID mismatch. Expected {string.Join(',', expected)}, actual {string.Join(',', actual)}");
}

static string DocumentJson(int i, int valueOffset = 0) =>
    $"{{\"status\":\"s{i % 100:D2}\",\"profile\":{{\"segment\":\"g{i % 100:D2}\"}},\"value\":{(i + valueOffset) % 5}}}";

static long DirectoryBytes(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
static double Percentile(double[] values, double fraction) => values.Order().ElementAt((int)Math.Ceiling(values.Length * fraction) - 1);
static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
static string GetCpuAllowedList() => File.ReadLines("/proc/self/status").FirstOrDefault(line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "unknown";
static Dictionary<string, string> GetCgroupFiles()
{
    var paths = new[] { "/sys/fs/cgroup/cpu.max", "/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory.swap.max",
        "/sys/fs/cgroup/cpu/cpu.cfs_quota_us", "/sys/fs/cgroup/cpu/cpu.cfs_period_us", "/sys/fs/cgroup/memory/memory.limit_in_bytes" };
    return paths.Where(File.Exists).ToDictionary(path => path, path => File.ReadAllText(path).Trim());
}
static string GetGitHead(string path) => Git(path, "rev-parse", "HEAD");
static bool GetGitDirty(string path) => Git(path, "status", "--porcelain").Length > 0;
static string CpuModel() => File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "unknown";
static string InstalledMemoryKiB() => File.ReadLines("/proc/meminfo").FirstOrDefault(line => line.StartsWith("MemTotal:", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "unknown";
static string DotnetSdkVersion()
{
    using var process = new Process { StartInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true } };
    process.StartInfo.ArgumentList.Add("--version");
    process.Start();
    var output = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return process.ExitCode == 0 ? output : "unknown";
}
static string Git(string path, params string[] arguments)
{
    using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardOutput = true } };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var output = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return process.ExitCode == 0 ? output : "unknown";
}

static string RenderSummary(Receipt receipt, string rawName, string rawHash)
{
    var builder = new StringBuilder();
    builder.AppendLine("# Independent SQLite physical-index parity baseline");
    builder.AppendLine();
    builder.AppendLine($"Generated {receipt.GeneratedAtUtc:O}. Raw receipt: `{rawName}` (SHA-256 `{rawHash}`).");
    builder.AppendLine("The 2,000 held-out base documents plus 100 maintenance documents, two JSON paths, 100-row transaction batches, JSONB body, WAL/FULL durability, prepared query, and ID order match the query-independent workload. Each first call follows EXPLAIN and Prepare; the nested path follows status repeats. Inference and advisor work are absent from these engine timings.");
    builder.AppendLine();
    builder.AppendLine("| Trial | Lane | Shape | Result IDs | First µs | Repeat p50 µs | Repeat p95 µs | Seed ms | Index build ms | Maintenance ms | File bytes after seed | File bytes after build | File bytes after maintenance |");
    builder.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
    foreach (var lane in receipt.Lanes)
        foreach (var result in lane.Queries)
            builder.AppendLine($"| {lane.Trial} | {lane.Mode} | {result.Shape} | {result.ExpectedIds.Length} | {result.FirstMicroseconds:F1} | {result.RepeatP50Microseconds:F1} | {result.RepeatP95Microseconds:F1} | {lane.IngestMilliseconds:F1} | {lane.BuildMilliseconds:F1} | {lane.MaintenanceMilliseconds:F1} | {lane.BytesAfterSeed} | {lane.BytesAfterBuild} | {lane.BytesAfterMaintenance} |");
    builder.AppendLine();
    builder.AppendLine("All first and repeat ID sequences were checked against independently generated expected IDs. The raw receipt retains every ID, all repeat samples, observed PRAGMAs, query plans, source hashes, and runtime/resource settings.");
    builder.AppendLine("SQLite's explicit fields are selected with knowledge of the held-out predicates; it is a tuned physical-index oracle, not a query-independent advisor peer. SQLite cache and mmap settings are soft limits, not the TerraBase 8 MiB physical-index admission cap; SQLite has no equivalent admission contract here. A two-engine speed ratio is therefore not claimed by this independent SQLite-only run. Existing TerraBase receipts also include planner and runtime work in their first calls, while these SQLite first calls follow Prepare. OS page cache was not evicted; first means first execution of that predicate in a freshly seeded connection, not a cold-disk query.");
    builder.AppendLine("Logical file lengths include SQLite database/WAL/SHM at each sampled phase and are not allocated disk blocks or full write amplification. The final length is sampled after maintenance and before query execution. Three fresh seeded trials assess local noise; no confidence interval, crash recovery, sustained writes, or larger-than-memory claim follows from them.");
    return builder.ToString();
}

record SourceHash(string Path, string Sha256);
record Workload(int Documents, int MaintenanceSeedDocuments, int TransactionBatchSize, int RepeatSamples,
    int ResultLimit, string[] Predicates, int MaximumSecondaryIndexes);
record ResourceSettings(string JournalMode, string Synchronous, string TempStore, int CacheSizeKiB, long MmapSizeBytes,
    string BudgetDisclosure, int EffectiveProcessorCount, long GcAvailableMemoryBytes, string CpuAllowedList,
    Dictionary<string, string> CgroupFiles);
record RuntimeInfo(string OperatingSystem, string Architecture, string DotnetRuntime, string DotnetSdk,
    string CpuModel, string InstalledMemoryKiB, string SqliteVersion,
    string MicrosoftDataSqliteAssemblyVersion, string BenchmarkGitHead, bool BenchmarkWorktreeDirty);
record QueryResult(string Shape, string JsonPath, string PredicateValue, string IndexName, string ExplainQueryPlan,
    string[] ExpectedIds, string ExpectedIdsSha256, string[] FirstIds, double FirstMicroseconds,
    double[] RepeatMicroseconds, double RepeatP50Microseconds, double RepeatP95Microseconds);
record LaneResult(int Trial, string Mode, string SqliteVersion, Dictionary<string, string> ObservedSettings,
    double IngestMilliseconds, double BuildMilliseconds, double MaintenanceMilliseconds,
    long BytesAfterSeed, long BytesAfterBuild, long BytesAfterMaintenance, List<QueryResult> Queries);
record Receipt(int SchemaVersion, DateTimeOffset GeneratedAtUtc, string Invocation, string MeasurementBoundary,
    Workload Workload, ResourceSettings Resources, RuntimeInfo Runtime, SourceHash[] Sources, List<LaneResult> Lanes);
