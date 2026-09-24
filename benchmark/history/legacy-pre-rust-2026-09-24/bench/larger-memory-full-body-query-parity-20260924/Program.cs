using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TerraBase;
using TerraBase.Indexing;
using TerraBase.Indexing.Runtime;
using TerraBase.StorageEngine;
using ZoneTree;

var smoke = args.Contains("--smoke", StringComparer.Ordinal);
var continuous = args.Contains("--continuous", StringComparer.Ordinal);
const string queryMaterialization = "full-json-body-and-version";
var rows = smoke ? 200 : 3_000;
var payloadBytes = smoke ? 1_024 : 98_304;
const int maintenanceRows = 100;
const int batchSize = 25;
const int repeatSamples = 5;
const int resultLimit = 100;
const int checkpointDocuments = 500;
const long targetCapBytes = 256L * 1024 * 1024;
const long targetGcCapBytes = 128L * 1024 * 1024;
var sourceDirectory = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(sourceDirectory, "FullBodyPhysicalQuery.csproj")))
    throw new InvalidOperationException("Run from bench/larger-memory-full-body-query-parity-20260924.");
var frozenReferenceDirectory = Path.GetFullPath(Path.Combine(sourceDirectory,
    "../larger-memory-physical-query-parity-20260924/run-20260924T054213Z"));
var frozenSourcesDirectory = Path.Combine(frozenReferenceDirectory, "frozen-provenance");
string frozenSubjectGitHead;
using (var frozenReceipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(frozenReferenceDirectory, "physical-query-raw.json"))))
    frozenSubjectGitHead = frozenReceipt.RootElement.GetProperty("subjectGitHead").GetString() ?? "unknown";
using (var frozenStart = JsonDocument.Parse(File.ReadAllText(Path.Combine(frozenReferenceDirectory, "run-start.json"))))
{
    foreach (var (property, assembly) in new[]
    {
        ("terraBaseAssemblySha256", typeof(DurableIndexStore).Assembly.Location),
        ("zoneTreeAssemblySha256", typeof(IZoneTreeMaintenance<,>).Assembly.Location),
    })
    {
        var expected = frozenStart.RootElement.GetProperty(property).GetString();
        if (expected != Sha256File(assembly))
            throw new InvalidOperationException($"Loaded {property} does not match frozen reference run.");
    }
}
var outputDirectory = Path.Combine(sourceDirectory, "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture));
Directory.CreateDirectory(outputDirectory);
var cgroup = CgroupSnapshot();
if (!smoke && (cgroup.MemoryMaxBytes is null or > targetCapBytes || cgroup.SwapMaxBytes != 0))
    throw new InvalidOperationException($"Qualified run requires a cgroup with memory.max <= {targetCapBytes} and memory.swap.max=0. Observed: {JsonSerializer.Serialize(cgroup)}");
if (!smoke && GC.GetGCMemoryInfo().TotalAvailableMemoryBytes > targetGcCapBytes)
    throw new InvalidOperationException($"Qualified run requires a GC heap hard limit <= {targetGcCapBytes} bytes; observed {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes} bytes.");

var fields = new[]
{
    new Field("status", "$.status", "s17", 17, "ix_users_status"),
    new Field("profile.segment", "$.profile.segment", "g23", 23, "ix_users_profile_segment"),
};
var sourceFiles = new[]
{
    Path.Combine(sourceDirectory, "Program.cs"),
    Path.Combine(sourceDirectory, "FullBodyPhysicalQuery.csproj"),
    Path.Combine(frozenSourcesDirectory, "source-00-Program.cs"),
    Path.Combine(frozenSourcesDirectory, "source-01-LargerMemoryPhysicalQuery.csproj"),
    Path.Combine(frozenSourcesDirectory, "source-02-Program.cs"),
    Path.Combine(frozenSourcesDirectory, "source-03-DurableIndexStore.cs"),
    Path.Combine(frozenSourcesDirectory, "source-04-DurableIndexBackend.cs"),
    Path.Combine(frozenSourcesDirectory, "source-05-MutableSegment.cs"),
    Path.Combine(frozenSourcesDirectory, "source-06-ZoneTree.ReadWrite.cs"),
    Path.Combine(frozenSourcesDirectory, "source-07-ZoneTree.Merge.cs"),
    Path.Combine(frozenSourcesDirectory, "source-08-ZoneTreeMaintainer.cs"),
};
var sources = sourceFiles.Select(path => new SourceHash(path, Sha256File(path))).ToArray();
await File.WriteAllTextAsync(Path.Combine(outputDirectory, "run-start.json"), JsonSerializer.Serialize(new
{
    SchemaVersion = 1,
    StartedAtUtc = DateTimeOffset.UtcNow,
    Invocation = Environment.CommandLine,
    Smoke = smoke,
    ContinuousIngestion = continuous,
    QueryMaterialization = queryMaterialization,
    FrozenReferenceRun = frozenReferenceDirectory,
    BaseDocuments = rows,
    MaintenanceDocuments = maintenanceRows,
    RawPayloadBytes = payloadBytes,
    TransactionBatchSize = batchSize,
    TerraBaseCheckpointDocuments = continuous ? 0 : checkpointDocuments,
    TargetMemoryCapBytes = targetCapBytes,
    TargetGcHeapCapBytes = targetGcCapBytes,
    ObservedGcAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
    DotnetGcHeapHardLimit = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"),
    Cgroup = cgroup,
    Sources = sources,
    RunnerAssemblySha256 = Sha256File(typeof(Program).Assembly.Location),
    TerraBaseAssemblySha256 = Sha256File(typeof(DurableIndexStore).Assembly.Location),
    ZoneTreeAssemblySha256 = Sha256File(typeof(IZoneTreeMaintenance<,>).Assembly.Location),
    SqliteAssemblySha256 = Sha256File(typeof(SqliteConnection).Assembly.Location),
}, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);
var receipts = new List<LaneReceipt>();
try
{
    foreach (var indexedField in fields)
    {
        receipts.Add(await RunSqlite(indexedField));
        receipts.Add(await RunTerraBase(indexedField));
    }
    var receipt = new RunReceipt(1, DateTimeOffset.UtcNow, Environment.CommandLine,
        GitHead(sourceDirectory), frozenSubjectGitHead,
        smoke, continuous, queryMaterialization, frozenReferenceDirectory, rows, maintenanceRows, payloadBytes,
        checked((long)(rows + maintenanceRows) * Convert.ToBase64String(new byte[payloadBytes]).Length),
        batchSize, continuous ? 0 : checkpointDocuments, repeatSamples, resultLimit, targetCapBytes, targetGcCapBytes,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"),
        cgroup, CgroupSnapshot(),
        RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), Environment.Version.ToString(),
        CpuModel(), typeof(DurableIndexStore).Assembly.GetName().Version?.ToString() ?? "unknown",
        typeof(SqliteConnection).Assembly.GetName().Version?.ToString() ?? "unknown",
        Sha256File(typeof(DurableIndexStore).Assembly.Location),
        Sha256File(typeof(IZoneTreeMaintenance<,>).Assembly.Location),
        Sha256File(typeof(SqliteConnection).Assembly.Location),
        sources, receipts);
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    var rawPath = Path.Combine(outputDirectory, "physical-query-raw.json");
    await File.WriteAllTextAsync(rawPath, JsonSerializer.Serialize(receipt, options) + Environment.NewLine);
    var reportPath = Path.Combine(outputDirectory, "physical-query.md");
    await File.WriteAllTextAsync(reportPath, Render(receipt, Sha256File(rawPath)));
    Console.WriteLine(reportPath);
    Console.WriteLine(rawPath);
}
catch (Exception exception)
{
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "failure.txt"), exception.ToString() + Environment.NewLine);
    throw;
}

async Task<LaneReceipt> RunSqlite(Field indexedField)
{
    var path = Path.Combine(outputDirectory, "data", "sqlite-" + indexedField.Name.Replace('.', '-'));
    Directory.CreateDirectory(path);
    var dbPath = Path.Combine(path, "users.sqlite");
    var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
    var phases = new List<Phase>();
    var queries = new List<QueryReceipt>();
    var settings = new Dictionary<string, string>();
    var baseTransactions = 0;
    var maintenanceInsertTransactions = 0;
    var maintenanceUpdateTransactions = 0;
    var maximumTransactionDocuments = 0;
    var sqliteVersion = "unknown";
    await using (var db = new SqliteConnection(connectionString))
    {
        await db.OpenAsync();
        await Configure(db);
        settings["journal_mode"] = await Scalar(db, "PRAGMA journal_mode;");
        settings["synchronous"] = await Scalar(db, "PRAGMA synchronous;");
        settings["cache_size"] = await Scalar(db, "PRAGMA cache_size;");
        settings["mmap_size"] = await Scalar(db, "PRAGMA mmap_size;");
        if (settings["journal_mode"] != "wal" || settings["synchronous"] != "2"
            || settings["cache_size"] != "-16384" || settings["mmap_size"] != "16777216")
            throw new InvalidOperationException("SQLite durability or cache settings differ: " + JsonSerializer.Serialize(settings));
        sqliteVersion = await Scalar(db, "SELECT sqlite_version();");
        await Execute(db, "CREATE TABLE users (id TEXT PRIMARY KEY, body BLOB NOT NULL, version INTEGER NOT NULL);");
        settings["body_storage"] = "SQLite JSONB";
        settings["query_materialization"] = "json(body) converted to text, parsed as JsonDocument, root cloned";
        settings["query_sql_template"] = "SELECT id, json(body), version FROM users WHERE json_extract(body, '{field.JsonPath}')=@value ORDER BY id LIMIT @limit;";
        settings["version_storage"] = "INTEGER column updated with body";
        phases.Add(await Measure("index-definition", path, async () =>
            await Execute(db, $"CREATE INDEX {indexedField.IndexName} ON users(json_extract(body, '{indexedField.JsonPath}'));")));
        var inputMilliseconds = 0.0;
        phases.Add(await Measure("seed-and-incremental-index-build", path, async () =>
        {
            for (var start = 0; start < rows; start += batchSize)
                await InsertBatch(start, Math.Min(batchSize, rows - start), false);
            for (var start = 0; start < maintenanceRows; start += batchSize)
                await InsertBatch(start, Math.Min(batchSize, maintenanceRows - start), true);
        }));
        phases.Add(new Phase("input-generation-subset-of-seed", inputMilliseconds, Snapshot(path), Snapshot(path), "JSON text and deterministic high-entropy payload generation; included inside seed wall time"));
        phases.Add(await Measure("post-build-maintenance", path, async () =>
        {
            for (var start = 0; start < maintenanceRows; start += batchSize)
                await UpdateBatch(start, Math.Min(batchSize, maintenanceRows - start));
        }));

        async Task InsertBatch(int start, int count, bool maintenance)
        {
            await Execute(db, "BEGIN;");
            try
            {
                await using var command = new SqliteCommand("INSERT INTO users (id,body,version) VALUES (@id,jsonb(@body),@version);", db);
                var id = command.Parameters.Add("@id", SqliteType.Text);
                var body = command.Parameters.Add("@body", SqliteType.Text);
                var version = command.Parameters.Add("@version", SqliteType.Integer);
                command.Prepare();
                for (var i = start; i < start + count; i++)
                {
                    id.Value = maintenance ? $"doc-maint-{i:D6}" : $"doc-{i:D6}";
                    var clock = Stopwatch.StartNew();
                    body.Value = DocumentJson(i);
                    version.Value = 1;
                    clock.Stop();
                    inputMilliseconds += clock.Elapsed.TotalMilliseconds;
                    await command.ExecuteNonQueryAsync();
                }
                await Execute(db, "COMMIT;");
                if (maintenance) maintenanceInsertTransactions++;
                else baseTransactions++;
                maximumTransactionDocuments = Math.Max(maximumTransactionDocuments, count);
            }
            catch { await Execute(db, "ROLLBACK;"); throw; }
        }

        async Task UpdateBatch(int start, int count)
        {
            await Execute(db, "BEGIN;");
            try
            {
                await using var command = new SqliteCommand("INSERT INTO users (id,body,version) VALUES (@id,jsonb(@body),@version) ON CONFLICT(id) DO UPDATE SET body=excluded.body,version=excluded.version;", db);
                var id = command.Parameters.Add("@id", SqliteType.Text);
                var body = command.Parameters.Add("@body", SqliteType.Text);
                var version = command.Parameters.Add("@version", SqliteType.Integer);
                command.Prepare();
                for (var i = start; i < start + count; i++)
                {
                    id.Value = $"doc-maint-{i:D6}";
                    var clock = Stopwatch.StartNew();
                    body.Value = DocumentJson(i, 1);
                    version.Value = 2;
                    clock.Stop();
                    inputMilliseconds += clock.Elapsed.TotalMilliseconds;
                    await command.ExecuteNonQueryAsync();
                }
                await Execute(db, "COMMIT;");
                maintenanceUpdateTransactions++;
                maximumTransactionDocuments = Math.Max(maximumTransactionDocuments, count);
            }
            catch { await Execute(db, "ROLLBACK;"); throw; }
        }
    }
    AssertBatchCounts("SQLite", baseTransactions, maintenanceInsertTransactions,
        maintenanceUpdateTransactions, maximumTransactionDocuments);
    RecordBatchCounts(settings, baseTransactions, maintenanceInsertTransactions,
        maintenanceUpdateTransactions, maximumTransactionDocuments);
    // Closing and reopening qualifies durable recovery. The OS page cache is
    // intentionally left alone; first-after-reopen is not called cold disk.
    await using (var db = new SqliteConnection(connectionString))
    {
        phases.Add(await Measure("durable-reopen", path, async () =>
        {
            await db.OpenAsync();
            await Configure(db);
        }));
        foreach (var field in fields)
        {
            var expected = ExpectedIds(field);
            var plan = await Explain(db, field);
            var indexed = field.Name == indexedField.Name;
            if (indexed != plan.Contains(indexedField.IndexName, StringComparison.Ordinal))
                throw new InvalidOperationException($"SQLite physical path mismatch for {field.Name}: {plan}");
            await using var command = new SqliteCommand($"SELECT id, json(body), version FROM users WHERE json_extract(body, '{field.JsonPath}')=@value ORDER BY id LIMIT @limit;", db);
            command.Parameters.AddWithValue("@value", field.Value);
            command.Parameters.AddWithValue("@limit", resultLimit);
            command.Prepare();
            var (first, firstRows, firstOutput) = await SqliteQuery(command);
            var firstIds = VerifyBodies(firstRows, expected, "SQLite " + field.Name + " first");
            var repeat = new double[repeatSamples];
            for (var i = 0; i < repeatSamples; i++)
            {
                var (elapsed, rows, _) = await SqliteQuery(command);
                VerifyBodies(rows, expected, "SQLite " + field.Name + " repeat");
                repeat[i] = elapsed;
            }
            queries.Add(new QueryReceipt(field.Name, indexed, plan, expected, Sha256Text(string.Join('\n', expected) + "\n"),
                firstIds, firstRows.Length, firstRows.Sum(row => (long)row.Body.GetProperty("payload").GetString()!.Length),
                first, repeat, firstOutput, Snapshot(path)));
        }
        var check = await Scalar(db, "PRAGMA integrity_check;");
        var count = await Scalar(db, "SELECT count(*) FROM users;");
        if (check != "ok" || count != (rows + maintenanceRows).ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException($"SQLite reopen verification failed: integrity={check}, count={count}");
        settings["reopen_integrity_check"] = check;
        settings["reopen_row_count"] = count;
    }
    var final = Snapshot(path);
    RequireLargerThanMemory(final, "SQLite " + indexedField.Name);
    Directory.Delete(path, recursive: true);
    return new LaneReceipt("SQLite JSONB", indexedField.Name, sqliteVersion, settings, phases, queries, [], final);
}

async Task<LaneReceipt> RunTerraBase(Field indexedField)
{
    var path = Path.Combine(outputDirectory, "data", "terrabase-" + indexedField.Name.Replace('.', '-'));
    Directory.CreateDirectory(path);
    var definition = IndexDefinition.Equality("by-" + indexedField.Name.Replace('.', '-'), indexedField.Name);
    var phases = new List<Phase>();
    var queries = new List<QueryReceipt>();
    var checkpoints = new List<CheckpointReceipt>();
    var committedBaseDocuments = 0;
    var committedMaintenanceSeedDocuments = 0;
    var committedMaintenanceUpdates = 0;
    var baseTransactions = 0;
    var maintenanceInsertTransactions = 0;
    var maintenanceUpdateTransactions = 0;
    var maximumTransactionDocuments = 0;
    long maximumObservedMutableWalBytes = 0;
    int maximumObservedReadOnlySegments = 0;
    var settings = new Dictionary<string, string>
    {
        ["index_definition"] = definition.Name,
        ["durability"] = "ZoneTree WriteAheadLogMode.Sync",
        ["query_materialization"] = "QueryAsync decodes full stored JSON body into cloned JsonElement",
        ["ingestion_mode"] = continuous ? "continuous-incremental-index-build"
            : $"durable-close-reopen-and-full-verify-every-{checkpointDocuments}-base-documents",
    };
    ulong sequence = 0;
    var inputMilliseconds = 0.0;
    var creationBefore = Snapshot(path);
    var creationWatch = Stopwatch.StartNew();
    var initialStore = new DurableIndexStore(path, definition);
    creationWatch.Stop();
    var store = initialStore;
    try
    {
        phases.Add(new Phase("index-definition", creationWatch.Elapsed.TotalMilliseconds, creationBefore, Snapshot(path),
            "Store constructor creates the empty physical index"));
        phases.Add(await Measure("seed-and-incremental-index-build", path, async () =>
        {
            for (var start = 0; start < rows; start += batchSize)
            {
                await Frame(start, Math.Min(batchSize, rows - start), false, 1);
                var complete = start + Math.Min(batchSize, rows - start);
                if (!continuous && complete % checkpointDocuments == 0)
                    await Checkpoint(complete, maintenanceInserted: false, maintenanceUpdated: false);
            }
            for (var start = 0; start < maintenanceRows; start += batchSize)
                await Frame(start, Math.Min(batchSize, maintenanceRows - start), true, 1);
            if (!continuous)
                await Checkpoint(rows, maintenanceInserted: true, maintenanceUpdated: false);
        }));
        phases.Add(new Phase("input-generation-and-json-parse-subset-of-seed", inputMilliseconds, Snapshot(path), Snapshot(path), "JSON text generation, parse and document/mutation construction; included inside seed wall time"));
        phases.Add(await Measure("post-build-maintenance", path, async () =>
        {
            for (var start = 0; start < maintenanceRows; start += batchSize)
                await Frame(start, Math.Min(batchSize, maintenanceRows - start), true, 2);
            if (!continuous)
                await Checkpoint(rows, maintenanceInserted: true, maintenanceUpdated: true);
        }));
        AssertBatchCounts("TerraBase", baseTransactions, maintenanceInsertTransactions,
            maintenanceUpdateTransactions, maximumTransactionDocuments);
        if (sequence != (ulong)(baseTransactions + maintenanceInsertTransactions + maintenanceUpdateTransactions))
            throw new InvalidOperationException("TerraBase frame sequence differs from committed frame count.");
        RecordBatchCounts(settings, baseTransactions, maintenanceInsertTransactions,
            maintenanceUpdateTransactions, maximumTransactionDocuments);

        async Task Frame(int start, int count, bool maintenance, ulong version)
        {
            var mutations = new IndexRuntimeMutation[count];
            var clock = Stopwatch.StartNew();
            for (var offset = 0; offset < count; offset++)
            {
                var i = start + offset;
                using var parsed = JsonDocument.Parse(DocumentJson(i, version == 2 ? 1 : 0));
                var key = new DocumentKey(new TenantId("benchmark-tenant"), new CollectionId("users"),
                    maintenance ? $"doc-maint-{i:D6}" : $"doc-{i:D6}");
                mutations[offset] = IndexRuntimeMutation.Upsert(new Document(key, parsed.RootElement, version, DateTimeOffset.UnixEpoch));
            }
            clock.Stop();
            inputMilliseconds += clock.Elapsed.TotalMilliseconds;
            await store.ApplyFrameAsync(new IndexRuntimeFrame(++sequence, mutations));
            if (maintenance)
            {
                if (version == 1)
                {
                    committedMaintenanceSeedDocuments += count;
                    maintenanceInsertTransactions++;
                }
                else
                {
                    committedMaintenanceUpdates += count;
                    maintenanceUpdateTransactions++;
                }
            }
            else
            {
                committedBaseDocuments += count;
                baseTransactions++;
            }
            maximumTransactionDocuments = Math.Max(maximumTransactionDocuments, count);
            var observedSegments = ObserveSegments(store);
            maximumObservedMutableWalBytes = Math.Max(maximumObservedMutableWalBytes, observedSegments.MutableWalBytes);
            maximumObservedReadOnlySegments = Math.Max(maximumObservedReadOnlySegments, observedSegments.ReadOnlySegmentCount);
            await WriteProgress();
        }

        async Task Checkpoint(int baseCount, bool maintenanceInserted, bool maintenanceUpdated)
        {
            var before = Snapshot(path);
            var reopen = Stopwatch.StartNew();
            await store.DisposeAsync();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            store = new DurableIndexStore(path, definition);
            reopen.Stop();
            if (store.Watermark.CommitSequence != sequence)
                throw new InvalidOperationException($"Checkpoint watermark {store.Watermark.CommitSequence} != {sequence}");
            var verified = await VerifyAll(store, baseCount, maintenanceInserted, maintenanceUpdated);
            checkpoints.Add(new CheckpointReceipt("ingest-checkpoint", baseCount, maintenanceInserted, maintenanceUpdated, sequence,
                reopen.Elapsed.TotalMilliseconds, verified.Milliseconds, verified.Count,
                verified.Digest, before, Snapshot(path)));
            await WriteProgress();
        }

        async Task WriteProgress()
        {
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "progress.json"), JsonSerializer.Serialize(new
            {
                AtUtc = DateTimeOffset.UtcNow,
                Engine = "TerraBase DurableIndexStore",
                IndexField = indexedField.Name,
                ContinuousIngestion = continuous,
                CommitSequence = sequence,
                CommittedBaseDocuments = committedBaseDocuments,
                CommittedMaintenanceSeedDocuments = committedMaintenanceSeedDocuments,
                CommittedMaintenanceUpdates = committedMaintenanceUpdates,
                MaximumSampledMutableWalBytes = maximumObservedMutableWalBytes,
                MaximumSampledReadOnlySegments = maximumObservedReadOnlySegments,
                CurrentSnapshot = Snapshot(path),
                Cgroup = CgroupSnapshot(),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);
        }
    }
    finally { await store.DisposeAsync(); }
    var reopenedBefore = Snapshot(path);
    var reopenedWatch = Stopwatch.StartNew();
    var reopenedStore = new DurableIndexStore(path, definition);
    reopenedWatch.Stop();
    await using (var reopenedIndex = reopenedStore)
    {
        phases.Add(new Phase("durable-reopen", reopenedWatch.Elapsed.TotalMilliseconds, reopenedBefore, Snapshot(path),
            "Previous store was disposed; timing covers opening the same physical index, including recovery and point-map reconstruction"));
        if (reopenedIndex.Watermark.CommitSequence != sequence)
            throw new InvalidOperationException($"TerraBase durable watermark {reopenedIndex.Watermark.CommitSequence} != {sequence}");
        settings["reopen_watermark"] = sequence.ToString(CultureInfo.InvariantCulture);
        foreach (var field in fields)
        {
            var expected = ExpectedIds(field);
            var query = new IndexRuntimeQuery(new TenantId("benchmark-tenant"), new CollectionId("users"),
                [IndexPredicate.Equal(field.Name, field.Value)], requiredCommitSequence: sequence, resultLimit: resultLimit);
            var indexed = field.Name == indexedField.Name;
            var (first, firstRows, firstOutput, access) = await TerraBaseQuery(reopenedIndex, query);
            var firstIds = VerifyBodies(firstRows, expected, "TerraBase " + field.Name + " first");
            if (indexed != (access != IndexAccessMethod.Scan))
                throw new InvalidOperationException($"TerraBase physical path mismatch for {field.Name}: {access}");
            var repeat = new double[repeatSamples];
            for (var i = 0; i < repeatSamples; i++)
            {
                var (elapsed, rows, _, repeatedAccess) = await TerraBaseQuery(reopenedIndex, query);
                VerifyBodies(rows, expected, "TerraBase " + field.Name + " repeat");
                if (access != repeatedAccess) throw new InvalidOperationException("Access method changed during repeats.");
                repeat[i] = elapsed;
            }
            queries.Add(new QueryReceipt(field.Name, indexed, access.ToString(), expected,
                Sha256Text(string.Join('\n', expected) + "\n"), firstIds, firstRows.Length,
                firstRows.Sum(row => (long)row.Body.GetProperty("payload").GetString()!.Length),
                first, repeat, firstOutput, Snapshot(path)));
        }
        if (continuous)
        {
            var before = Snapshot(path);
            var verified = await VerifyAll(reopenedIndex, rows, maintenanceInserted: true, maintenanceUpdated: true);
            checkpoints.Add(new CheckpointReceipt("post-reopen-full-verification", rows, true, true, sequence,
                reopenedWatch.Elapsed.TotalMilliseconds, verified.Milliseconds, verified.Count,
                verified.Digest, before, Snapshot(path)));
        }
    }
    settings["maximum_sampled_mutable_wal_bytes"] = maximumObservedMutableWalBytes.ToString(CultureInfo.InvariantCulture);
    settings["maximum_sampled_read_only_segments"] = maximumObservedReadOnlySegments.ToString(CultureInfo.InvariantCulture);
    settings["committed_base_documents"] = committedBaseDocuments.ToString(CultureInfo.InvariantCulture);
    settings["committed_maintenance_seed_documents"] = committedMaintenanceSeedDocuments.ToString(CultureInfo.InvariantCulture);
    settings["committed_maintenance_updates"] = committedMaintenanceUpdates.ToString(CultureInfo.InvariantCulture);
    var final = Snapshot(path);
    RequireLargerThanMemory(final, "TerraBase " + indexedField.Name);
    Directory.Delete(path, recursive: true);
    return new LaneReceipt("TerraBase DurableIndexStore", indexedField.Name, "source project", settings, phases, queries, checkpoints, final);

    async Task<(int Count, string Digest, double Milliseconds)> VerifyAll(
        DurableIndexStore current, int baseCount, bool maintenanceInserted, bool maintenanceUpdated)
    {
        var verify = Stopwatch.StartNew();
        if (current.Watermark.CommitSequence != sequence)
            throw new InvalidOperationException($"Verification watermark {current.Watermark.CommitSequence} != {sequence}");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var modulo = 0; modulo < 100; modulo++)
        {
            var value = indexedField.Name == "status" ? $"s{modulo:D2}" : $"g{modulo:D2}";
            var query = new IndexRuntimeQuery(new TenantId("benchmark-tenant"), new CollectionId("users"),
                [IndexPredicate.Equal(indexedField.Name, value)], requiredCommitSequence: sequence, resultLimit: 1000);
            var result = await current.QueryAsync(query);
            if (result.Plan.Access == IndexAccessMethod.Scan)
                throw new InvalidOperationException("Full verification lost its physical index seek.");
            foreach (var document in result.Documents)
            {
                var id = document.Key.DocumentId;
                if (!seen.Add(id)) throw new InvalidOperationException($"Duplicate verified document {id}");
                var maintenance = id.StartsWith("doc-maint-", StringComparison.Ordinal);
                if (maintenance && !maintenanceInserted)
                    throw new InvalidOperationException($"Unseeded maintenance document {id}");
                var i = int.Parse(id.AsSpan(maintenance ? "doc-maint-".Length : "doc-".Length), CultureInfo.InvariantCulture);
                if (i >= (maintenance ? maintenanceRows : baseCount))
                    throw new InvalidOperationException($"Full verification contained unexpected ID {id}");
                var body = document.Body;
                var expectedPayload = Convert.ToBase64String(PayloadBytes(i));
                var expectedOffset = maintenance && maintenanceUpdated ? 1 : 0;
                if (body.GetProperty("status").GetString() != $"s{i % 100:D2}"
                    || body.GetProperty("profile").GetProperty("segment").GetString() != $"g{i % 100:D2}"
                    || body.GetProperty("value").GetInt32() != (i + expectedOffset) % 5
                    || body.GetProperty("payload").GetString() != expectedPayload
                    || document.Version != (maintenance && maintenanceUpdated ? 2UL : 1UL))
                    throw new InvalidOperationException($"Full verification body/version mismatch for {id}");
            }
        }
        var expected = Enumerable.Range(0, baseCount).Select(i => $"doc-{i:D6}")
            .Concat(maintenanceInserted ? Enumerable.Range(0, maintenanceRows).Select(i => $"doc-maint-{i:D6}") : [])
            .Order(StringComparer.Ordinal).ToArray();
        Verify(seen.Order(StringComparer.Ordinal).ToArray(), expected, "full verification all-documents");
        verify.Stop();
        return (seen.Count, Sha256Text(string.Join('\n', expected) + "\n"), verify.Elapsed.TotalMilliseconds);
    }
}

async Task<(double Microseconds, MaterializedBody[] Rows, double OutputSerializationMicroseconds)> SqliteQuery(SqliteCommand command)
{
    var watch = Stopwatch.StartNew();
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<MaterializedBody>();
    while (await reader.ReadAsync())
    {
        var id = reader.GetString(0);
        using var json = JsonDocument.Parse(reader.GetString(1));
        rows.Add(new MaterializedBody(id, json.RootElement.Clone(), checked((ulong)reader.GetInt64(2))));
    }
    var materialized = rows.ToArray();
    var ids = materialized.Select(row => row.Id).ToArray();
    watch.Stop();
    var outputWatch = Stopwatch.StartNew();
    _ = JsonSerializer.Serialize(ids);
    outputWatch.Stop();
    return (watch.Elapsed.TotalMicroseconds, materialized, outputWatch.Elapsed.TotalMicroseconds);
}

async Task<(double Microseconds, MaterializedBody[] Rows, double OutputSerializationMicroseconds, IndexAccessMethod Access)> TerraBaseQuery(
    DurableIndexStore store, IndexRuntimeQuery query)
{
    var watch = Stopwatch.StartNew();
    var result = await store.QueryAsync(query);
    var rows = result.Documents.Select(item => new MaterializedBody(item.Key.DocumentId, item.Body, item.Version)).ToArray();
    var ids = rows.Select(row => row.Id).ToArray();
    watch.Stop();
    var outputWatch = Stopwatch.StartNew();
    _ = JsonSerializer.Serialize(ids);
    outputWatch.Stop();
    return (watch.Elapsed.TotalMicroseconds, rows, outputWatch.Elapsed.TotalMicroseconds, result.Plan.Access);
}

string DocumentJson(int i, int valueOffset = 0)
{
    var bytes = PayloadBytes(i);
    return $"{{\"status\":\"s{i % 100:D2}\",\"profile\":{{\"segment\":\"g{i % 100:D2}\"}},\"value\":{(i + valueOffset) % 5},\"payload\":\"{Convert.ToBase64String(bytes)}\"}}";
}

byte[] PayloadBytes(int i)
{
    var bytes = new byte[payloadBytes];
    new Random(unchecked(1_337 + i * 7919)).NextBytes(bytes);
    return bytes;
}

string[] ExpectedIds(Field field) => Enumerable.Range(0, rows).Where(i => i % 100 == field.Modulo)
    .Select(i => $"doc-{i:D6}")
    .Concat(Enumerable.Range(0, maintenanceRows).Where(i => i % 100 == field.Modulo).Select(i => $"doc-maint-{i:D6}"))
    .Order(StringComparer.Ordinal).ToArray();

void RequireLargerThanMemory(SnapshotData snapshot, string lane)
{
    if (!smoke && snapshot.FileLengthBytes <= targetCapBytes)
        throw new InvalidOperationException($"{lane} files total {snapshot.FileLengthBytes} bytes, below memory cap {targetCapBytes}; not a qualified larger-than-memory run.");
}

static void Verify(string[] actual, string[] expected, string phase)
{
    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        throw new InvalidOperationException($"{phase}: exact ordered IDs differ. Expected {string.Join(',', expected)}; actual {string.Join(',', actual)}");
}

string[] VerifyBodies(MaterializedBody[] materializedRows, string[] expected, string phase)
{
    var ids = materializedRows.Select(row => row.Id).ToArray();
    Verify(ids, expected, phase);
    foreach (var row in materializedRows)
    {
        var maintenance = row.Id.StartsWith("doc-maint-", StringComparison.Ordinal);
        var prefixLength = maintenance ? "doc-maint-".Length : "doc-".Length;
        var i = int.Parse(row.Id.AsSpan(prefixLength), CultureInfo.InvariantCulture);
        if (i < 0 || i >= (maintenance ? maintenanceRows : rows))
            throw new InvalidOperationException($"{phase}: unexpected document ID {row.Id}");
        var body = row.Body;
        var expectedPayload = Convert.ToBase64String(PayloadBytes(i));
        var expectedVersion = maintenance ? 2UL : 1UL;
        if (body.ValueKind != JsonValueKind.Object
            || body.GetProperty("status").GetString() != $"s{i % 100:D2}"
            || body.GetProperty("profile").GetProperty("segment").GetString() != $"g{i % 100:D2}"
            || body.GetProperty("value").GetInt32() != (i + (maintenance ? 1 : 0)) % 5
            || body.GetProperty("payload").GetString() != expectedPayload
            || row.Version != expectedVersion)
            throw new InvalidOperationException($"{phase}: body or version mismatch for {row.Id}");
    }
    return ids;
}

void AssertBatchCounts(string engine, int baseTransactions, int maintenanceInsertTransactions,
    int maintenanceUpdateTransactions, int maximumTransactionDocuments)
{
    var expectedBase = (rows + batchSize - 1) / batchSize;
    var expectedMaintenance = (maintenanceRows + batchSize - 1) / batchSize;
    if (baseTransactions != expectedBase
        || maintenanceInsertTransactions != expectedMaintenance
        || maintenanceUpdateTransactions != expectedMaintenance
        || maximumTransactionDocuments != batchSize)
        throw new InvalidOperationException($"{engine} transaction shape differs from expected "
            + $"{expectedBase}/{expectedMaintenance}/{expectedMaintenance} batches with maximum {batchSize} documents: "
            + $"observed {baseTransactions}/{maintenanceInsertTransactions}/{maintenanceUpdateTransactions}, maximum {maximumTransactionDocuments}.");
}

void RecordBatchCounts(Dictionary<string, string> settings, int baseTransactions,
    int maintenanceInsertTransactions, int maintenanceUpdateTransactions, int maximumTransactionDocuments)
{
    settings["base_transaction_count"] = baseTransactions.ToString(CultureInfo.InvariantCulture);
    settings["maintenance_insert_transaction_count"] = maintenanceInsertTransactions.ToString(CultureInfo.InvariantCulture);
    settings["maintenance_update_transaction_count"] = maintenanceUpdateTransactions.ToString(CultureInfo.InvariantCulture);
    settings["maximum_transaction_documents"] = maximumTransactionDocuments.ToString(CultureInfo.InvariantCulture);
}

static async Task Configure(SqliteConnection db)
{
    await Execute(db, "PRAGMA journal_mode=WAL;");
    await Execute(db, "PRAGMA synchronous=FULL;");
    await Execute(db, "PRAGMA temp_store=MEMORY;");
    await Execute(db, "PRAGMA cache_size=-16384;");
    await Execute(db, "PRAGMA mmap_size=16777216;");
}
static async Task Execute(SqliteConnection db, string sql)
{
    await using var command = new SqliteCommand(sql, db);
    await command.ExecuteNonQueryAsync();
}
static async Task<string> Scalar(SqliteConnection db, string sql)
{
    await using var command = new SqliteCommand(sql, db);
    return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
}
static async Task<string> Explain(SqliteConnection db, Field field)
{
    await using var command = new SqliteCommand($"EXPLAIN QUERY PLAN SELECT id, json(body), version FROM users WHERE json_extract(body, '{field.JsonPath}')=@value ORDER BY id LIMIT 100;", db);
    command.Parameters.AddWithValue("@value", field.Value);
    await using var reader = await command.ExecuteReaderAsync();
    var plans = new List<string>();
    while (await reader.ReadAsync()) plans.Add(reader.GetString(3));
    return string.Join("; ", plans);
}
static async Task<Phase> Measure(string name, string path, Func<Task> operation)
{
    var before = Snapshot(path);
    var watch = Stopwatch.StartNew();
    await operation();
    watch.Stop();
    return new Phase(name, watch.Elapsed.TotalMilliseconds, before, Snapshot(path), "");
}
static SnapshotData Snapshot(string path)
{
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    var io = File.ReadLines("/proc/self/io").Select(line => line.Split(':', 2)).ToDictionary(parts => parts[0], parts => long.Parse(parts[1].Trim(), CultureInfo.InvariantCulture));
    var cgroup = CgroupSnapshot();
    return new SnapshotData(DateTimeOffset.UtcNow, Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0,
        process.WorkingSet64, process.PeakWorkingSet64, io.GetValueOrDefault("read_bytes"), io.GetValueOrDefault("write_bytes"),
        cgroup.MemoryCurrentBytes, cgroup.MemoryPeakBytes);
}
static CgroupData CgroupSnapshot()
{
    var relative = File.ReadAllLines("/proc/self/cgroup").FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal))?[3..].TrimStart('/');
    var root = Path.Combine("/sys/fs/cgroup", relative ?? "");
    return new CgroupData(root, ReadNumber("memory.max"), ReadNumber("memory.swap.max"), ReadNumber("memory.current"), ReadNumber("memory.peak"),
        File.Exists(Path.Combine(root, "memory.events")) ? File.ReadAllText(Path.Combine(root, "memory.events")) : "unavailable");
    long? ReadNumber(string name)
    {
        var file = Path.Combine(root, name);
        return File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
static string CpuModel() => File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "unknown";
static (long MutableWalBytes, int ReadOnlySegmentCount) ObserveSegments(DurableIndexStore store)
{
    // This benchmark samples the live ZoneTree after each committed frame.
    // The private field is intentionally used only for diagnostic receipts;
    // on-disk 0.json can lag live rotation and WAL directory totals include
    // read-only segments.
    var field = typeof(DurableIndexStore).GetField("zoneTree", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(DurableIndexStore), "zoneTree");
    var tree = field.GetValue(store) as ITransactionalZoneTree<Memory<byte>, Memory<byte>>
        ?? throw new InvalidOperationException("DurableIndexStore ZoneTree diagnostic field has an unexpected type.");
    var maintenance = tree.Maintenance.ZoneTree.Maintenance;
    var mutableSegmentId = maintenance.MutableSegment.SegmentId;
    var mutableWalPath = Path.Combine(store.BackendPath, "seg", $"{mutableSegmentId}.wal.0");
    var mutableWalBytes = File.Exists(mutableWalPath) ? new FileInfo(mutableWalPath).Length : 0;
    return (mutableWalBytes, maintenance.ReadOnlySegmentsCount);
}
static string GitHead(string directory)
{
    using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true } };
    process.StartInfo.ArgumentList.Add("rev-parse");
    process.StartInfo.ArgumentList.Add("HEAD");
    process.Start();
    var output = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return process.ExitCode == 0 ? output : "unknown";
}
static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
static string Render(RunReceipt receipt, string rawHash)
{
    var report = new StringBuilder("# Larger-than-memory full-body physical-query qualification\n\n");
    report.AppendLine($"Generated {receipt.GeneratedAtUtc:O}; raw JSON SHA-256 `{rawHash}`. Mode: {(receipt.Smoke ? "uncapped smoke, unqualified" : "cgroup-qualified")}; query materialization: {receipt.QueryMaterialization}; frozen TerraBase/ZoneTree reference: `{receipt.FrozenReferenceRun}`; TerraBase ingestion: {(receipt.ContinuousIngestion ? "continuous" : "periodic durable close/reopen")}. Dataset: {receipt.BaseDocuments}+{receipt.MaintenanceDocuments} documents; deterministic 98,304-byte raw payload per document in qualification mode; estimated encoded payload {receipt.EstimatedEncodedPayloadBytes:N0} bytes. Transaction batch={receipt.BatchSize}. Cgroup memory.max={receipt.CgroupAtStart.MemoryMaxBytes?.ToString() ?? "unlimited"}, memory.swap.max={receipt.CgroupAtStart.SwapMaxBytes?.ToString() ?? "unlimited"}; GC available memory={receipt.ObservedGcAvailableMemoryBytes}; final cgroup memory.peak={receipt.CgroupAtFinish.MemoryPeakBytes?.ToString() ?? "unavailable"}.");
    report.AppendLine();
    report.AppendLine("| Engine | One physical index | Predicate | Access | IDs / bodies | First after reopen µs | Repeat p50 µs | Repeat p95 µs | Final file bytes | RSS after queries | Process read bytes | Process write bytes |");
    report.AppendLine("| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
    foreach (var lane in receipt.Lanes)
        foreach (var query in lane.Queries)
            report.AppendLine($"| {lane.Engine} | {lane.IndexField} | {query.Shape} | {query.Access} | {query.ExpectedIds.Length} / {query.MaterializedBodies} | {query.FirstMicroseconds:F1} | {Percentile(query.RepeatMicroseconds, .5):F1} | {Percentile(query.RepeatMicroseconds, .95):F1} | {lane.Final.FileLengthBytes} | {query.After.WorkingSetBytes} | {query.After.ProcessReadBytes} | {query.After.ProcessWriteBytes} |");
    report.AppendLine();
    report.AppendLine("Each lane also records index definition, incremental build/seed, input generation, post-build maintenance and reopen phases with wall time, file bytes, RSS and /proc/self/io. Every lane asserts 120 base, four maintenance insert and four maintenance update transactions of at most 25 documents in qualification mode. Every timed query materializes all 31 complete bodies and versions; exact IDs, body fields, payloads and versions are verified after timing. The raw receipt contains materialized-body and payload-character counts, actual transaction counts, exact ordered IDs, plans/access methods, repeat samples, output ID serialization, source hashes and cgroup observations. SQLite reopen verification includes integrity_check and row count; TerraBase verifies the durable watermark and all committed documents after final reopen.");
    report.AppendLine("The TerraBase lane settings include the maximum mutable WAL file size and read-only segment count sampled from the live ZoneTree after committed frames. These samples do not establish a strict peak between frames.");
    if (!receipt.ContinuousIngestion)
        report.AppendLine("TerraBase additionally closes/reopens every 500 base documents and after maintenance; each checkpoint verifies every committed ID, body field, payload and version through the selected physical index. These reopens, GC collections and full checks are extra ingestion work.");
    report.AppendLine("The first-after-reopen query follows connection/store initialization; OS page cache is not evicted, so it is not a cold-disk sample. A repeated query is a same-connection/store warm sample, although a dataset larger than the cgroup cap prevents the whole database from residing in its page cache. SQLite converts stored JSONB to JSON text with json(body), parses it into JsonDocument and clones the root; TerraBase QueryAsync decodes and clones its stored JSON document. Both return full bodies, but these remain different materialization and physical-storage paths. Separate output ID JSON serialization is reported, and body verification is outside query timing. These query values are not pure engine execution. The direct TerraBase DurableIndexStore does not include the authoritative source store, advisor, planner, API, transport or replication; SQLite JSONB remains a separate physical database. No cross-engine speed superiority is claimed.");
    report.AppendLine("Both lanes predeclare exactly one index on the same field and test it alongside the other field's scan. Index fields are selected with known held-out predicates, so this is physical-index qualification rather than query-independent advisor evaluation. SQLite uses WAL/FULL; TerraBase uses its normal durable index WAL. The two engines do not share an enforceable index-admission or cache budget. Logical file lengths are not allocated disk blocks or write amplification; process I/O counters include activity outside a single database operation. These resource differences limit comparative interpretation even inside the same cgroup cap. No cross-engine superiority is claimed.");
    return report.ToString();
}
static double Percentile(double[] values, double fraction) => values.Order().ElementAt((int)Math.Ceiling(values.Length * fraction) - 1);

record Field(string Name, string JsonPath, string Value, int Modulo, string IndexName);
record MaterializedBody(string Id, JsonElement Body, ulong Version);
record SourceHash(string Path, string Sha256);
record CgroupData(string Path, long? MemoryMaxBytes, long? SwapMaxBytes, long? MemoryCurrentBytes, long? MemoryPeakBytes, string MemoryEvents);
record SnapshotData(DateTimeOffset AtUtc, long FileLengthBytes, long WorkingSetBytes, long PeakWorkingSetBytes,
    long ProcessReadBytes, long ProcessWriteBytes, long? CgroupMemoryCurrentBytes, long? CgroupMemoryPeakBytes);
record Phase(string Name, double Milliseconds, SnapshotData Before, SnapshotData After, string Notes);
record QueryReceipt(string Shape, bool Indexed, string Access, string[] ExpectedIds, string ExpectedIdsSha256,
    string[] FirstIds, int MaterializedBodies, long MaterializedPayloadCharacters,
    double FirstMicroseconds, double[] RepeatMicroseconds, double OutputSerializationMicroseconds, SnapshotData After);
record CheckpointReceipt(string Kind, int BaseDocuments, bool MaintenanceInserted, bool MaintenanceUpdated, ulong Watermark,
    double ReopenMilliseconds, double VerificationMilliseconds, int VerifiedDocuments, string IdsSha256,
    SnapshotData Before, SnapshotData After);
record LaneReceipt(string Engine, string IndexField, string Version, Dictionary<string, string> Settings,
    List<Phase> Phases, List<QueryReceipt> Queries, List<CheckpointReceipt> Checkpoints, SnapshotData Final);
record RunReceipt(int SchemaVersion, DateTimeOffset GeneratedAtUtc, string Invocation,
    string BenchmarkGitHead, string SubjectGitHead, bool Smoke, bool ContinuousIngestion,
    string QueryMaterialization, string FrozenReferenceRun,
    int BaseDocuments, int MaintenanceDocuments,
    int RawPayloadBytes, long EstimatedEncodedPayloadBytes, int BatchSize, int CheckpointDocuments, int RepeatSamples, int ResultLimit,
    long TargetMemoryCapBytes, long TargetGcHeapCapBytes, long ObservedGcAvailableMemoryBytes,
    string? DotnetGcHeapHardLimit, CgroupData CgroupAtStart, CgroupData CgroupAtFinish,
    string OperatingSystem, string Architecture, string DotnetRuntime,
    string CpuModel, string TerraBaseAssemblyVersion, string MicrosoftDataSqliteAssemblyVersion,
    string TerraBaseAssemblySha256, string ZoneTreeAssemblySha256, string MicrosoftDataSqliteAssemblySha256,
    SourceHash[] Sources, List<LaneReceipt> Lanes);
