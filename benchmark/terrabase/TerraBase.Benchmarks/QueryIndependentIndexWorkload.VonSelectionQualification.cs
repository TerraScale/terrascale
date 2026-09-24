using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Indexing.Automatic;
using TerraBase.Indexing.Automatic.Jev;
using TerraBase.Indexing.Runtime;
using TerraBase.Storage.DatabaseBinding;
using DurableStorageEngine = global::TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

internal static partial class QueryIndependentIndexWorkload
{
    // These operational roles are fixed independently of the held-out predicate draw.
    // Each role has a plausible use, so the model can still abstain or rank them differently.
    private static readonly ImmutableDictionary<string, string> QualificationDescriptions =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("account.lookupId", "Stable account identifier used by operators to look up one account.")
            .Add("service.region", "Service region code used by operators to filter accounts for regional operations.")
            .Add("review.queue", "Review queue code used by operators to filter assigned accounts.")
            .Add("payload", "Opaque export payload; it has no lookup, filtering, or ordering role.");
    private const ulong QualificationLogicalCapBytes = 4UL * 1024 * 1024;
    private const int QualificationMaintenanceDocuments = 100;

    private static async Task<int> RunVonSelectionQualificationAsync(
        int documents, int samples, int maximumIndexes, int physicalBudgetMiB, string? requestedOutput)
    {
        var output = Path.GetFullPath(requestedOutput ?? Path.Combine("bench",
            "query-independent-von-selection-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(output);
        var endpoint = new Uri(Environment.GetEnvironmentVariable("TERRABASE_VON_ENDPOINT")
            ?? "http://127.0.0.1:8000/v1/systemone");
        if (!endpoint.IsLoopback)
        {
            throw new ArgumentException("Von selection qualification requires a local loopback endpoint.");
        }
        var option = new JevAdvisorOptions
        {
            Provider = "von", ModelVersion = "von-1.2.0", Endpoint = endpoint,
            Deadline = TimeSpan.FromSeconds(30), InputPricePerMillionTokensUsd = 0,
        };
        option.Validate();
        var seed = RandomNumberGenerator.GetInt32(int.MaxValue);
        var documentSeed = RandomNumberGenerator.GetInt32(int.MaxValue);
        // Persist the plan before any model response is inspected. It contains
        // the draw seed, not the eventual held-out predicate objects.
        var plan = new VonQualificationPlan(2, DateTimeOffset.UtcNow, documents,
            QualificationMaintenanceDocuments,
            maximumIndexes, physicalBudgetMiB, QualificationLogicalCapBytes,
            seed, documentSeed, option.ModelVersion, option.Endpoint.ToString(),
            QualificationDescriptions);
        await File.WriteAllTextAsync(Path.Combine(output, "selection-plan.json"),
            JsonSerializer.Serialize(plan, RawJsonOptions) + Environment.NewLine).ConfigureAwait(false);
        var rows = new List<VonQualificationRow>();
        for (var trial = 1; trial <= 3; trial++)
        {
            var arms = trial switch
            {
                1 => new[] { "no-index", "deterministic", "von" },
                2 => new[] { "deterministic", "von", "no-index" },
                _ => new[] { "von", "no-index", "deterministic" },
            };
            foreach (var arm in arms)
            {
                rows.AddRange(await RunVonQualificationArmAsync(
                    trial, arm, documents, samples, maximumIndexes, physicalBudgetMiB,
                    unchecked(seed + trial), documentSeed, option).ConfigureAwait(false));
            }
        }

        var receipt = new VonQualificationReceipt(
            2, DateTimeOffset.UtcNow, documents, samples, QualificationMaintenanceDocuments,
            maximumIndexes, physicalBudgetMiB,
            QualificationLogicalCapBytes,
            seed, documentSeed, option.ModelVersion, option.Endpoint.ToString(),
            QualificationDescriptions,
            BinarySha256(typeof(AutomaticIndexRuntime).Assembly.Location),
            BinarySha256(typeof(QueryIndependentIndexWorkload).Assembly.Location), rows,
            CalculateVonQualificationRegressions(rows));
        var rawPath = Path.Combine(output, "von-selection-receipt.json");
        await File.WriteAllTextAsync(rawPath,
            JsonSerializer.Serialize(receipt, RawJsonOptions) + Environment.NewLine).ConfigureAwait(false);
        var summaryPath = Path.Combine(output, "von-selection.md");
        await File.WriteAllTextAsync(summaryPath, RenderVonQualification(receipt)).ConfigureAwait(false);
        Console.WriteLine($"Von selection qualification: {summaryPath}");
        Console.WriteLine($"Raw receipt: {rawPath}");
        return 0;
    }

    private static async Task<IReadOnlyList<VonQualificationRow>> RunVonQualificationArmAsync(
        int trial, string arm, int documents, int samples, int maximumIndexes,
        int physicalBudgetMiB, int drawSeed, int documentSeed, JevAdvisorOptions vonOptions)
    {
        var root = Path.Combine(Path.GetTempPath(), "terrabase-von-selection-" + Guid.NewGuid().ToString("N"));
        var tenant = new TenantId("von-qualification-tenant");
        var collection = new CollectionId("accounts");
        var scope = new AutomaticIndexScope(tenant.Value, "von-qualification-database", collection.Value);
        var descriptor = new DatabaseBindingDescriptor(
            new DatabaseBindingKey(tenant, new DatabaseRouteId("von-qualification")),
            EntityId.New(), root, DatabaseStorageBackend.StorageEngine,
            DatabaseBindingMode.Explicit, Region.Gru, new HashSet<Region> { Region.Gru });
        try
        {
            await using var source = new DurableStorageEngine(root);
            var ingestWatch = Stopwatch.StartNew();
            await SeedVonQualificationAsync(source, tenant, collection, documents, documentSeed).ConfigureAwait(false);
            ingestWatch.Stop();
            await using var runtime = await AutomaticIndexRuntime.OpenAsync(source,
                new AutomaticIndexRuntimeOptions { DisablePageCache = true },
                CreateVonQualificationBudgetLedger(scope, physicalBudgetMiB)).ConfigureAwait(false);
            var binding = new DatabaseBinding(descriptor,
                new DatabaseScopedStorageProvider(source, tenant, ownsInner: false), root)
            {
                DurableEngine = source,
            };
            var sourceExecutor = new IndexRuntimeSourceQueryExecutor(binding, collection);
            var bounds = IndexRuntimeExecutionBounds.ForPage(
                100, null, QueryScanBudget, QueryMemoryBudgetBytes, QueryWorkBudget);
            var storageBeforeIndexBytes = DirectoryBytes(root);
            var profileMilliseconds = 0d;
            var adviceMilliseconds = 0d;
            var buildMilliseconds = 0d;
            AutoIndexAdvice? advice = null;
            if (arm != "no-index")
            {
                EnsureEmptyQueryHistory(runtime, "before Von qualification candidate generation");
                await using var snapshot = await source.BeginSnapshotAsync().ConfigureAwait(false);
                var profileWatch = Stopwatch.StartNew();
                var profile = await new CollectionIndexProfiler()
                    .ProfileAsync(source, snapshot, scope).ConfigureAwait(false);
                var candidates = await new SchemaStatisticsCandidateSource().GetCandidatesAsync(profile,
                    new QueryIndependentIndexPolicy { MaximumIndexes = maximumIndexes }).ConfigureAwait(false);
                profileWatch.Stop();
                profileMilliseconds = profileWatch.Elapsed.TotalMilliseconds;
                EnsureEmptyQueryHistory(runtime, "after Von qualification candidate generation");

                var adviceWatch = Stopwatch.StartNew();
                if (arm == "von")
                {
                    var approvals = new JevTenantApprovalRegistry();
                    approvals.Approve(scope, QualificationDescriptions);
                    using var httpClient = new HttpClient(new SocketsHttpHandler
                    {
                        AllowAutoRedirect = false,
                        UseCookies = false,
                    });
                    var client = new JevSystemOneClient(httpClient, vonOptions);
                    using var advisor = new JevAutoIndexAdvisor(client, approvals,
                        new JevApiCredentials(() => Environment.GetEnvironmentVariable("TERRABASE_VON_API_KEY")),
                        vonOptions);
                    advice = await advisor.AdviseAsync(candidates).ConfigureAwait(false);
                }
                else
                {
                    advice = await new SchemaStatisticsAdvisor().AdviseAsync(candidates).ConfigureAwait(false);
                }

                adviceWatch.Stop();
                adviceMilliseconds = adviceWatch.Elapsed.TotalMilliseconds;
                var buildWatch = Stopwatch.StartNew();
                using (var lifecycle = new QueryIndependentIndexLifecycleManager(runtime, scope))
                {
                    await lifecycle.ApplyAsync(candidates, advice).ConfigureAwait(false);
                }

                buildWatch.Stop();
                buildMilliseconds = buildWatch.Elapsed.TotalMilliseconds;
            }

            var maintenance = await UpdateVonQualificationMaintenanceAsync(
                source, tenant, collection, root, documentSeed).ConfigureAwait(false);
            var storageAfterMaintenanceBytes = DirectoryBytes(root);

            // Construct the query objects and expected IDs only after the initial decision.
            var shapes = DrawVonQualificationShapes(drawSeed, documentSeed, documents);
            var activePaths = runtime.Coordinator.Snapshot(scope).Indexes
                .Where(static record => record.Phase == AutomaticIndexPhase.Active && record.Visible)
                .Select(static record => record.Candidate.Fields[0])
                .Order(StringComparer.Ordinal).ToArray();
            var measurements = new List<(EvaluationShape Shape, int ExpectedIdCount, Measurement Result)>(shapes.Length);
            foreach (var shape in shapes)
            {
                var expected = Enumerable.Range(0, documents).Where(shape.Matches)
                    .Select(static index => $"doc-{index:D6}").ToArray();
                var measurement = await MeasureTerrabaseAsync(runtime, scope,
                    shape.Query(tenant, collection), sourceExecutor, bounds, samples, expected)
                    .ConfigureAwait(false);
                measurements.Add((shape, expected.Length, measurement));
            }

            var finalFileBytes = DirectoryBytes(root);
            var results = new List<VonQualificationRow>(measurements.Count);
            foreach (var (shape, expectedIdCount, measurement) in measurements)
            {
                results.Add(new VonQualificationRow(
                    trial, arm, root, shape.Path, shape.Value, expectedIdCount, activePaths,
                    advice?.Provider ?? "not-run", advice?.ReasonCode ?? "not-run",
                    advice?.Recommendations.Select(recommendation => recommendation.CandidateId).ToArray() ?? [],
                    QualificationInference(advice?.Inference), ingestWatch.Elapsed.TotalMilliseconds,
                    profileMilliseconds, adviceMilliseconds, buildMilliseconds,
                    maintenance.Milliseconds, maintenance.StorageDeltaBytes,
                    storageBeforeIndexBytes, storageAfterMaintenanceBytes, finalFileBytes,
                    finalFileBytes - storageBeforeIndexBytes - maintenance.StorageDeltaBytes,
                    measurement));
            }

            return results;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task SeedVonQualificationAsync(
        DurableStorageEngine source, TenantId tenant, CollectionId collection, int documents, int documentSeed)
    {
        for (var start = 0; start < documents; start += TransactionBatchSize)
        {
            var count = Math.Min(TransactionBatchSize, documents - start);
            var mutations = new StorageMutation[count];
            for (var index = 0; index < count; index++)
            {
                var ordinal = start + index;
                mutations[index] = StorageMutation.Upsert(
                    new DocumentKey(tenant, collection, $"doc-{ordinal:D6}"),
                    Body(VonQualificationDocumentJson(ordinal, documentSeed, updatedPayload: false)),
                    DocumentPrecondition.Missing);
            }

            var result = await source.ApplyBatchAsync(mutations).ConfigureAwait(false);
            if (result.Outcome != StorageBatchOutcome.Committed)
            {
                throw new InvalidOperationException($"Von qualification seed batch {start} did not commit: {result.Outcome}.");
            }
        }
    }

    private static async Task<MaintenanceResult> UpdateVonQualificationMaintenanceAsync(
        DurableStorageEngine source, TenantId tenant, CollectionId collection, string root,
        int documentSeed)
    {
        var before = DirectoryBytes(root);
        var watch = Stopwatch.StartNew();
        var mutations = new StorageMutation[QualificationMaintenanceDocuments];
        for (var index = 0; index < mutations.Length; index++)
        {
            mutations[index] = StorageMutation.Upsert(
                new DocumentKey(tenant, collection, $"doc-{index:D6}"),
                Body(VonQualificationDocumentJson(index, documentSeed, updatedPayload: true)),
                DocumentPrecondition.Any);
        }

        var result = await source.ApplyBatchAsync(mutations).ConfigureAwait(false);
        if (result.Outcome != StorageBatchOutcome.Committed)
        {
            throw new InvalidOperationException(
                $"Von qualification maintenance batch did not commit: {result.Outcome}.");
        }

        watch.Stop();
        return new MaintenanceResult(watch.Elapsed.TotalMilliseconds, DirectoryBytes(root) - before);
    }

    private static string VonQualificationDocumentJson(int ordinal, int documentSeed, bool updatedPayload) =>
        $"{{\"account\":{{\"lookupId\":\"acct-{ordinal:D6}\"}},"
        + $"\"service\":{{\"region\":\"region-{(ordinal + documentSeed % 50) % 50:D2}\"}},"
        + $"\"review\":{{\"queue\":\"queue-{(ordinal + documentSeed % 40) % 40:D2}\"}},"
        + $"\"payload\":\"{(updatedPayload ? "updated" : "opaque")}-{ordinal:D6}\"}}";

    private static EvaluationShape[] DrawVonQualificationShapes(int seed, int documentSeed, int documents)
    {
        var random = new Random(seed);
        var options = new[] { 0, 1, 2 };
        random.Shuffle(options);
        var ordinal = random.Next(documents);
        return options.Take(2).Select(path => path switch
        {
            0 => new EvaluationShape("account lookup", "account.lookupId", $"acct-{ordinal:D6}",
                index => index == ordinal),
            1 => new EvaluationShape("service region", "service.region", $"region-{(ordinal + documentSeed % 50) % 50:D2}",
                index => index % 50 == ordinal % 50),
            _ => new EvaluationShape("review queue", "review.queue", $"queue-{(ordinal + documentSeed % 40) % 40:D2}",
                index => index % 40 == ordinal % 40),
        }).ToArray();
    }

    private static InMemoryAutomaticIndexBudgetLedger CreateVonQualificationBudgetLedger(
        AutomaticIndexScope scope, int physicalBudgetMiB)
    {
        var physicalCapBytes = checked((ulong)physicalBudgetMiB * 1024 * 1024);
        var ledger = new InMemoryAutomaticIndexBudgetLedger();
        ledger.SetTenantBudget(scope.Tenant, Budget(InMemoryAutomaticIndexBudgetLedger.DefaultTenantBudget));
        ledger.SetCollectionBudget(scope, Budget(InMemoryAutomaticIndexBudgetLedger.DefaultCollectionBudget));
        return ledger;

        AutomaticIndexBudget Budget(AutomaticIndexBudget defaults) => new(
            cpuUnits: defaults.CpuUnits,
            memoryBytes: defaults.MemoryBytes,
            ioUnits: defaults.IoUnits,
            maxVisibleIndexes: defaults.MaxVisibleIndexes,
            maxStorageBytes: physicalCapBytes,
            maxTransientBytes: physicalCapBytes,
            maxWriteAmplification: defaults.MaxWriteAmplification,
            maxLogicalStorageBytes: QualificationLogicalCapBytes);
    }

    private static string RenderVonQualification(VonQualificationReceipt receipt)
    {
        var text = new StringBuilder();
        text.AppendLine("# Query-independent Von selection qualification");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Generated: {receipt.GeneratedAtUtc:O}; documents per fresh root: {receipt.Documents}; "
            + $"trials: 3; repeat samples: {receipt.Samples}; post-build updates: {receipt.MaintenanceDocuments}; maximum indexes: {receipt.MaximumIndexes}; "
            + $"physical storage cap: {receipt.PhysicalBudgetMiB} MiB per collection and tenant; "
            + $"logical storage cap: {receipt.LogicalCapBytes} bytes per collection and tenant.");
        text.AppendLine("The declared field roles and both seeds are persisted in `selection-plan.json` before inference. "
            + "The same document seed and post-advice predicate draw are used for all three arms in each trial. "
            + "Each trial draws two held-out predicates "
            + "after candidate generation, advice, and index application. No prior query observations or preferred paths enter selection.");
        text.AppendLine("Von runs on the configured local endpoint; only its returned model/version and token usage are reported. "
            + "Token cost is unpriced because no service price is configured. A fallback remains a fallback.");
        var acceptedVonTrials = receipt.Rows
            .Where(static row => row.Arm == "von" && row.AdvisorProvider == "von")
            .Select(static row => row.Trial).Distinct().Count();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Accepted Von-advice trials: {acceptedVonTrials}/3. Von attempts that selected schema-statistics advice are deterministic fallback lanes.");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Loaded TerraBase DLL SHA-256: `{receipt.TerraBaseAssemblySha256}`; benchmark DLL SHA-256: `{receipt.BenchmarkAssemblySha256}`.");
        text.AppendLine();
        text.AppendLine("| Field path | Predeclared operational description |");
        text.AppendLine("| --- | --- |");
        foreach (var description in receipt.Descriptions.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| `{description.Key}` | {description.Value} |");
        }

        text.AppendLine();
        text.AppendLine("| Trial | Arm | Held-out predicate | Active paths | First µs | Repeat p50 µs | Repeat p95 µs | Expected IDs | First/repeat index served | Ingest ms | Profile ms | Advice ms | Build ms | Maintenance ms | Maintenance storage Δ B | Before-index B | After-maintenance B | Final file B | Index storage Δ B | Advisor outcome | Model | Tokens in/out | Cost |");
        text.AppendLine("| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | --- | --- |");
        foreach (var row in receipt.Rows)
        {
            var evidence = row.Inference;
            text.AppendLine(CultureInfo.InvariantCulture, $"| {row.Trial} | {row.Arm} | `{row.QueryPath} = {row.QueryValue}` | "
                + $"{string.Join(", ", row.ActivePaths.Select(static path => $"`{path}`"))} | "
                + $"{row.Measurement.FirstQueryMicroseconds:F1} | {row.Measurement.RepeatP50Microseconds:F1} | "
                + $"{row.Measurement.RepeatP95Microseconds:F1} | {row.ExpectedIdCount} | "
                + $"{row.Measurement.FirstIndexServed}/{row.Measurement.RepeatIndexServedFraction:P0} | "
                + $"{row.IngestMilliseconds:F1} | {row.ProfileMilliseconds:F1} | {row.AdviceMilliseconds:F1} | "
                + $"{row.BuildMilliseconds:F1} | {row.MaintenanceMilliseconds:F1} | "
                + $"{row.MaintenanceStorageDeltaBytes} | {row.StorageBeforeIndexBytes} | "
                + $"{row.StorageAfterMaintenanceBytes} | {row.FinalFileBytes} | "
                + $"{row.IndexStorageDeltaBytes} | {row.AdvisorProvider}:{row.AdvisorReason} | "
                + $"{evidence?.RequestedModel ?? "—"}/{evidence?.ActualModel ?? "—"} | "
                + $"{(evidence?.InputTokens is int input ? input.ToString(CultureInfo.InvariantCulture) : "—")}/"
                + $"{(evidence?.OutputTokens is int output ? output.ToString(CultureInfo.InvariantCulture) : "—")} | unpriced |");
        }

        text.AppendLine();
        text.AppendLine("## Regressions versus same-trial no-index baseline");
        text.AppendLine();
        text.AppendLine("Positive latency or maintenance percentages mean slower than the no-index arm. "
            + "Storage is the final sum-of-file-lengths difference in bytes. Each trial compares the same hidden predicates and document seed.");
        text.AppendLine();
        text.AppendLine("| Trial | Arm | Held-out predicate | First query Δ | Repeat p50 Δ | Maintenance Δ | Final storage Δ B |");
        text.AppendLine("| ---: | --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var regression in receipt.Regressions)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {regression.Trial} | {regression.Arm} | `{regression.QueryPath} = {regression.QueryValue}` | "
                + $"{VonDeltaText(regression.FirstQueryDeltaPercent)} | "
                + $"{VonDeltaText(regression.RepeatP50DeltaPercent)} | "
                + $"{VonDeltaText(regression.MaintenanceDeltaPercent)} | "
                + $"{regression.FinalStorageDeltaBytes:+#,##0;-#,##0;0} |");
        }

        text.AppendLine();
        text.AppendLine("Every timed first and repeat query checks the exact document ID set. Raw repeat samples, "
            + "candidate recommendations, and allowlisted Von choice/assessment evidence are in `von-selection-receipt.json`. "
            + "Model confidence is not a measured query benefit. All three arms receive the same TerraBase "
            + "caps and independently seeded equivalent documents. The same 100 existing documents receive "
            + "one payload update after index build in every arm, before any evaluation query is constructed. "
            + "Maintenance time and file-size change measure this one batch, not sustained write amplification. "
            + "Index storage delta is final logical file bytes minus bytes before index build and the maintenance "
            + "file-size change; it includes any later query-related file growth and is not physical disk-block usage.");
        return text.ToString();
    }

    private static List<VonQualificationRegression> CalculateVonQualificationRegressions(
        IReadOnlyList<VonQualificationRow> rows)
    {
        var regressions = new List<VonQualificationRegression>(rows.Count);
        foreach (var row in rows)
        {
            var baseline = rows.Single(candidate => candidate.Trial == row.Trial
                && candidate.Arm == "no-index"
                && candidate.QueryPath == row.QueryPath
                && candidate.QueryValue == row.QueryValue);
            regressions.Add(new VonQualificationRegression(
                row.Trial, row.Arm, row.QueryPath, row.QueryValue,
                VonDeltaPercent(row.Measurement.FirstQueryMicroseconds, baseline.Measurement.FirstQueryMicroseconds),
                VonDeltaPercent(row.Measurement.RepeatP50Microseconds, baseline.Measurement.RepeatP50Microseconds),
                VonDeltaPercent(row.MaintenanceMilliseconds, baseline.MaintenanceMilliseconds),
                row.FinalFileBytes - baseline.FinalFileBytes));
        }

        return regressions;
    }

    private static double? VonDeltaPercent(double value, double baseline) =>
        baseline == 0 ? value == 0 ? 0 : null : (value - baseline) / baseline * 100;

    private static string VonDeltaText(double? value) =>
        value is { } percent
            ? percent.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%"
            : "n/a (baseline 0)";

    private sealed record VonQualificationPlan(
        int SchemaVersion, DateTimeOffset RecordedAtUtc, int Documents, int MaintenanceDocuments,
        int MaximumIndexes, int PhysicalBudgetMiB, ulong LogicalCapBytes,
        int DrawSeed, int DocumentSeed, string RequestedModel, string Endpoint,
        ImmutableDictionary<string, string> Descriptions);

    private sealed record VonQualificationReceipt(
        int SchemaVersion, DateTimeOffset GeneratedAtUtc, int Documents, int Samples,
        int MaintenanceDocuments,
        int MaximumIndexes, int PhysicalBudgetMiB, ulong LogicalCapBytes,
        int DrawSeed, int DocumentSeed, string RequestedModel,
        string Endpoint, ImmutableDictionary<string, string> Descriptions,
        string TerraBaseAssemblySha256, string BenchmarkAssemblySha256,
        IReadOnlyList<VonQualificationRow> Rows,
        IReadOnlyList<VonQualificationRegression> Regressions);

    private static string BinarySha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record VonQualificationRow(
        int Trial, string Arm, string FreshRoot, string QueryPath, string QueryValue,
        int ExpectedIdCount, string[] ActivePaths, string AdvisorProvider, string AdvisorReason,
        string[] RecommendedCandidateIds, VonQualificationInference? Inference,
        double IngestMilliseconds, double ProfileMilliseconds, double AdviceMilliseconds,
        double BuildMilliseconds, double MaintenanceMilliseconds,
        long MaintenanceStorageDeltaBytes, long StorageBeforeIndexBytes,
        long StorageAfterMaintenanceBytes, long FinalFileBytes,
        long IndexStorageDeltaBytes, Measurement Measurement);

    private sealed record VonQualificationRegression(
        int Trial, string Arm, string QueryPath, string QueryValue,
        double? FirstQueryDeltaPercent, double? RepeatP50DeltaPercent,
        double? MaintenanceDeltaPercent, long FinalStorageDeltaBytes);

    private static VonQualificationInference? QualificationInference(IndexInferenceEvidence? evidence) =>
        evidence is null ? null : new VonQualificationInference(
            evidence.RequestedModel, evidence.ActualModel, evidence.QuestionTemplate,
            evidence.RequestDigest, evidence.InputTokens, evidence.OutputTokens,
            EstimatedInputCostUsd: null, CostStatus: "unpriced",
            evidence.Choice, evidence.Assessments.ToArray(), evidence.CacheHit);

    private sealed record VonQualificationInference(
        string RequestedModel, string? ActualModel, string QuestionTemplate, string RequestDigest,
        int? InputTokens, int? OutputTokens, decimal? EstimatedInputCostUsd, string CostStatus,
        IndexChoiceEvidence? Choice, IndexCandidateAssessment[] Assessments, bool CacheHit);
}
