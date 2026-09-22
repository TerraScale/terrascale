using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TerraBase.Indexing;
using TerraBase.Indexing.Automatic;
using TerraBase.Indexing.Automatic.Jev;
using TerraBase.Indexing.Runtime;
using TerraBase.Storage.DatabaseBinding;
using DurableStorageEngine = global::TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>
/// Equal-budget first-query comparison for no secondary index, deterministic
/// query-independent indexing, Jev advice when explicitly approved, and
/// SQLite with no secondary index or one explicit matching index.
/// </summary>
internal static class QueryIndependentIndexWorkload
{
    private const string Collection = "users";
    private const string StatusPath = "status";
    private const string EvaluationStatus = "s17";
    private const string NestedSegmentPath = "profile.segment";
    private const string EvaluationSegment = "g23";
    private const int ResultLimit = 100;
    private const int MaximumIndexBudget = 2;
    private const int MaintenanceDocuments = 100;
    private const int TransactionBatchSize = 100;

    private static readonly EvaluationShape[] EvaluationShapes =
    [
        new("status", StatusPath, EvaluationStatus, static index => index % 100 == 17),
        new("nested profile.segment", NestedSegmentPath, EvaluationSegment, static index => index % 100 == 23),
    ];

    public static bool IsRequested(string[] args) =>
        args.Any(argument => argument.Equals("--workload=query-independent", StringComparison.OrdinalIgnoreCase))
        || args.Contains("query-independent", StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var documents = IntOption(args, "--documents", 2_000);
        var samples = IntOption(args, "--samples", 50);
        var jevRequested = args.Contains("--jev", StringComparer.OrdinalIgnoreCase);
        var jevApiKey = Environment.GetEnvironmentVariable("TERRABASE_JEV_API_KEY");
        var jevApproved = !string.IsNullOrWhiteSpace(jevApiKey);
        var outputDirectory = StringOption(args, "--output");
        var output = outputDirectory is null
            ? Path.Combine("bench", "query-independent-indexing-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md")
            : Path.Combine(outputDirectory, "query-independent-indexing.md");
        if (documents < 100 || samples < 10)
        {
            Console.Error.WriteLine("query-independent requires --documents >= 100 and --samples >= 10.");
            return 2;
        }

        var rows = new List<StrategyResult>();
        rows.AddRange(await RunStrategyAsync("No secondary index", documents, samples, null, useJev: false).ConfigureAwait(false));
        rows.AddRange(await RunStrategyAsync("Deterministic schema statistics", documents, samples, null, useJev: false).ConfigureAwait(false));

        var sqlite = await RunSqliteComparisonAsync(documents, samples).ConfigureAwait(false);
        if (jevRequested && jevApproved)
        {
            rows.AddRange(await RunStrategyAsync("Jev-assisted", documents, samples, jevApiKey, useJev: true).ConfigureAwait(false));
        }

        var markdown = Render(documents, samples, rows, sqlite, jevRequested, jevApproved);
        Console.Write(markdown);
        var fullOutput = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await File.WriteAllTextAsync(fullOutput, markdown).ConfigureAwait(false);
        Console.WriteLine($"Summary: {fullOutput}");
        return rows.Concat(sqlite.NoIndex).Concat(sqlite.ExplicitIndex).Any(row => !row.Correct) ? 1 : 0;
    }

    private static async Task<IReadOnlyList<StrategyResult>> RunStrategyAsync(
        string strategy, int documentCount, int samples, string? apiKey, bool useJev)
    {
        var root = Path.Combine(Path.GetTempPath(), "terrabase-bench-query-independent-" + Guid.NewGuid().ToString("N"));
        var tenant = new TenantId("benchmark-tenant");
        var collection = new CollectionId(Collection);
        var scope = new AutomaticIndexScope(tenant.Value, "benchmark-database", Collection);
        var descriptor = new DatabaseBindingDescriptor(
            new DatabaseBindingKey(tenant, new DatabaseRouteId("benchmark")), EntityId.New(), root,
            DatabaseStorageBackend.StorageEngine, DatabaseBindingMode.Explicit, Region.Gru, new HashSet<Region> { Region.Gru });
        try
        {
            await using var source = new DurableStorageEngine(root);
            var writeWatch = Stopwatch.StartNew();
            await SeedTerrabaseAsync(source, tenant, collection, documentCount).ConfigureAwait(false);

            writeWatch.Stop();
            var storageAfterWrites = DirectoryBytes(root);
            await using var runtime = await AutomaticIndexRuntime.OpenAsync(source, new AutomaticIndexRuntimeOptions { DisablePageCache = true }).ConfigureAwait(false);
            var storageBeforeIndex = DirectoryBytes(root);
            var binding = new DatabaseBinding(descriptor, new DatabaseScopedStorageProvider(source, tenant, ownsInner: false), root)
            {
                DurableEngine = source,
            };
            var sourceExecutor = new IndexRuntimeSourceQueryExecutor(binding, collection);
            var bounds = IndexRuntimeExecutionBounds.ForPage(ResultLimit, null, 100_000, 64UL * 1024 * 1024, 200_000);

            if (strategy == "No secondary index")
            {
                // This is the no-index evaluation lane. Its query history is
                // intentionally populated by the measurement itself.
                var coldMaintenance = await UpdateTerrabaseMaintenanceAsync(source, tenant, collection, root).ConfigureAwait(false);
                var coldRows = new List<StrategyResult>(EvaluationShapes.Length);
                foreach (var shape in EvaluationShapes)
                {
                    var measurement = await MeasureTerrabaseAsync(
                        runtime, scope, shape.Query(tenant, collection), sourceExecutor, bounds, samples,
                        ExpectedIds(documentCount, shape, includeMaintenance: true)).ConfigureAwait(false);
                    coldRows.Add(new StrategyResult(
                        shape.Name, strategy, "0 secondary", measurement, ActiveIndex: false, measurement.Correct,
                        QueryHistoryBeforeCandidates: null, InitialIngestMilliseconds: writeWatch.Elapsed.TotalMilliseconds,
                        ProfileMilliseconds: 0, BuildMilliseconds: 0, InferenceMilliseconds: 0, InferenceCostUsd: 0,
                        MaintenanceMilliseconds: coldMaintenance.Milliseconds, MaintenanceStorageDeltaBytes: coldMaintenance.StorageDeltaBytes,
                        StorageAfterWritesBytes: storageAfterWrites, StorageBeforeIndexBytes: storageBeforeIndex,
                        FinalStorageBytes: DirectoryBytes(root), Advice: "not-run", Access: "TerraBase source scan"));
                }

                return coldRows;
            }

            // Candidate generation must remain query-independent. The proof is
            // both a runtime observation count and the absence of an evaluation
            // query object until after advice and lifecycle application.
            EnsureEmptyQueryHistory(runtime, "before profiling and candidate generation");
            await using var snapshot = await source.BeginSnapshotAsync().ConfigureAwait(false);
            var profileWatch = Stopwatch.StartNew();
            var profile = await new CollectionIndexProfiler().ProfileAsync(source, snapshot, scope).ConfigureAwait(false);
            var candidates = await new SchemaStatisticsCandidateSource().GetCandidatesAsync(profile,
                new QueryIndependentIndexPolicy { MaximumIndexes = MaximumIndexBudget }).ConfigureAwait(false);
            EnsureEmptyQueryHistory(runtime, "after candidate generation");
            var queryHistoryBeforeCandidates = runtime.Analyzer.ObservationCount;
            profileWatch.Stop();

            IAutoIndexAdvisor advisor = new SchemaStatisticsAdvisor();
            JevAutoIndexAdvisor? jev = null;
            if (useJev && !string.IsNullOrWhiteSpace(apiKey))
            {
                var approvals = new JevTenantApprovalRegistry();
                approvals.Approve(scope, candidates.Candidates.ToImmutableDictionary(item => item.Field.Path,
                    _ => "A category explicitly intended for filtering.", StringComparer.Ordinal));
                jev = new JevAutoIndexAdvisor(new JevSystemOneClient(new HttpClient()), approvals,
                    new JevApiCredentials(() => apiKey), new JevAdvisorOptions());
                advisor = jev;
            }

            var adviceWatch = Stopwatch.StartNew();
            var advice = await advisor.AdviseAsync(candidates).ConfigureAwait(false);
            adviceWatch.Stop();
            var buildWatch = Stopwatch.StartNew();
            using var lifecycle = new QueryIndependentIndexLifecycleManager(runtime, scope);
            await lifecycle.ApplyAsync(candidates, advice).ConfigureAwait(false);
            buildWatch.Stop();
            jev?.Dispose();

            var maintenance = await UpdateTerrabaseMaintenanceAsync(source, tenant, collection, root).ConfigureAwait(false);
            var records = runtime.Coordinator.Snapshot(scope).Indexes;
            var active = records.Any(record => record.Phase == AutomaticIndexPhase.Active && record.Visible);
            var reportedInferenceCost = advice.Inference?.EstimatedInputCostUsd ?? 0;
            var mode = advice.Provider + ":" + advice.ReasonCode;
            var rows = new List<StrategyResult>(EvaluationShapes.Length);
            foreach (var shape in EvaluationShapes)
            {
                // The evaluation query is created and executed only after the
                // query-independent candidate and advisor phases have completed.
                var indexedMeasurement = await MeasureTerrabaseAsync(
                    runtime, scope, shape.Query(tenant, collection), sourceExecutor, bounds, samples,
                    ExpectedIds(documentCount, shape, includeMaintenance: true)).ConfigureAwait(false);
                rows.Add(new StrategyResult(
                    shape.Name, strategy, "2 secondary", indexedMeasurement, active, indexedMeasurement.Correct,
                    queryHistoryBeforeCandidates, writeWatch.Elapsed.TotalMilliseconds, profileWatch.Elapsed.TotalMilliseconds,
                    buildWatch.Elapsed.TotalMilliseconds, adviceWatch.Elapsed.TotalMilliseconds, reportedInferenceCost,
                    maintenance.Milliseconds, maintenance.StorageDeltaBytes, storageAfterWrites, storageBeforeIndex,
                    DirectoryBytes(root), mode,
                    AccessLabel(indexedMeasurement, "TerraBase automatic index", "TerraBase source scan (held-out predicate)")));
            }

            return rows;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<Measurement> MeasureTerrabaseAsync(
        AutomaticIndexRuntime runtime, AutomaticIndexScope scope, IndexRuntimeQuery query,
        IndexRuntimeSourceQueryExecutor sourceExecutor, IndexRuntimeExecutionBounds bounds, int samples,
        IReadOnlyList<string> expected)
    {
        var firstWatch = Stopwatch.StartNew();
        var first = await runtime.QueryBoundedAsync(scope, query, bounds, sourceExecutor.ExecuteAsync).ConfigureAwait(false);
        firstWatch.Stop();
        VerifyIds(first.Documents.Select(static document => document.Key.DocumentId), expected, "first TerraBase query");

        var latencies = new long[samples];
        ulong rows = 0;
        var repeatCount = 0;
        var repeatIndexServed = 0;
        for (var index = 0; index < samples; index++)
        {
            var watch = Stopwatch.StartNew();
            var result = await runtime.QueryBoundedAsync(scope, query, bounds, sourceExecutor.ExecuteAsync).ConfigureAwait(false);
            watch.Stop();
            VerifyIds(result.Documents.Select(static document => document.Key.DocumentId), expected, $"TerraBase repeat {index}");
            latencies[index] = watch.ElapsedTicks;
            rows += result.Execution.Measurement.RowsExamined;
            repeatCount = result.Documents.Count;
            if (result.Plan.IndexName.Length > 0)
            {
                repeatIndexServed++;
            }
        }

        Array.Sort(latencies);
        return new Measurement(
            Microseconds(firstWatch.ElapsedTicks), Microseconds(latencies[(int)Math.Floor((samples - 1) * 0.5)]),
            Microseconds(latencies[(int)Math.Floor((samples - 1) * 0.95)]), first.Execution.Measurement.RowsExamined,
            (double)rows / samples, first.Documents.Count, repeatCount, Correct: true,
            FirstIndexServed: first.Plan.IndexName.Length > 0,
            RepeatIndexServedFraction: (double)repeatIndexServed / samples);
    }

    private static async Task<SqliteComparison> RunSqliteComparisonAsync(int documentCount, int samples)
    {
        var root = Path.Combine(Path.GetTempPath(), "terrabase-bench-query-independent-sqlite-" + Guid.NewGuid().ToString("N"));
        try
        {
            var noIndex = await RunSqliteLaneAsync(
                Path.Combine(root, "no-index"), documentCount, samples, buildIndexes: false).ConfigureAwait(false);
            var explicitLane = await RunSqliteLaneAsync(
                Path.Combine(root, "explicit-index"), documentCount, samples, buildIndexes: true).ConfigureAwait(false);
            return new SqliteComparison(noIndex.Rows, explicitLane.Rows, noIndex.Plans, explicitLane.Plans);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<SqliteLane> RunSqliteLaneAsync(
        string root, int documentCount, int samples, bool buildIndexes)
    {
        var databasePath = Path.Combine(root, "comparison.sqlite");
        Directory.CreateDirectory(root);
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        await ExecuteSqliteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        await ExecuteSqliteAsync(connection, "CREATE TABLE users (id TEXT PRIMARY KEY, body BLOB NOT NULL);");

        var writeWatch = Stopwatch.StartNew();
        await SeedSqliteAsync(connection, documentCount).ConfigureAwait(false);
        writeWatch.Stop();
        var storageAfterWrites = DirectoryBytes(root);

        var buildWatch = Stopwatch.StartNew();
        if (buildIndexes)
        {
            await ExecuteSqliteAsync(connection, "CREATE INDEX ix_users_status ON users(json_extract(body, '$.status'));");
            await ExecuteSqliteAsync(connection, "CREATE INDEX ix_users_profile_segment ON users(json_extract(body, '$.profile.segment'));");
        }

        buildWatch.Stop();
        var maintenance = await UpdateSqliteMaintenanceAsync(connection, root).ConfigureAwait(false);
        var finalStorage = DirectoryBytes(root);
        var rows = new List<StrategyResult>(EvaluationShapes.Length);
        var plans = new string[EvaluationShapes.Length];
        for (var shapeIndex = 0; shapeIndex < EvaluationShapes.Length; shapeIndex++)
        {
            var shape = EvaluationShapes[shapeIndex];
            plans[shapeIndex] = await ExplainSqliteAsync(connection, shape).ConfigureAwait(false);
            if (buildIndexes && !plans[shapeIndex].Contains(shape.SqliteIndexName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SQLite did not use the explicit {shape.Name} index. EXPLAIN QUERY PLAN: {plans[shapeIndex]}");
            }

            var measurement = await MeasureSqliteAsync(connection, shape, samples,
                ExpectedIds(documentCount, shape, includeMaintenance: true), indexServed: buildIndexes).ConfigureAwait(false);
            rows.Add(new StrategyResult(
                shape.Name,
                buildIndexes ? "SQLite explicit JSONB indexes" : "SQLite no secondary index",
                buildIndexes ? "2 secondary" : "0 secondary",
                measurement,
                ActiveIndex: buildIndexes,
                measurement.Correct,
                QueryHistoryBeforeCandidates: null,
                InitialIngestMilliseconds: writeWatch.Elapsed.TotalMilliseconds,
                ProfileMilliseconds: 0,
                BuildMilliseconds: buildIndexes ? buildWatch.Elapsed.TotalMilliseconds : 0,
                InferenceMilliseconds: 0,
                InferenceCostUsd: 0,
                MaintenanceMilliseconds: maintenance.Milliseconds,
                MaintenanceStorageDeltaBytes: maintenance.StorageDeltaBytes,
                StorageAfterWritesBytes: storageAfterWrites,
                StorageBeforeIndexBytes: storageAfterWrites,
                FinalStorageBytes: finalStorage,
                Advice: "comparator",
                Access: buildIndexes
                    ? $"SQLite {shape.SqliteIndexName} (EXPLAIN QUERY PLAN)"
                    : "SQLite table scan (EXPLAIN QUERY PLAN)"));
        }

        return new SqliteLane(rows, plans);
    }

    private static async Task<Measurement> MeasureSqliteAsync(
        SqliteConnection connection, EvaluationShape shape, int samples,
        IReadOnlyList<string> expected, bool indexServed)
    {
        await using var query = new SqliteCommand(
            $"SELECT id FROM users WHERE json_extract(body, '$.{shape.Path}') = @value ORDER BY id LIMIT @limit;", connection);
        query.Parameters.AddWithValue("@value", shape.Value);
        query.Parameters.AddWithValue("@limit", ResultLimit);
        query.Prepare();

        var firstWatch = Stopwatch.StartNew();
        var first = await QuerySqliteIdsAsync(query).ConfigureAwait(false);
        firstWatch.Stop();
        VerifyIds(first, expected, "first SQLite query");

        var latencies = new long[samples];
        var repeatCount = 0;
        for (var index = 0; index < samples; index++)
        {
            var watch = Stopwatch.StartNew();
            var ids = await QuerySqliteIdsAsync(query).ConfigureAwait(false);
            watch.Stop();
            VerifyIds(ids, expected, $"SQLite repeat {index}");
            latencies[index] = watch.ElapsedTicks;
            repeatCount = ids.Count;
        }

        Array.Sort(latencies);
        return new Measurement(
            Microseconds(firstWatch.ElapsedTicks), Microseconds(latencies[(int)Math.Floor((samples - 1) * 0.5)]),
            Microseconds(latencies[(int)Math.Floor((samples - 1) * 0.95)]), double.NaN, double.NaN,
            first.Count, repeatCount, Correct: true, FirstIndexServed: indexServed,
            RepeatIndexServedFraction: indexServed ? 1 : 0);
    }

    private static async Task<List<string>> QuerySqliteIdsAsync(SqliteCommand query)
    {
        await using var reader = await query.ExecuteReaderAsync().ConfigureAwait(false);
        var ids = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<string> ExplainSqliteAsync(SqliteConnection connection, EvaluationShape shape)
    {
        await using var command = new SqliteCommand(
            $"EXPLAIN QUERY PLAN SELECT id FROM users WHERE json_extract(body, '$.{shape.Path}') = @value ORDER BY id LIMIT @limit;", connection);
        command.Parameters.AddWithValue("@value", shape.Value);
        command.Parameters.AddWithValue("@limit", ResultLimit);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var details = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            details.Add(reader.GetString(3));
        }

        return string.Join("; ", details);
    }

    private static async Task ExecuteSqliteAsync(SqliteConnection connection, string sql)
    {
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static void EnsureEmptyQueryHistory(AutomaticIndexRuntime runtime, string phase)
    {
        var observations = runtime.Analyzer.ObservationCount;
        if (observations != 0)
        {
            throw new InvalidOperationException(
                $"Query-independent benchmark guard failed {phase}: query history contains {observations} observation(s).");
        }
    }

    private static void VerifyIds(IEnumerable<string> actual, IReadOnlyList<string> expected, string phase)
    {
        var observed = actual.Order(StringComparer.Ordinal).ToArray();
        if (!observed.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{phase} returned {observed.Length} IDs instead of the expected {expected.Count} evaluation results.");
        }
    }

    private static async Task SeedTerrabaseAsync(
        DurableStorageEngine source, TenantId tenant, CollectionId collection, int documentCount)
    {
        for (var batchStart = 0; batchStart < documentCount; batchStart += TransactionBatchSize)
        {
            var count = Math.Min(TransactionBatchSize, documentCount - batchStart);
            var mutations = new StorageMutation[count];
            for (var index = 0; index < count; index++)
            {
                var documentIndex = batchStart + index;
                mutations[index] = StorageMutation.Upsert(
                    new DocumentKey(tenant, collection, $"doc-{documentIndex:D6}"),
                    Body(DocumentJson(documentIndex)), DocumentPrecondition.Missing);
            }

            var result = await source.ApplyBatchAsync(mutations).ConfigureAwait(false);
            if (result.Outcome != StorageBatchOutcome.Committed)
            {
                throw new InvalidOperationException($"TerraBase seed batch at {batchStart} did not commit: {result.Outcome}.");
            }
        }

        var maintenance = new StorageMutation[MaintenanceDocuments];
        for (var index = 0; index < MaintenanceDocuments; index++)
        {
            maintenance[index] = StorageMutation.Upsert(
                new DocumentKey(tenant, collection, $"doc-maint-{index:D6}"),
                Body(DocumentJson(index)), DocumentPrecondition.Missing);
        }

        var maintenanceResult = await source.ApplyBatchAsync(maintenance).ConfigureAwait(false);
        if (maintenanceResult.Outcome != StorageBatchOutcome.Committed)
        {
            throw new InvalidOperationException($"TerraBase maintenance seed batch did not commit: {maintenanceResult.Outcome}.");
        }
    }

    private static async Task SeedSqliteAsync(SqliteConnection connection, int documentCount)
    {
        for (var batchStart = 0; batchStart < documentCount; batchStart += TransactionBatchSize)
        {
            var count = Math.Min(TransactionBatchSize, documentCount - batchStart);
            await SeedSqliteBatchAsync(connection, batchStart, count, maintenance: false).ConfigureAwait(false);
        }

        await SeedSqliteBatchAsync(connection, 0, MaintenanceDocuments, maintenance: true).ConfigureAwait(false);
    }

    private static async Task SeedSqliteBatchAsync(
        SqliteConnection connection, int batchStart, int count, bool maintenance)
    {
        await ExecuteSqliteAsync(connection, "BEGIN;").ConfigureAwait(false);
        try
        {
            await using var insert = new SqliteCommand(
                "INSERT INTO users (id, body) VALUES (@id, jsonb(@body));", connection);
            var id = insert.Parameters.Add("@id", SqliteType.Text);
            var body = insert.Parameters.Add("@body", SqliteType.Text);
            for (var index = 0; index < count; index++)
            {
                var documentIndex = batchStart + index;
                id.Value = maintenance ? $"doc-maint-{documentIndex:D6}" : $"doc-{documentIndex:D6}";
                body.Value = DocumentJson(documentIndex);
                await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await ExecuteSqliteAsync(connection, "COMMIT;").ConfigureAwait(false);
        }
        catch
        {
            await ExecuteSqliteAsync(connection, "ROLLBACK;").ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<MaintenanceResult> UpdateTerrabaseMaintenanceAsync(
        DurableStorageEngine source, TenantId tenant, CollectionId collection, string root)
    {
        var before = DirectoryBytes(root);
        var watch = Stopwatch.StartNew();
        var mutations = new StorageMutation[MaintenanceDocuments];
        for (var index = 0; index < MaintenanceDocuments; index++)
        {
            mutations[index] = StorageMutation.Upsert(
                new DocumentKey(tenant, collection, $"doc-maint-{index:D6}"),
                Body(DocumentJson(index, valueOffset: 1)), DocumentPrecondition.Any);
        }

        var result = await source.ApplyBatchAsync(mutations).ConfigureAwait(false);
        if (result.Outcome != StorageBatchOutcome.Committed)
        {
            throw new InvalidOperationException($"TerraBase maintenance batch did not commit: {result.Outcome}.");
        }

        watch.Stop();
        return new MaintenanceResult(watch.Elapsed.TotalMilliseconds, DirectoryBytes(root) - before);
    }

    private static async Task<MaintenanceResult> UpdateSqliteMaintenanceAsync(SqliteConnection connection, string root)
    {
        var before = DirectoryBytes(root);
        var watch = Stopwatch.StartNew();
        await ExecuteSqliteAsync(connection, "BEGIN;");
        await using (var insert = new SqliteCommand(
            "INSERT INTO users (id, body) VALUES (@id, jsonb(@body)) ON CONFLICT(id) DO UPDATE SET body = excluded.body;", connection))
        {
            var id = insert.Parameters.Add("@id", SqliteType.Text);
            var body = insert.Parameters.Add("@body", SqliteType.Text);
            for (var index = 0; index < MaintenanceDocuments; index++)
            {
                id.Value = $"doc-maint-{index:D6}";
                body.Value = DocumentJson(index, valueOffset: 1);
                await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        await ExecuteSqliteAsync(connection, "COMMIT;");
        watch.Stop();
        return new MaintenanceResult(watch.Elapsed.TotalMilliseconds, DirectoryBytes(root) - before);
    }

    private static string[] ExpectedIds(int documentCount, EvaluationShape shape, bool includeMaintenance)
    {
        var expected = Enumerable.Range(0, documentCount)
            .Where(shape.Matches)
            .Select(index => $"doc-{index:D6}");
        if (includeMaintenance)
        {
            expected = expected.Concat(Enumerable.Range(0, MaintenanceDocuments)
                .Where(shape.Matches)
                .Select(index => $"doc-maint-{index:D6}"));
        }

        return expected.Order(StringComparer.Ordinal).ToArray();
    }

    private static string DocumentJson(int index, int valueOffset = 0) =>
        $"{{\"status\":\"s{index % 100:D2}\",\"profile\":{{\"segment\":\"g{index % 100:D2}\"}},\"value\":{(index + valueOffset) % 5}}}";

    private static string Render(
        int documents, int samples, IReadOnlyList<StrategyResult> rows, SqliteComparison sqlite,
        bool jevRequested, bool jevApproved)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Query-independent indexing comparison");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Generated: {DateTimeOffset.UtcNow:O}; documents: {documents} + {MaintenanceDocuments} maintenance-update documents; repeat samples: {samples}; evaluation queries: `{StatusPath} = {EvaluationStatus}` and `{NestedSegmentPath} = {EvaluationSegment}`; result limit: {ResultLimit}.");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Initial seed and maintenance updates use {TransactionBatchSize}-document transactions in both TerraBase and SQLite; the final seed batch may contain fewer documents when the requested document count is not divisible by the batch size.");
        builder.AppendLine("SQLite no-index and explicit-index lanes use separately seeded equivalent databases; each lane applies its own post-build maintenance batch from the same initial document state.");
        builder.AppendLine("The evaluation query is executed only after candidate selection, advisor decision, and lifecycle application in indexed TerraBase lanes.");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Equal secondary-index budget: TerraBase policy MaximumIndexes={MaximumIndexBudget} with no evaluation-field hint; SQLite explicit lane creates exactly two expression indexes (`ix_users_status`, `ix_users_profile_segment`) over JSONB paths. SQLite uses WAL with synchronous=FULL; its table primary key remains present in both lanes.");
        builder.AppendLine("SQLite stores the same document shape as TerraBase in a JSONB body and queries it with json_extract; only the relational primary key is separate. Candidate generation receives no query history or preferred paths.");
        builder.AppendLine();
        builder.AppendLine("| Shape | Strategy | Index budget | First query µs | Repeat p50 µs | Repeat p95 µs | First rows | Repeat rows | Access | First index served | Repeat index served | Result coverage | Query history before candidates | Initial ingest ms | Profile ms | Build ms | Inference ms | Maintenance ms | Maintenance storage Δ | Storage after writes | Storage before index | Final storage | Index storage Δ | Advice |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var row in rows.Concat(sqlite.NoIndex).Concat(sqlite.ExplicitIndex))
        {
            AppendRow(builder, row);
        }

        builder.AppendLine();
        for (var shapeIndex = 0; shapeIndex < EvaluationShapes.Length; shapeIndex++)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"SQLite {EvaluationShapes[shapeIndex].Name} no-index EXPLAIN QUERY PLAN: `{sqlite.NoIndexPlans[shapeIndex]}`");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"SQLite {EvaluationShapes[shapeIndex].Name} explicit-index EXPLAIN QUERY PLAN: `{sqlite.ExplicitPlans[shapeIndex]}`");
        }
        builder.AppendLine("Timing semantics: `First query µs` is the one first evaluation call in that lane, not a percentile or minimum. Repeat percentiles are computed from the following calls; the first call is excluded from repeat statistics.");
        builder.AppendLine("The deterministic and Jev lanes assert `AutomaticWorkloadAnalyzer.ObservationCount == 0` immediately before profiling/candidate generation and again after candidate generation. Storage values are summed logical file lengths from `FileInfo.Length` for each lane's database and lifecycle/index files; index storage Δ is final storage minus storage before index. They exclude allocated disk-block accounting, so sparse or preallocated WAL files can differ from physical disk usage and these values do not establish physical storage superiority.");
        builder.AppendLine("Initial ingest ms is measured before any secondary index exists. Maintenance ms and maintenance storage Δ measure the same 100 post-build updates in each lane; they are write-cost observations for this batch, not a sustained write-amplification estimate. Repeat samples are repeated measurements within one seeded process and do not constitute independent seeded trials; no cross-trial variance or confidence interval is claimed.");
        if (jevRequested && !jevApproved)
        {
            builder.AppendLine("Jev row omitted: `--jev` was requested without an approved `TERRABASE_JEV_API_KEY`; the deterministic row remains explicitly labeled as deterministic.");
        }
        else
        {
            builder.AppendLine("Jev is only a measured external strategy when `--jev` and an approved `TERRABASE_JEV_API_KEY` are supplied. Model probabilities/confidence are not treated as measured index benefit.");
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, StrategyResult row)
    {
        var history = row.QueryHistoryBeforeCandidates is { } count ? count.ToString(CultureInfo.InvariantCulture) : "—";
        var firstRows = Number(row.Measurement.FirstRowsExamined);
        var repeatRows = Number(row.Measurement.RepeatRowsExamined);
        var delta = row.FinalStorageBytes - row.StorageBeforeIndexBytes - row.MaintenanceStorageDeltaBytes;
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"| {row.Shape} | {row.Strategy} | {row.IndexBudget} | {row.Measurement.FirstQueryMicroseconds:F1} | {row.Measurement.RepeatP50Microseconds:F1} | {row.Measurement.RepeatP95Microseconds:F1} | {firstRows} | {repeatRows} | {row.Access} | {BoolText(row.Measurement.FirstIndexServed)} | {row.Measurement.RepeatIndexServedFraction.ToString("P0", CultureInfo.InvariantCulture)} | {row.Correct} | {history} | {row.InitialIngestMilliseconds:F1} | {row.ProfileMilliseconds:F1} | {row.BuildMilliseconds:F1} | {row.InferenceMilliseconds:F1} | {row.MaintenanceMilliseconds:F1} | {row.MaintenanceStorageDeltaBytes} | {row.StorageAfterWritesBytes} | {row.StorageBeforeIndexBytes} | {row.FinalStorageBytes} | {delta} | {row.Advice} |");
    }

    private static string AccessLabel(Measurement measurement, string indexed, string scan) =>
        measurement.FirstIndexServed || measurement.RepeatIndexServedFraction > 0 ? indexed : scan;

    private static string BoolText(bool value) => value ? "True" : "False";

    private static string Number(double value) => double.IsNaN(value) ? "—" : value.ToString("F1", CultureInfo.InvariantCulture);

    private static long DirectoryBytes(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
            : 0;

    private static int IntOption(string[] args, string name, int fallback)
    {
        var text = StringOption(args, name);
        return text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static string? StringOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[index][(name.Length + 1)..];
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length) return args[index + 1];
        }

        return null;
    }

    private static double Microseconds(long ticks) => ticks * 1_000_000d / Stopwatch.Frequency;

    private static JsonElement Body(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record SqliteComparison(
        IReadOnlyList<StrategyResult> NoIndex, IReadOnlyList<StrategyResult> ExplicitIndex,
        IReadOnlyList<string> NoIndexPlans, IReadOnlyList<string> ExplicitPlans);

    private sealed record SqliteLane(IReadOnlyList<StrategyResult> Rows, IReadOnlyList<string> Plans);

    private sealed record EvaluationShape(
        string Name, string Path, string Value, Func<int, bool> Matcher)
    {
        public string SqliteIndexName => Path == StatusPath ? "ix_users_status" : "ix_users_profile_segment";

        public bool Matches(int index) => Matcher(index);

        public IndexRuntimeQuery Query(TenantId tenant, CollectionId collection) =>
            new(tenant, collection, [IndexPredicate.Equal(Path, Value)]);
    }

    private readonly record struct MaintenanceResult(double Milliseconds, long StorageDeltaBytes);

    private sealed record Measurement(
        double FirstQueryMicroseconds, double RepeatP50Microseconds, double RepeatP95Microseconds,
        double FirstRowsExamined, double RepeatRowsExamined, int FirstResultCount, int RepeatResultCount, bool Correct,
        bool FirstIndexServed, double RepeatIndexServedFraction);

    private sealed record StrategyResult(
        string Shape, string Strategy, string IndexBudget, Measurement Measurement, bool ActiveIndex, bool Correct,
        int? QueryHistoryBeforeCandidates, double InitialIngestMilliseconds, double ProfileMilliseconds,
        double BuildMilliseconds, double InferenceMilliseconds, decimal InferenceCostUsd,
        double MaintenanceMilliseconds, long MaintenanceStorageDeltaBytes,
        long StorageAfterWritesBytes, long StorageBeforeIndexBytes, long FinalStorageBytes,
        string Advice, string Access);
}
