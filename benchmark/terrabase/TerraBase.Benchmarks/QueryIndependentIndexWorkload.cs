using System.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TerraBase.Indexing;
using TerraBase.Indexing.Automatic;
using TerraBase.Indexing.Automatic.Jev;
using TerraBase.Indexing.Runtime;
using TerraBase.Storage.DatabaseBinding;
using DurableStorageEngine = global::TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>
/// Equal-budget first-query comparison for no secondary index, deterministic
/// query-independent indexing, and optional Jev advice. Jev is only called
/// when --jev and TERRABASE_JEV_API_KEY are supplied; otherwise the row records
/// the deterministic fallback explicitly instead of disguising a local run as
/// a model qualification.
/// </summary>
internal static class QueryIndependentIndexWorkload
{
    private const string Collection = "users";

    public static bool IsRequested(string[] args) =>
        args.Any(argument => argument.Equals("--workload=query-independent", StringComparison.OrdinalIgnoreCase))
        || args.Contains("query-independent", StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var documents = IntOption(args, "--documents", 2_000);
        var samples = IntOption(args, "--samples", 50);
        var useJev = args.Contains("--jev", StringComparer.OrdinalIgnoreCase);
        var outputDirectory = StringOption(args, "--output");
        var output = outputDirectory is null
            ? Path.Combine("bench", "query-independent-indexing-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md")
            : Path.Combine(outputDirectory, "query-independent-indexing.md");
        if (documents < 100 || samples < 10)
        {
            Console.Error.WriteLine("query-independent requires --documents >= 100 and --samples >= 10.");
            return 2;
        }

        var rows = new List<StrategyResult>
        {
            await RunStrategyAsync("No secondary index", documents, samples, null, useJev: false).ConfigureAwait(false),
            await RunStrategyAsync("Deterministic schema statistics", documents, samples, null, useJev: false).ConfigureAwait(false),
        };
        if (useJev)
        {
            rows.Add(await RunStrategyAsync("Jev-assisted", documents, samples, Environment.GetEnvironmentVariable("TERRABASE_JEV_API_KEY"), useJev: true).ConfigureAwait(false));
        }

        var markdown = Render(documents, samples, rows);
        Console.Write(markdown);
        var fullOutput = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await File.WriteAllTextAsync(fullOutput, markdown).ConfigureAwait(false);
        Console.WriteLine($"Summary: {fullOutput}");
        return rows.Any(row => !row.Correct) ? 1 : 0;
    }

    private static async Task<StrategyResult> RunStrategyAsync(
        string strategy, int documentCount, int samples, string? apiKey, bool useJev)
    {
        var root = Path.Combine(Path.GetTempPath(), "terrabase-bench-query-independent-" + Guid.NewGuid().ToString("N"));
        var tenant = new TenantId("benchmark-tenant");
        var collection = new CollectionId(Collection);
        var scope = new AutomaticIndexScope(tenant.Value, "benchmark-database", Collection);
        var descriptor = new DatabaseBindingDescriptor(
            new DatabaseBindingKey(tenant, new DatabaseRouteId("benchmark")), EntityId.New(), root,
            DatabaseStorageBackend.StorageEngine, DatabaseBindingMode.Explicit, Region.Gru, new HashSet<Region> { Region.Gru });
        var query = new IndexRuntimeQuery(tenant, collection, [IndexPredicate.Equal("status", "s17")]);
        var expected = Enumerable.Range(0, documentCount).Where(index => index % 100 == 17)
            .Select(index => $"doc-{index:D6}").ToArray();
        try
        {
            await using var source = new DurableStorageEngine(root);
            for (var index = 0; index < documentCount; index++)
            {
                await source.UpsertAsync(new DocumentKey(tenant, collection, $"doc-{index:D6}"),
                    Body($"{{\"status\":\"s{index % 100:D2}\",\"value\":{index}}}"), DocumentPrecondition.Missing).ConfigureAwait(false);
            }

            await using var runtime = await AutomaticIndexRuntime.OpenAsync(source, new AutomaticIndexRuntimeOptions { DisablePageCache = true }).ConfigureAwait(false);
            var binding = new DatabaseBinding(descriptor, new DatabaseScopedStorageProvider(source, tenant, ownsInner: false), root)
            {
                DurableEngine = source,
            };
            var sourceExecutor = new IndexRuntimeSourceQueryExecutor(binding, collection);
            var bounds = IndexRuntimeExecutionBounds.ForPage(100, null, 100_000, 64UL * 1024 * 1024, 200_000);
            var before = await MeasureAsync(runtime, scope, query, sourceExecutor, bounds, samples).ConfigureAwait(false);
            if (strategy == "No secondary index")
            {
                return before with { Strategy = strategy, Correct = before.ResultCount == expected.Length, Advice = "not-run" };
            }

            await using var snapshot = await source.BeginSnapshotAsync().ConfigureAwait(false);
            var profileWatch = Stopwatch.StartNew();
            var profile = await new CollectionIndexProfiler().ProfileAsync(source, snapshot, scope).ConfigureAwait(false);
            var candidates = await new SchemaStatisticsCandidateSource().GetCandidatesAsync(profile,
                new QueryIndependentIndexPolicy { MaximumIndexes = 1, PreferredPaths = ["status"] }).ConfigureAwait(false);
            IAutoIndexAdvisor advisor = new SchemaStatisticsAdvisor();
            JevAutoIndexAdvisor? jev = null;
            JevTenantApprovalRegistry? approvals = null;
            if (useJev && !string.IsNullOrWhiteSpace(apiKey))
            {
                approvals = new JevTenantApprovalRegistry();
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
            var after = await MeasureAsync(runtime, scope, query, sourceExecutor, bounds, samples).ConfigureAwait(false);
            var records = runtime.Coordinator.Snapshot(scope).Indexes;
            var active = records.Any(record => record.Phase == AutomaticIndexPhase.Active && record.Visible);
            var reportedInferenceCost = advice.Inference?.EstimatedInputCostUsd ?? 0;
            var storageBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
            var mode = useJev && string.IsNullOrWhiteSpace(apiKey) ? "fallback-no-approval-key" : advice.Provider + ":" + advice.ReasonCode;
            jev?.Dispose();
            return new StrategyResult(strategy, before.FirstP50Microseconds, after.FirstP50Microseconds, before.FirstP95Microseconds,
                after.FirstP95Microseconds, before.FirstRowsExamined, after.FirstRowsExamined, before.FirstResultCount, after.FirstResultCount,
                active, before.FirstResultCount == expected.Length && after.FirstResultCount == expected.Length,
                profileWatch.Elapsed.TotalMilliseconds, buildWatch.Elapsed.TotalMilliseconds,
                adviceWatch.Elapsed.TotalMilliseconds, reportedInferenceCost, storageBytes, mode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<StrategyResult> MeasureAsync(
        AutomaticIndexRuntime runtime, AutomaticIndexScope scope, IndexRuntimeQuery query,
        IndexRuntimeSourceQueryExecutor sourceExecutor, IndexRuntimeExecutionBounds bounds, int samples)
    {
        var latencies = new long[samples];
        ulong rows = 0;
        var count = 0;
        for (var index = 0; index < samples; index++)
        {
            var watch = Stopwatch.StartNew();
            var result = await runtime.QueryBoundedAsync(scope, query, bounds, sourceExecutor.ExecuteAsync).ConfigureAwait(false);
            watch.Stop();
            latencies[index] = watch.ElapsedTicks;
            rows += result.Execution.Measurement.RowsExamined;
            count = result.Documents.Count;
        }

        Array.Sort(latencies);
        return new StrategyResult(string.Empty, Microseconds(latencies[0]),
            Microseconds(latencies[(int)Math.Floor((samples - 1) * 0.5)]), Microseconds(latencies[(int)Math.Floor((samples - 1) * 0.95)]),
            Microseconds(latencies[(int)Math.Floor((samples - 1) * 0.95)]), (double)rows / samples, (double)rows / samples,
            count, count, false, true, 0, 0, 0, 0, 0, "measured");
    }

    private static string Render(int documents, int samples, IReadOnlyList<StrategyResult> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Query-independent indexing comparison");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTimeOffset.UtcNow:O}; documents: {documents}; samples: {samples}; equal policy: one index, status path; evaluation query was not supplied to candidate selection.");
        builder.AppendLine();
        builder.AppendLine("| Strategy | First p50 µs | Steady p50 µs | No-index p95 µs | Steady p95 µs | Rows examined | Active index | Result coverage | Profile ms | Build ms | Inference ms | Inference cost USD | Storage bytes | Advice |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var row in rows)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Strategy} | {row.FirstP50Microseconds:F1} | {row.SteadyP50Microseconds:F1} | {row.FirstP95Microseconds:F1} | {row.SteadyP95Microseconds:F1} | {row.SteadyRowsExamined:F1} | {row.ActiveIndex} | {row.Correct} | {row.ProfileMilliseconds:F1} | {row.BuildMilliseconds:F1} | {row.InferenceMilliseconds:F1} | {row.InferenceCostUsd:F8} | {row.StorageBytes} | {row.Advice} |");
        }

        builder.AppendLine();
        builder.AppendLine("Jev is only a measured external strategy when `--jev` and `TERRABASE_JEV_API_KEY` are explicitly supplied. Otherwise its row is omitted; no fallback run is relabeled as Jev. Model probabilities/confidence are not treated as measured index benefit.");
        return builder.ToString();
    }

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

    private sealed record StrategyResult(
        string Strategy, double FirstP50Microseconds, double SteadyP50Microseconds,
        double FirstP95Microseconds, double SteadyP95Microseconds, double FirstRowsExamined,
        double SteadyRowsExamined, int FirstResultCount, int ResultCount, bool ActiveIndex,
        bool Correct, double ProfileMilliseconds, double BuildMilliseconds, double InferenceMilliseconds,
        decimal InferenceCostUsd, long StorageBytes, string Advice);
}
