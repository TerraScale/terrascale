using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TerraBase;
using TerraBase.Indexing;
using TerraBase.Indexing.Automatic;
using TerraBase.Indexing.Runtime;
using TerraBase.Storage.DatabaseBinding;
using DurableStorageEngine = global::TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>
/// Measures one filtered-query workload before and after the automatic-index
/// advisor builds, verifies, and activates its indexes, and compares both
/// phases against SQLite running the identical workload with and without a
/// matching explicit index. Every phase verifies its result set against the
/// source-scan baseline; the receipt fails when results disagree or the
/// index-served phase does not improve.
/// </summary>
internal static class AutomaticIndexWorkload
{
    private const string Collection = "users";
    private const int StatusValues = 50;
    // Below the measured page limit so both engines return the complete
    // matching set and the correctness guard compares identical sets.
    private const int RangeWidth = 60;
    private const int ResultLimit = 100;

    public static bool IsRequested(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--workload=", StringComparison.OrdinalIgnoreCase)
                && argument["--workload=".Length..].Equals("automatic-index", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (argument.Equals("--workload", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length
                && args[index + 1].Equals("automatic-index", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var documents = 2_000;
        var samples = 60;
        var profileIterations = 0;
        string? outputPath = null;
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals("--documents", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var parsedDocuments))
            {
                documents = parsedDocuments;
            }
            else if (args[index].Equals("--samples", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var parsedSamples))
            {
                samples = parsedSamples;
            }
            else if (args[index].Equals("--profile", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var parsedIterations))
            {
                profileIterations = parsedIterations;
            }
            else if (args[index].Equals("--output", StringComparison.OrdinalIgnoreCase))
            {
                outputPath = args[index + 1];
            }
        }

        if (documents < 100 || samples < 30 || RangeWidth >= documents || RangeWidth >= ResultLimit)
        {
            Console.Error.WriteLine(
                "automatic-index requires --documents >= 100, --samples >= 30, and documents > range width.");
            return 2;
        }

        var root = Path.Combine(Path.GetTempPath(), $"terrabase-bench-automatic-{Guid.NewGuid():N}");
        try
        {
            return profileIterations > 0
                ? await RunProfiledAsync(root, documents, profileIterations).ConfigureAwait(false)
                : await RunMeasuredAsync(root, documents, samples, outputPath).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Prepares the workload, activates the automatic indexes, then loops the
    /// index-served queries without measurement so a sampling profiler sees
    /// only the served hot path.
    /// </summary>
    private static async Task<int> RunProfiledAsync(string root, int documents, int iterations)
    {
        var tenant = new TenantId("bench");
        var collection = new CollectionId(Collection);
        var descriptor = new DatabaseBindingDescriptor(
            new DatabaseBindingKey(tenant, new DatabaseRouteId("bench")),
            EntityId.New(),
            root,
            DatabaseStorageBackend.StorageEngine,
            DatabaseBindingMode.Explicit,
            Region.Gru,
            new HashSet<Region> { Region.Gru });
        var scope = new AutomaticIndexScope(tenant.Value, descriptor.DatabaseUlid.ToString(), Collection);
        await using var source = new DurableStorageEngine(root);
        for (var index = 0; index < documents; index++)
        {
            await source.UpsertAsync(
                new DocumentKey(tenant, collection, $"doc-{index:D6}"),
                Body($"{{\"status\":\"s{index % StatusValues:D2}\",\"value\":{index}}}"),
                DocumentPrecondition.Missing).ConfigureAwait(false);
        }

        await using var runtime = await AutomaticIndexRuntime.OpenAsync(source).ConfigureAwait(false);
        var binding = new DatabaseBinding(
            descriptor,
            new DatabaseScopedStorageProvider(source, tenant, ownsInner: false),
            descriptor.PhysicalPath)
        {
            DurableEngine = source,
        };
        var sourceExecutor = new IndexRuntimeSourceQueryExecutor(binding, collection);
        var equality = new IndexRuntimeQuery(
            tenant,
            collection,
            [IndexPredicate.Equal("status", "s00")]);
        var range = new IndexRuntimeQuery(
            tenant,
            collection,
            [
                IndexPredicate.GreaterThanOrEqual("value", 0L),
                IndexPredicate.LessThan("value", (long)RangeWidth),
            ]);
        var bounds = IndexRuntimeExecutionBounds.ForPage(
            resultLimit: ResultLimit,
            afterDocumentId: null,
            maxRowsExamined: 10_000,
            maxBytesRead: 8UL * 1024 * 1024,
            maxWorkUnits: 20_000);
        foreach (var query in new[] { equality, range })
        {
            foreach (var parameters in Enumerable.Range(0, 25))
            {
                _ = await runtime.QueryBoundedAsync(scope, query, bounds, sourceExecutor.ExecuteAsync).ConfigureAwait(false);
            }
        }

        await using (var advisor = AutomaticIndexAdvisorLoop.Start(runtime, new AutomaticIndexAdvisorOptions
        {
            Interval = TimeSpan.FromHours(1),
        }))
        {
            for (var tick = 0; tick < 4; tick++)
            {
                await advisor.TickAsync().ConfigureAwait(false);
                var pending = runtime.Coordinator.Snapshot(scope).Indexes
                    .Count(static record => record.Phase
                        is AutomaticIndexPhase.Candidate
                        or AutomaticIndexPhase.Selected
                        or AutomaticIndexPhase.Building);
                if (pending == 0)
                {
                    break;
                }
            }
        }

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var query = iteration % 2 == 0 ? equality : range;
            _ = await runtime.QueryBoundedAsync(scope, query, bounds, sourceExecutor.ExecuteAsync).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> RunMeasuredAsync(string root, int documents, int samples, string? outputPath)
    {
        var tenant = new TenantId("bench");
        var collection = new CollectionId(Collection);
        var descriptor = new DatabaseBindingDescriptor(
            new DatabaseBindingKey(tenant, new DatabaseRouteId("bench")),
            EntityId.New(),
            root,
            DatabaseStorageBackend.StorageEngine,
            DatabaseBindingMode.Explicit,
            Region.Gru,
            new HashSet<Region> { Region.Gru });
        var scope = new AutomaticIndexScope(
            tenant.Value,
            descriptor.DatabaseUlid.ToString(),
            Collection);

        await using var source = new DurableStorageEngine(root);
        for (var index = 0; index < documents; index++)
        {
            await source.UpsertAsync(
                new DocumentKey(tenant, collection, $"doc-{index:D6}"),
                Body($"{{\"status\":\"s{index % StatusValues:D2}\",\"value\":{index}}}"),
                DocumentPrecondition.Missing).ConfigureAwait(false);
        }

        await using var runtime = await AutomaticIndexRuntime.OpenAsync(source).ConfigureAwait(false);
        var binding = new DatabaseBinding(
            descriptor,
            new DatabaseScopedStorageProvider(source, tenant, ownsInner: false),
            descriptor.PhysicalPath)
        {
            DurableEngine = source,
        };
        var sourceExecutor = new IndexRuntimeSourceQueryExecutor(binding, collection);
        var shapes = new QueryShapeCase[]
        {
            new(
                "equality",
                status => new IndexRuntimeQuery(
                    tenant,
                    collection,
                    [IndexPredicate.Equal("status", $"s{status:D2}")]),
                Enumerable.Range(0, StatusValues).ToArray()),
            new(
                "range",
                offset => new IndexRuntimeQuery(
                    tenant,
                    collection,
                    [
                        IndexPredicate.GreaterThanOrEqual("value", (long)(offset * RangeWidth)),
                        IndexPredicate.LessThan("value", (long)((offset * RangeWidth) + RangeWidth)),
                    ]),
                Enumerable.Range(0, Math.Max(1, documents / RangeWidth)).ToArray()),
        };
        var bounds = IndexRuntimeExecutionBounds.ForPage(
            resultLimit: ResultLimit,
            afterDocumentId: null,
            maxRowsExamined: 10_000,
            maxBytesRead: 8UL * 1024 * 1024,
            maxWorkUnits: 20_000);

        // Warm every shape and collect the source-scan baseline: expected
        // document sets and measured scan-phase numbers.
        var expected = new Dictionary<string, string[]>[shapes.Length];
        var scanPhases = new PhaseResult[shapes.Length];
        var indexedPhases = new PhaseResult[shapes.Length];
        for (var shape = 0; shape < shapes.Length; shape++)
        {
            var testCase = shapes[shape];
            expected[shape] = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var parameters in testCase.Parameters)
            {
                var result = await runtime
                    .QueryBoundedAsync(scope, testCase.Query(parameters), bounds, sourceExecutor.ExecuteAsync)
                    .ConfigureAwait(false);
                expected[shape][testCase.Key(parameters)] = result.Documents
                    .Select(static document => document.Key.DocumentId)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            }

            scanPhases[shape] = await MeasureTerrabaseAsync(
                runtime, scope, sourceExecutor, testCase, bounds, samples).ConfigureAwait(false);
        }

        // The advisor converges one bounded batch of candidates per pass, so
        // tick until the whole workload settles, exactly as the running host
        // would over its periodic passes. The timer interval is pushed out so
        // only these manual passes run during measurement.
        var advisorWatch = Stopwatch.StartNew();
        await using (var advisor = AutomaticIndexAdvisorLoop.Start(runtime, new AutomaticIndexAdvisorOptions
        {
            Interval = TimeSpan.FromHours(1),
            MaxBuildsPerTick = 2,
        }))
        {
            for (var tick = 0; tick < shapes.Length + 2; tick++)
            {
                await advisor.TickAsync().ConfigureAwait(false);
                var pending = runtime.Coordinator.Snapshot(scope).Indexes
                    .Count(static record => record.Phase
                        is AutomaticIndexPhase.Candidate
                        or AutomaticIndexPhase.Selected
                        or AutomaticIndexPhase.Building);
                if (pending == 0)
                {
                    break;
                }
            }
        }

        var advisorElapsed = advisorWatch.Elapsed;
        foreach (var record in runtime.Coordinator.Snapshot(scope).Indexes.ToArray())
        {
            Console.WriteLine(
                $"advisor outcome: phase={record.Phase} candidate={record.CandidateId[..12]} fields=[{string.Join(",", record.Candidate.Fields)}]");
        }

        for (var shape = 0; shape < shapes.Length; shape++)
        {
            var testCase = shapes[shape];
            // Correctness first: the index phase must return exactly the
            // baseline document set for every query of every shape.
            foreach (var parameters in testCase.Parameters)
            {
                var result = await runtime
                    .QueryBoundedAsync(scope, testCase.Query(parameters), bounds, sourceExecutor.ExecuteAsync)
                    .ConfigureAwait(false);
                var actual = result.Documents
                    .Select(static document => document.Key.DocumentId)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (!actual.SequenceEqual(
                        expected[shape].GetValueOrDefault(testCase.Key(parameters)) ?? [],
                        StringComparer.Ordinal))
                {
                    Console.Error.WriteLine(
                        $"automatic-index correctness failure for shape '{testCase.Name}' parameter {parameters}: the index phase returned a different document set than the source scan.");
                    return 1;
                }
            }

            indexedPhases[shape] = await MeasureTerrabaseAsync(
                runtime, scope, sourceExecutor, testCase, bounds, samples).ConfigureAwait(false);
        }

        var sqlite = await MeasureSqliteAsync(root, documents, samples, shapes, expected).ConfigureAwait(false);

        var markdown = Render(documents, samples, advisorElapsed, shapes, scanPhases, indexedPhases, sqlite);
        Console.Write(markdown);
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, markdown).ConfigureAwait(false);
            Console.WriteLine($"Summary: {Path.GetFullPath(outputPath)}");
        }

        for (var shape = 0; shape < shapes.Length; shape++)
        {
            if (indexedPhases[shape].IndexServedFraction < 0.99
                || indexedPhases[shape].P95Microseconds >= scanPhases[shape].P95Microseconds
                || indexedPhases[shape].MeanRowsExamined >= scanPhases[shape].MeanRowsExamined / 2)
            {
                Console.Error.WriteLine(
                    $"automatic-index qualification failed for shape '{shapes[shape].Name}': the index-served phase must be active and clearly faster.");
                return 1;
            }
        }

        return 0;
    }

    private static async Task<PhaseResult> MeasureTerrabaseAsync(
        AutomaticIndexRuntime runtime,
        AutomaticIndexScope scope,
        IndexRuntimeSourceQueryExecutor sourceExecutor,
        QueryShapeCase testCase,
        IndexRuntimeExecutionBounds bounds,
        int samples)
    {
        var latencies = new long[samples];
        ulong rowsExamined = 0;
        ulong workUnits = 0;
        var indexServed = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var sample = 0; sample < samples; sample++)
        {
            var query = testCase.Query(testCase.Parameters[sample % testCase.Parameters.Length]);
            stopwatch.Restart();
            var result = await runtime
                .QueryBoundedAsync(scope, query, bounds, sourceExecutor.ExecuteAsync)
                .ConfigureAwait(false);
            stopwatch.Stop();
            latencies[sample] = stopwatch.ElapsedTicks;
            rowsExamined += result.Execution.Measurement.RowsExamined;
            workUnits += result.Execution.Measurement.WorkUnits;
            if (result.Plan.IndexName.Length > 0)
            {
                indexServed++;
            }
        }

        Array.Sort(latencies);
        return new PhaseResult(
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            Percentile(latencies, 0.99),
            (double)rowsExamined / samples,
            (double)workUnits / samples,
            (double)indexServed / samples);
    }

    private static async Task<SqlitePhases> MeasureSqliteAsync(
        string root,
        int documents,
        int samples,
        QueryShapeCase[] shapes,
        Dictionary<string, string[]>[] expected)
    {
        var databasePath = Path.Combine(root, "parity.sqlite");
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;").ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "CREATE TABLE users (id TEXT PRIMARY KEY, status TEXT NOT NULL, value INTEGER NOT NULL);").ConfigureAwait(false);
        await using (var insert = new SqliteCommand(
            "INSERT INTO users (id, status, value) VALUES (@id, @status, @value);",
            connection))
        {
            var id = insert.Parameters.Add("@id", SqliteType.Text);
            var status = insert.Parameters.Add("@status", SqliteType.Text);
            var value = insert.Parameters.Add("@value", SqliteType.Integer);
            await ExecuteAsync(connection, "BEGIN;").ConfigureAwait(false);
            for (var index = 0; index < documents; index++)
            {
                id.Value = $"doc-{index:D6}";
                status.Value = $"s{index % StatusValues:D2}";
                value.Value = index;
                await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await ExecuteAsync(connection, "COMMIT;").ConfigureAwait(false);
        }

        var scan = new PhaseResult[shapes.Length];
        var indexed = new PhaseResult[shapes.Length];
        for (var shape = 0; shape < shapes.Length; shape++)
        {
            scan[shape] = await MeasureSqlitePhaseAsync(connection, shapes[shape], samples).ConfigureAwait(false);
        }

        // The comparator index mirrors the automatic candidate exactly: one
        // ordered index per queried field.
        await ExecuteAsync(connection, "CREATE INDEX ix_users_status ON users(status);").ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE INDEX ix_users_value ON users(value);").ConfigureAwait(false);
        for (var shape = 0; shape < shapes.Length; shape++)
        {
            var testCase = shapes[shape];
            var phase = await MeasureSqlitePhaseAsync(connection, testCase, samples).ConfigureAwait(false);
            // The indexed SQLite run must also reproduce the baseline sets.
            foreach (var parameters in testCase.Parameters)
            {
                var ids = await QuerySqliteIdsAsync(connection, testCase, parameters).ConfigureAwait(false);
                if (!ids.SequenceEqual(
                        expected[shape].GetValueOrDefault(testCase.Key(parameters)) ?? [],
                        StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"SQLite comparator diverged from the TerraBase baseline for shape '{testCase.Name}'.");
                }
            }

            indexed[shape] = phase;
        }

        return new SqlitePhases(scan, indexed);
    }

    private static async Task<PhaseResult> MeasureSqlitePhaseAsync(
        SqliteConnection connection,
        QueryShapeCase testCase,
        int samples)
    {
        var latencies = new long[samples];
        var stopwatch = Stopwatch.StartNew();
        for (var sample = 0; sample < samples; sample++)
        {
            var parameters = testCase.Parameters[sample % testCase.Parameters.Length];
            stopwatch.Restart();
            await using var command = testCase.SqliteCommand(connection, parameters);
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            var rows = 0;
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows++;
            }

            stopwatch.Stop();
            latencies[sample] = stopwatch.ElapsedTicks;
        }

        Array.Sort(latencies);
        return new PhaseResult(
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            Percentile(latencies, 0.99),
            MeanRowsExamined: 0,
            MeanWorkUnits: 0,
            IndexServedFraction: 0);
    }

    private static async Task<string[]> QuerySqliteIdsAsync(
        SqliteConnection connection,
        QueryShapeCase testCase,
        int parameters)
    {
        await using var command = testCase.SqliteCommand(connection, parameters);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var ids = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return [.. ids.Order(StringComparer.Ordinal)];
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string Render(
        int documents,
        int samples,
        TimeSpan advisorElapsed,
        QueryShapeCase[] shapes,
        PhaseResult[] scan,
        PhaseResult[] indexed,
        SqlitePhases sqlite)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Automatic index before/after with a SQLite comparator");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Documents: {documents}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Samples per phase and shape: {samples}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Advisor pass wall time: {advisorElapsed.TotalMilliseconds:F0} ms");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Receipt: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        builder.AppendLine();
        builder.AppendLine("| Shape | Lane | p50 µs | p95 µs | p99 µs | Mean rows examined | Index-served |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");
        void Row(string shape, string lane, PhaseResult result) =>
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {shape} | {lane} | {result.P50Microseconds:F1} | {result.P95Microseconds:F1} | {result.P99Microseconds:F1} | {result.MeanRowsExamined:F1} | {result.IndexServedFraction:P0} |");
        for (var shape = 0; shape < shapes.Length; shape++)
        {
            var name = shapes[shape].Name;
            Row(name, "TerraBase source scan", scan[shape]);
            Row(name, "TerraBase automatic index", indexed[shape]);
            Row(name, "SQLite no index", sqlite.Scan[shape]);
            Row(name, "SQLite explicit index", sqlite.Indexed[shape]);
        }

        builder.AppendLine();
        builder.AppendLine("| Shape | Auto vs scan p95 | Auto vs scan rows | SQLite scan / auto p95 | Auto / SQLite indexed p95 |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        for (var shape = 0; shape < shapes.Length; shape++)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {shapes[shape].Name} | {(1 - (double)indexed[shape].P95Microseconds / scan[shape].P95Microseconds):P1} | {(1 - indexed[shape].MeanRowsExamined / scan[shape].MeanRowsExamined):P1} | {(double)sqlite.Scan[shape].P95Microseconds / indexed[shape].P95Microseconds:F2}x | {(double)indexed[shape].P95Microseconds / sqlite.Indexed[shape].P95Microseconds:F2}x |");
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static long Percentile(long[] sortedTicks, double percentile) =>
        sortedTicks[(int)Math.Floor((sortedTicks.Length - 1) * percentile)];

    private static JsonElement Body(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record SqlitePhases(PhaseResult[] Scan, PhaseResult[] Indexed);

    private sealed record QueryShapeCase(
        string Name,
        Func<int, IndexRuntimeQuery> Query,
        int[] Parameters)
    {
        public string Key(int parameters) => $"{Name}:{parameters}";

        public SqliteCommand SqliteCommand(SqliteConnection connection, int parameters) => Name switch
        {
            // ORDER BY id LIMIT mirrors TerraBase's page contract exactly:
            // deterministic document-id order with the same result limit, so
            // the comparator returns the same page when matches exceed it.
            "equality" => Command(connection,
                "SELECT id FROM users WHERE status = @p0 ORDER BY id LIMIT @limit;",
                [
                    new SqliteParameter("@p0", $"s{parameters:D2}"),
                    new SqliteParameter("@limit", ResultLimit),
                ]),
            "range" => Command(connection,
                "SELECT id FROM users WHERE value >= @p0 AND value < @p1 ORDER BY id LIMIT @limit;",
                [
                    new SqliteParameter("@p0", (long)(parameters * RangeWidth)),
                    new SqliteParameter("@p1", (long)((parameters * RangeWidth) + RangeWidth)),
                    new SqliteParameter("@limit", ResultLimit),
                ]),
            _ => throw new InvalidOperationException($"No SQLite mapping for shape '{Name}'."),
        };

        private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteParameter[] parameters)
        {
            var command = new SqliteCommand(sql, connection);
            command.Parameters.AddRange(parameters);
            return command;
        }
    }

    private readonly record struct PhaseResult(
        long P50Ticks,
        long P95Ticks,
        long P99Ticks,
        double MeanRowsExamined,
        double MeanWorkUnits,
        double IndexServedFraction)
    {
        public double P50Microseconds => P50Ticks * 1_000_000.0 / Stopwatch.Frequency;

        public double P95Microseconds => P95Ticks * 1_000_000.0 / Stopwatch.Frequency;

        public double P99Microseconds => P99Ticks * 1_000_000.0 / Stopwatch.Frequency;
    }
}
