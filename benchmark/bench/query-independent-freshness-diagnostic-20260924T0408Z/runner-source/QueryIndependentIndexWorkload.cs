using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
/// query-independent indexing, optional Von or legacy Jev advice, and SQLite
/// with no secondary index or an explicit matching index.
/// </summary>
internal static class QueryIndependentIndexWorkload
{
    private const string Collection = "users";
    private const string StatusPath = "status";
    private const string EvaluationStatus = "s17";
    private const string NestedSegmentPath = "profile.segment";
    private const string EvaluationSegment = "g23";
    private const int ResultLimit = 100;
    private const int DefaultMaximumIndexBudget = 2;
    private const int DefaultMaintenanceDocuments = 100;
    private const int SeedMaintenanceDocuments = 100;
    private const int DirectProbeRepeatSamples = 30;
    private const int MaximumPointMirrorProbeSamples = 1_000;
    private const int TransactionBatchSize = 100;
    private const ulong QueryMemoryBudgetBytes = 64UL * 1024 * 1024;
    private const int QueryWorkBudget = 200_000;
    private const int QueryScanBudget = 100_000;
    private const int SqliteCacheSizeKiB = 64 * 1024;
    private const long SqliteMmapSizeBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions RawJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

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
        var maximumIndexBudget = IntOption(args, "--maximum-indexes", DefaultMaximumIndexBudget);
        var maintenanceDocuments = IntOption(args, "--maintenance-documents", DefaultMaintenanceDocuments);
        var physicalIndexBudgetText = StringOption(args, "--physical-index-budget-mib");
        var physicalIndexBudgetMiB = physicalIndexBudgetText is null
            ? 0
            : int.TryParse(physicalIndexBudgetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPhysicalIndexBudget)
                ? parsedPhysicalIndexBudget
                : -1;
        var indexPerformance = args.Contains("--index-perf", StringComparer.OrdinalIgnoreCase);
        var pointMirrorProbe = args.Contains("--point-mirror-probe", StringComparer.OrdinalIgnoreCase);
        var jevRequested = args.Contains("--jev", StringComparer.OrdinalIgnoreCase);
        var vonRequested = args.Contains("--von", StringComparer.OrdinalIgnoreCase);
        var vonEndpoint = Environment.GetEnvironmentVariable("TERRABASE_VON_ENDPOINT") ?? "http://127.0.0.1:8000/v1/systemone";
        var vonApiKey = Environment.GetEnvironmentVariable("TERRABASE_VON_API_KEY");
        var jevApiKey = Environment.GetEnvironmentVariable("TERRABASE_JEV_API_KEY");
        var jevApproved = !string.IsNullOrWhiteSpace(jevApiKey);
        var outputDirectory = StringOption(args, "--output");
        var output = outputDirectory is null
            ? Path.Combine("bench", "query-independent-indexing-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md")
            : Path.Combine(outputDirectory, "query-independent-indexing.md");
        if (documents < 100 || samples < 10 || maximumIndexBudget is < 1 or > 2
            || maintenanceDocuments is < 0 or > SeedMaintenanceDocuments
            || physicalIndexBudgetMiB is < 0 or > 1024
            || pointMirrorProbe && samples > MaximumPointMirrorProbeSamples)
        {
            Console.Error.WriteLine($"query-independent requires --documents >= 100, --samples >= 10, --maximum-indexes 1..2, --maintenance-documents 0..{SeedMaintenanceDocuments}, --physical-index-budget-mib 0..1024 (0 keeps TerraBase defaults), and --samples <= {MaximumPointMirrorProbeSamples} for --point-mirror-probe.");
            return 2;
        }

        if (pointMirrorProbe)
        {
            var probeOutput = outputDirectory is null
                ? Path.Combine("bench", "query-independent-point-mirror-probe-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md")
                : Path.Combine(outputDirectory, "point-mirror-probe.md");
            return await RunPointMirrorProbeAsync(
                documents, samples, maximumIndexBudget, physicalIndexBudgetMiB, probeOutput).ConfigureAwait(false);
        }

        var rows = new List<StrategyResult>();
        rows.AddRange(await RunStrategyAsync("No secondary index", documents, samples, null, useJev: false,
            maximumIndexBudget, maintenanceDocuments, physicalIndexBudgetMiB, indexPerformance).ConfigureAwait(false));
        rows.AddRange(await RunStrategyAsync("Deterministic schema statistics", documents, samples, null, useJev: false,
            maximumIndexBudget, maintenanceDocuments, physicalIndexBudgetMiB, indexPerformance).ConfigureAwait(false));

        var sqlite = await RunSqliteComparisonAsync(documents, samples, maximumIndexBudget, maintenanceDocuments).ConfigureAwait(false);
        if (jevRequested && jevApproved)
        {
            rows.AddRange(await RunStrategyAsync("Jev-assisted", documents, samples, jevApiKey, useJev: true,
                maximumIndexBudget, maintenanceDocuments, physicalIndexBudgetMiB, indexPerformance).ConfigureAwait(false));
        }
        if (vonRequested)
        {
            rows.AddRange(await RunStrategyAsync("Von configured", documents, samples, null, useJev: false,
                maximumIndexBudget, maintenanceDocuments, physicalIndexBudgetMiB, indexPerformance,
                vonEndpoint, vonApiKey).ConfigureAwait(false));
        }

        var markdown = Render(documents, samples, maximumIndexBudget, maintenanceDocuments,
            physicalIndexBudgetMiB, indexPerformance,
            rows, sqlite, jevRequested, jevApproved, vonRequested);
        var fullOutput = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await File.WriteAllTextAsync(fullOutput, markdown).ConfigureAwait(false);
        var rawReceiptPath = Path.Combine(Path.GetDirectoryName(fullOutput)!, "query-independent-indexing-receipt.json");
        var rawReceipt = new QueryIndependentRawReceipt(
            SchemaVersion: 1,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Documents: documents,
            RepeatSamples: samples,
            MaximumIndexBudget: maximumIndexBudget,
            MaintenanceDocuments: maintenanceDocuments,
            PhysicalIndexBudgetMiB: physicalIndexBudgetMiB,
            IndexPerformance: indexPerformance,
            SummaryFile: Path.GetFileName(fullOutput),
            SummarySha256: Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullOutput).ConfigureAwait(false))).ToLowerInvariant(),
            Rows: rows.Concat(sqlite.NoIndex).Concat(sqlite.ExplicitIndex).ToArray(),
            SqliteNoIndexPlans: sqlite.NoIndexPlans,
            SqliteExplicitPlans: sqlite.ExplicitPlans);
        await File.WriteAllTextAsync(rawReceiptPath, JsonSerializer.Serialize(rawReceipt, RawJsonOptions) + Environment.NewLine).ConfigureAwait(false);
        Console.Write(markdown);
        Console.WriteLine($"Summary: {fullOutput}");
        Console.WriteLine($"Raw receipt: {rawReceiptPath}");
        return rows.Concat(sqlite.NoIndex).Concat(sqlite.ExplicitIndex).Any(row => !row.Correct) ? 1 : 0;
    }


    private static async ValueTask CaptureFreshnessDiagnosticAsync(
        string requestedStrategy,
        string measuredStrategy,
        EvaluationShape shape,
        IndexCandidateSet candidates,
        AutoIndexAdvice advice,
        AutomaticIndexRuntime runtime,
        AutomaticIndexScope scope,
        DurableStorageEngine source,
        IndexRuntimeQuery query)
    {
        var outputPath = Environment.GetEnvironmentVariable("TERRABASE_QIDX_DIAGNOSTIC_PATH");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var sourceCut = source.ChangeFeedCursor.CommitSequence;
        var records = runtime.Coordinator.Snapshot(scope, int.MaxValue).Indexes;
        var physicalStates = new List<object>(records.Count);
        foreach (var record in records.OrderBy(static item => item.CandidateId, StringComparer.Ordinal))
        {
            var physical = await runtime.Backend.InspectPhysicalAsync(record.CandidateId).ConfigureAwait(false);
            physicalStates.Add(new
            {
                candidateId = record.CandidateId,
                paths = record.Candidate.Fields,
                phase = record.Phase.ToString(),
                lifecycleVisible = record.Visible,
                lifecycleAppliedPosition = record.AppliedPosition,
                targetPosition = record.TargetPosition,
                physicalExists = physical.Exists,
                physicalVisible = physical.Visible,
                physicalAppliedPosition = physical.AppliedPosition,
                freshAtSourceCut = physical.Exists && physical.Visible && physical.AppliedPosition >= sourceCut,
            });
        }

        var plan = await runtime.ExplainAsync(scope, query).ConfigureAwait(false);
        var diagnostic = new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            requestedStrategy,
            measuredStrategy,
            shape = shape.Name,
            queryIndependentCandidateIdsAndPaths = candidates.Candidates
                .OrderBy(static candidate => candidate.Definition.CandidateId, StringComparer.Ordinal)
                .Select(static candidate => new
                {
                    candidateId = candidate.Definition.CandidateId,
                    paths = candidate.Definition.Fields,
                }),
            advisor = new
            {
                provider = advice.Provider,
                reasonCode = advice.ReasonCode,
                recommendationCandidateIds = advice.Recommendations
                    .Select(static recommendation => recommendation.CandidateId)
                    .Order(StringComparer.Ordinal),
                modelVersion = advice.Inference?.ActualModel,
                inputTokens = advice.Inference?.InputTokens,
                outputTokens = advice.Inference?.OutputTokens,
            },
            sourceCommitSequence = sourceCut,
            activeAndPhysicalIndexStates = physicalStates,
            preQueryExplain = new
            {
                indexName = plan.IndexName,
                access = plan.Access.ToString(),
                isFresh = plan.IsFresh,
                watermarkCommitSequence = plan.Watermark.CommitSequence,
                usedSnapshotFallback = plan.UsedSnapshotFallback,
                reason = plan.Reason,
                candidates = plan.Candidates.Select(static candidate => new
                {
                    candidate.IndexName,
                    accepted = candidate.Accepted,
                    access = candidate.Access.ToString(),
                    candidate.IsFresh,
                    watermarkCommitSequence = candidate.Watermark.CommitSequence,
                    candidate.Reason,
                }),
                rejectedCandidates = plan.RejectedCandidates.Select(static candidate => new
                {
                    candidate.IndexName,
                    accepted = candidate.Accepted,
                    access = candidate.Access.ToString(),
                    candidate.IsFresh,
                    watermarkCommitSequence = candidate.Watermark.CommitSequence,
                    candidate.Reason,
                }),
            },
        };

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.AppendAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(diagnostic, RawJsonOptions) + Environment.NewLine).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<StrategyResult>> RunStrategyAsync(
        string strategy, int documentCount, int samples, string? apiKey, bool useJev,
        int maximumIndexBudget, int maintenanceDocuments, int physicalIndexBudgetMiB, bool indexPerformance,
        string? vonEndpoint = null, string? vonApiKey = null)
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
            var runtimeBudgetLedger = CreateRuntimeBudgetLedger(scope, physicalIndexBudgetMiB);
            await using var runtime = await AutomaticIndexRuntime.OpenAsync(source,
                new AutomaticIndexRuntimeOptions { DisablePageCache = true }, runtimeBudgetLedger).ConfigureAwait(false);
            var storageBeforeIndex = DirectoryBytes(root);
            var binding = new DatabaseBinding(descriptor, new DatabaseScopedStorageProvider(source, tenant, ownsInner: false), root)
            {
                DurableEngine = source,
            };
            var sourceExecutor = new IndexRuntimeSourceQueryExecutor(binding, collection);
            var bounds = IndexRuntimeExecutionBounds.ForPage(
                ResultLimit, null, QueryScanBudget, QueryMemoryBudgetBytes, QueryWorkBudget);

            if (strategy == "No secondary index")
            {
                // This is the no-index evaluation lane. Its query history is
                // intentionally populated by the measurement itself.
                var coldMaintenance = await UpdateTerrabaseMaintenanceAsync(source, tenant, collection, root, maintenanceDocuments).ConfigureAwait(false);
                var coldRows = new List<StrategyResult>(EvaluationShapes.Length);
                foreach (var shape in EvaluationShapes)
                {
                    var measurement = await MeasureTerrabaseAsync(
                        runtime, scope, shape.Query(tenant, collection), sourceExecutor, bounds, samples,
                        ExpectedIds(documentCount, shape)).ConfigureAwait(false);
                    coldRows.Add(new StrategyResult(
                        shape.Name, strategy, "0 secondary", measurement, ActiveIndex: false, ActiveIndexCount: 0, measurement.Correct,
                        QueryHistoryBeforeCandidates: null, InitialIngestMilliseconds: writeWatch.Elapsed.TotalMilliseconds,
                        ProfileMilliseconds: 0, BuildMilliseconds: 0, InferenceMilliseconds: 0, InferenceInputTokens: null,
                        InferenceOutputTokens: null, InferenceCostUsd: null,
                        MaintenanceMilliseconds: coldMaintenance.Milliseconds, MaintenanceStorageDeltaBytes: coldMaintenance.StorageDeltaBytes,
                        StorageAfterWritesBytes: storageAfterWrites, StorageBeforeIndexBytes: storageBeforeIndex,
                        FinalStorageBytes: DirectoryBytes(root), Advice: "not-run", Access: "TerraBase source scan",
                        IndexBuildTimings: "", IndexCatchUpTimings: ""));
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
                new QueryIndependentIndexPolicy { MaximumIndexes = maximumIndexBudget }).ConfigureAwait(false);
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
            if (vonEndpoint is not null)
            {
                var options = new JevAdvisorOptions
                {
                    Provider = "von", ModelVersion = "von-1.2.0", Endpoint = new Uri(vonEndpoint),
                    Deadline = TimeSpan.FromSeconds(30),
                    InputPricePerMillionTokensUsd = 0,
                };
                options.Validate();
                jev = new JevAutoIndexAdvisor(new JevSystemOneClient(new HttpClient(), options),
                    new JevTenantApprovalRegistry(), new JevApiCredentials(() => vonApiKey), options);
                advisor = jev;
            }

            var adviceWatch = Stopwatch.StartNew();
            var advice = await advisor.AdviseAsync(candidates).ConfigureAwait(false);
            adviceWatch.Stop();
            using var lifecycle = new QueryIndependentIndexLifecycleManager(runtime, scope);
            var buildWatch = Stopwatch.StartNew();
            var indexBuildTimings = new List<IndexPhaseTiming>();
            if (indexPerformance)
            {
                foreach (var recommendation in advice.Recommendations)
                {
                    var candidate = candidates.Candidates.First(item => item.Definition.CandidateId == recommendation.CandidateId);
                    var phase = PhaseProbe.Start();
                    await lifecycle.ApplyAsync(candidates, advice with
                    {
                        Recommendations = ImmutableArray.Create(recommendation),
                    }).ConfigureAwait(false);
                    var metrics = phase.Stop();
                    var activeRecord = runtime.Coordinator.Snapshot(scope, int.MaxValue).Indexes
                        .FirstOrDefault(item => item.CandidateId == recommendation.CandidateId
                            && item.Phase == AutomaticIndexPhase.Active && item.Visible);
                    if (activeRecord is not null)
                    {
                        indexBuildTimings.Add(new IndexPhaseTiming(candidate.Field.Path, metrics, null));
                    }
                }
            }
            else
            {
                await lifecycle.ApplyAsync(candidates, advice).ConfigureAwait(false);
            }

            buildWatch.Stop();
            jev?.Dispose();

            var maintenance = await UpdateTerrabaseMaintenanceAsync(source, tenant, collection, root, maintenanceDocuments).ConfigureAwait(false);
            var records = runtime.Coordinator.Snapshot(scope).Indexes;
            var active = records.Any(record => record.Phase == AutomaticIndexPhase.Active && record.Visible);
            var activeIndexCount = records.Count(record => record.Phase == AutomaticIndexPhase.Active && record.Visible);
            // A zero maintenance-update lane is the direct-probe diagnostic:
            // it leaves the just-built indexes at the seed cut, then measures
            // the physical index path repeatedly before the normal runtime
            // query repeats. Keep the existing one-probe catch-up diagnostic
            // for the post-maintenance lane.
            var indexCatchUpTimings = maintenanceDocuments == 0
                ? await MeasureIndexDirectProbesAsync(
                    runtime, tenant, collection, records, DirectProbeRepeatSamples).ConfigureAwait(false)
                : indexPerformance
                    ? await MeasureIndexCatchUpAsync(runtime, tenant, collection, records).ConfigureAwait(false)
                    : [];
            // The local Von service does not publish a token price. The Von
            // adapter intentionally configures a zero placeholder so no
            // unsupported dollar estimate is implied by its evidence.
            var reportedInferenceCost = vonEndpoint is not null || advice.Provider == "von"
                ? null
                : advice.Inference?.EstimatedInputCostUsd;
            var mode = advice.Provider + ":" + advice.ReasonCode
                + (advice.Inference is { } inference
                    ? $":{inference.RequestedModel}/{inference.ActualModel ?? "unknown"}"
                    : string.Empty);
            var measuredStrategy = strategy switch
            {
                "Von configured" when advice.Provider == "von" && advice.Inference is not null => "Von-assisted",
                "Von configured" => "Deterministic fallback after Von attempt",
                "Jev-assisted" when advice.Provider == "jev" && advice.Inference is not null => "Legacy JEV-assisted",
                "Jev-assisted" => "Deterministic fallback after legacy JEV attempt",
                _ => strategy,
            };
            var indexedMeasurements = new List<(EvaluationShape Shape, Measurement Measurement)>(EvaluationShapes.Length);
            foreach (var shape in EvaluationShapes)
            {
                // Diagnostic-only read-only explain and watermarks are captured
                // immediately before the first execution of each query shape.
                var diagnosticQuery = shape.Query(tenant, collection);
                await CaptureFreshnessDiagnosticAsync(
                    strategy, measuredStrategy, shape, candidates, advice,
                    runtime, scope, source, diagnosticQuery).ConfigureAwait(false);
                var indexedMeasurement = await MeasureTerrabaseAsync(
                    runtime, scope, diagnosticQuery, sourceExecutor, bounds, samples,
                    ExpectedIds(documentCount, shape)).ConfigureAwait(false);
                indexedMeasurements.Add((shape, indexedMeasurement));
            }

            // Capture the automatic-index diagnostics after query timing so
            // the inspection's quiescing/metadata persistence does not alter
            // the first-query or repeat samples. The whole-root length below
            // is captured from the same quiescent state. WAL and replacement
            // staging are subsets of physical lifecycle bytes; they are not
            // added to the logical document/entry total.
            var automaticIndexStorage = await InspectAutomaticIndexStorageAsync(runtime, records).ConfigureAwait(false);
            var finalStorageBytes = DirectoryBytes(root);
            var rows = new List<StrategyResult>(EvaluationShapes.Length);
            foreach (var (shape, indexedMeasurement) in indexedMeasurements)
            {
                rows.Add(new StrategyResult(
                    shape.Name, measuredStrategy, $"{maximumIndexBudget} secondary", indexedMeasurement, active, activeIndexCount, indexedMeasurement.Correct,
                    queryHistoryBeforeCandidates, writeWatch.Elapsed.TotalMilliseconds, profileWatch.Elapsed.TotalMilliseconds,
                    buildWatch.Elapsed.TotalMilliseconds, adviceWatch.Elapsed.TotalMilliseconds, reportedInferenceCost,
                    maintenance.Milliseconds, maintenance.StorageDeltaBytes, storageAfterWrites, storageBeforeIndex,
                    finalStorageBytes, mode,
                    AccessLabel(indexedMeasurement, "TerraBase automatic index", "TerraBase source scan (held-out predicate)"),
                    FormatPhaseTimings(indexBuildTimings), FormatPhaseTimings(indexCatchUpTimings), automaticIndexStorage,
                    indexBuildTimings.ToArray(), indexCatchUpTimings.ToArray(),
                    InferenceInputTokens: advice.Inference?.InputTokens,
                    InferenceOutputTokens: advice.Inference?.OutputTokens,
                    InferenceRequestedModel: advice.Inference?.RequestedModel,
                    InferenceActualModel: advice.Inference?.ActualModel));
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

    private static async ValueTask<AutomaticIndexStorageDiagnostics?> InspectAutomaticIndexStorageAsync(
        AutomaticIndexRuntime runtime,
        IReadOnlyList<AutomaticIndexRecord> records)
    {
        var activeRecords = records
            .Where(static record => record.Phase == AutomaticIndexPhase.Active && record.Visible)
            .OrderBy(static record => record.CandidateId, StringComparer.Ordinal)
            .ToArray();
        if (activeRecords.Length == 0)
        {
            return null;
        }

        ulong logicalDocumentEntryBytes = 0;
        ulong walBytes = 0;
        ulong walCompactionStagingBytes = 0;
        foreach (var record in activeRecords)
        {
            var storage = await runtime.Backend.InspectStorageAsync(record.CandidateId).ConfigureAwait(false);
            var logicalBytes = await runtime.Backend.Store(record.CandidateId)
                .InspectLogicalBytesAsync().ConfigureAwait(false);
            logicalDocumentEntryBytes = checked(logicalDocumentEntryBytes + logicalBytes);
            walBytes = checked(walBytes + storage.WalBytes);
            walCompactionStagingBytes = checked(walCompactionStagingBytes + storage.WalCompactionStagingBytes);
        }

        return new AutomaticIndexStorageDiagnostics(
            logicalDocumentEntryBytes,
            walBytes,
            walCompactionStagingBytes);
    }

    private static async Task<int> RunPointMirrorProbeAsync(
        int documentCount, int samples, int maximumIndexBudget, int physicalIndexBudgetMiB, string output)
    {
        var root = Path.Combine(Path.GetTempPath(), "terrabase-bench-point-mirror-" + Guid.NewGuid().ToString("N"));
        var tenant = new TenantId("benchmark-tenant");
        var collection = new CollectionId(Collection);
        var scope = new AutomaticIndexScope(tenant.Value, "benchmark-database", Collection);
        try
        {
            PointMirrorStoreDescriptor[] stores;
            ulong sourceCut;
            PhaseMetrics buildMetrics;
            int completedIndexes;
            // Keep the source, runtime, snapshot, and lifecycle manager alive
            // only through build and metadata capture. Their ZoneTree handles
            // must be closed before the physical generation is copied: WAL
            // files are opened exclusively by the durable index backend.
            {
                await using var source = new DurableStorageEngine(root);
                await SeedTerrabaseAsync(source, tenant, collection, documentCount).ConfigureAwait(false);
                var runtimeBudgetLedger = CreateRuntimeBudgetLedger(scope, physicalIndexBudgetMiB);
                await using var runtime = await AutomaticIndexRuntime.OpenAsync(
                    source,
                    new AutomaticIndexRuntimeOptions { DisablePageCache = true },
                    runtimeBudgetLedger).ConfigureAwait(false);

                // Candidate generation remains query-independent. The probe's
                // evaluation query is materialized only after the snapshot-backed
                // build is complete and its source cut is frozen.
                EnsureEmptyQueryHistory(runtime, "before point-mirror profiling and candidate generation");
                await using (var snapshot = await source.BeginSnapshotAsync().ConfigureAwait(false))
                {
                    var profile = await new CollectionIndexProfiler().ProfileAsync(source, snapshot, scope).ConfigureAwait(false);
                    var candidates = await new SchemaStatisticsCandidateSource().GetCandidatesAsync(
                        profile,
                        new QueryIndependentIndexPolicy { MaximumIndexes = maximumIndexBudget }).ConfigureAwait(false);
                    EnsureEmptyQueryHistory(runtime, "after point-mirror candidate generation");
                    var advice = await new SchemaStatisticsAdvisor().AdviseAsync(candidates).ConfigureAwait(false);
                    using (var lifecycle = new QueryIndependentIndexLifecycleManager(runtime, scope))
                    {
                        var buildPhase = PhaseProbe.Start();
                        await lifecycle.ApplyAsync(candidates, advice).ConfigureAwait(false);
                        buildMetrics = buildPhase.Stop();
                    }
                }

                sourceCut = source.ChangeFeedCursor.CommitSequence;
                var records = runtime.Coordinator.Snapshot(scope, int.MaxValue).Indexes
                    .Where(static record => record.Phase == AutomaticIndexPhase.Active && record.Visible)
                    .OrderBy(static record => record.Candidate.Fields[0], StringComparer.Ordinal)
                    .ToArray();
                if (records.Length == 0)
                {
                    throw new InvalidOperationException("The point-mirror probe produced no active visible index.");
                }

                stores = records
                    .Select(record =>
                    {
                        var shape = EvaluationShapes.FirstOrDefault(item => item.Path == record.Candidate.Fields[0]);
                        if (shape is null)
                        {
                            return null;
                        }

                        var builtStore = runtime.Backend.Store(record.CandidateId);
                        if (record.TargetPosition != sourceCut || record.AppliedPosition != sourceCut)
                        {
                            throw new InvalidOperationException(
                                $"The completed snapshot build for '{shape.Name}' does not cover the frozen source cut {sourceCut}: target={record.TargetPosition?.ToString(CultureInfo.InvariantCulture) ?? "null"}, applied={record.AppliedPosition}.");
                        }

                        if (builtStore.Watermark.CommitSequence != sourceCut)
                        {
                            throw new InvalidOperationException(
                                $"The completed snapshot index for '{shape.Name}' is at commit {builtStore.Watermark.CommitSequence}, expected frozen cut {sourceCut}.");
                        }

                        return new PointMirrorStoreDescriptor(
                            shape,
                            builtStore.StoragePath,
                            builtStore.Definition);
                    })
                    .Where(static descriptor => descriptor is not null)
                    .Select(static descriptor => descriptor!)
                    .ToArray();
                completedIndexes = records.Length;
            }

            // All source and runtime handles are now closed, so this copy is
            // taken from a quiescent physical generation. This preserves the
            // completed source cut while avoiding live WAL sharing.
            var rows = new List<ProbeBranchResult>(stores.Length * 2);
            foreach (var store in stores)
            {
                // Reopen two byte-identical copies of the completed physical
                // generation. This keeps document decode and iterator caches
                // independent while preserving the exact same build and cut.
                var copiesRoot = Path.Combine(root, "point-mirror-copies", store.Shape.Path.Replace('.', '-'));
                var pointPath = Path.Combine(copiesRoot, "point-mirror");
                var genericPath = Path.Combine(copiesRoot, "generic-entry-read");
                CopyDirectory(store.StoragePath, pointPath);
                CopyDirectory(store.StoragePath, genericPath);
                await using var pointStore = new DurableIndexStore(pointPath, store.Definition);
                await using var genericStore = new DurableIndexStore(genericPath, store.Definition);
                if (pointStore.Watermark.CommitSequence != sourceCut
                    || genericStore.Watermark.CommitSequence != sourceCut)
                {
                    throw new InvalidOperationException(
                        $"Copied '{store.Shape.Name}' stores did not retain the completed snapshot cut {sourceCut}.");
                }

                var query = new IndexRuntimeQuery(
                    tenant,
                    collection,
                    [IndexPredicate.Equal(store.Shape.Path, store.Shape.Value)],
                    projection: ["value"],
                    requiredCommitSequence: sourceCut,
                    resultLimit: ResultLimit);
                var bounds = IndexRuntimeExecutionBounds.ForPage(
                    ResultLimit,
                    afterDocumentId: null,
                    QueryScanBudget,
                    QueryMemoryBudgetBytes,
                    QueryWorkBudget);
                var expected = ExpectedIds(documentCount, store.Shape);
                Func<Document, bool> verify = document =>
                    document.Key.Tenant == tenant
                    && document.Key.Collection == collection
                    && RuntimeIndexCodec.Matches(document, query.Predicates);
                Func<ValueTask<IndexRuntimeCandidateExecution?>> pointExecute = () =>
                    ValueTask.FromResult(pointStore.TryPointSeek(
                        query,
                        bounds,
                        verify,
                        new IndexRuntimeExecutionBudget(bounds),
                        sourceCut));
                Func<ValueTask<IndexRuntimeCandidateExecution?>> genericExecute = async () =>
                    await genericStore.ExecuteBoundedAsync(
                        query,
                        bounds,
                        verify,
                        new IndexRuntimeExecutionBudget(bounds),
                        sourceCut).ConfigureAwait(false);

                var point = await MeasureProbeBranchAsync(
                    store.Shape.Name, "Completed snapshot point mirror", samples, sourceCut, expected, pointExecute).ConfigureAwait(false);
                var generic = await MeasureProbeBranchAsync(
                    store.Shape.Name, "Generic ExecuteBoundedEntryRead", samples, sourceCut, expected, genericExecute).ConfigureAwait(false);
                var equivalent = point.ResultDigest == generic.ResultDigest
                    && point.FirstResultCount == generic.FirstResultCount
                    && point.RepeatResultCount == generic.RepeatResultCount;
                rows.Add(point with { EquivalentResults = equivalent });
                rows.Add(generic with { EquivalentResults = equivalent });
            }

            if (rows.Count == 0)
            {
                throw new InvalidOperationException("The point-mirror probe found no supported evaluation shape.");
            }

            var markdown = RenderPointMirrorProbe(
                documentCount,
                samples,
                maximumIndexBudget,
                physicalIndexBudgetMiB,
                sourceCut,
                completedIndexes,
                buildMetrics,
                rows);
            var fullOutput = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            await File.WriteAllTextAsync(fullOutput, markdown).ConfigureAwait(false);
            var rawReceiptPath = Path.Combine(Path.GetDirectoryName(fullOutput)!, "point-mirror-probe-receipt.json");
            var rawReceipt = new PointMirrorRawReceipt(
                SchemaVersion: 1,
                GeneratedAtUtc: DateTimeOffset.UtcNow,
                Documents: documentCount,
                RepeatSamples: samples,
                MaximumIndexBudget: maximumIndexBudget,
                PhysicalIndexBudgetMiB: physicalIndexBudgetMiB,
                SourceCommitSequence: sourceCut,
                CompletedIndexes: completedIndexes,
                BuildMetrics: buildMetrics,
                SummaryFile: Path.GetFileName(fullOutput),
                SummarySha256: Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullOutput).ConfigureAwait(false))).ToLowerInvariant(),
                Rows: rows);
            await File.WriteAllTextAsync(rawReceiptPath, JsonSerializer.Serialize(rawReceipt, RawJsonOptions) + Environment.NewLine).ConfigureAwait(false);
            Console.Write(markdown);
            Console.WriteLine($"Summary: {fullOutput}");
            Console.WriteLine($"Raw receipt: {rawReceiptPath}");
            return rows.All(static row => row.EquivalentResults) ? 0 : 1;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<ProbeBranchResult> MeasureProbeBranchAsync(
        string shape,
        string branch,
        int samples,
        ulong sourceCut,
        IReadOnlyList<string> expected,
        Func<ValueTask<IndexRuntimeCandidateExecution?>> execute)
    {
        var firstPhase = PhaseProbe.Start();
        var first = await execute().ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The {branch} lane could not serve '{shape}'.");
        var firstMetrics = firstPhase.Stop();
        VerifyProbeExecution(first, expected, sourceCut, $"{branch} first {shape}");
        var firstDigest = DigestIds(first.Documents);

        var latencies = new long[samples];
        var repeatEntryReads = 0UL;
        var repeatFullDocumentReads = 0UL;
        var repeatPhysicalWorkAvailable = true;
        var repeatResultCount = 0;
        var repeatPhase = PhaseProbe.Start();
        for (var index = 0; index < samples; index++)
        {
            var watch = Stopwatch.StartNew();
            var repeat = await execute().ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The {branch} lane could not serve '{shape}' repeat {index}.");
            watch.Stop();
            VerifyProbeExecution(repeat, expected, sourceCut, $"{branch} repeat {index} {shape}");
            latencies[index] = watch.ElapsedTicks;
            repeatResultCount = repeat.Documents.Count;
            if (repeat.PhysicalWork is { } physicalWork)
            {
                repeatEntryReads = checked(repeatEntryReads + physicalWork.EntryReads);
                repeatFullDocumentReads = checked(repeatFullDocumentReads + physicalWork.FullDocumentReads);
            }
            else
            {
                repeatPhysicalWorkAvailable = false;
            }
        }

        var repeatMetrics = repeatPhase.Stop();
        var rawRepeatLatencyMicroseconds = latencies.Select(Microseconds).ToArray();
        Array.Sort(latencies);
        return new ProbeBranchResult(
            shape,
            branch,
            firstMetrics.ElapsedMilliseconds * 1_000,
            Microseconds(latencies[BenchmarkPercentile.NearestRankIndex(samples, 0.50)]),
            Microseconds(latencies[BenchmarkPercentile.NearestRankIndex(samples, 0.95)]),
            firstMetrics.CpuMilliseconds,
            repeatMetrics.CpuMilliseconds / samples,
            firstMetrics.AllocatedBytes,
            (double)repeatMetrics.AllocatedBytes / samples,
            first.PhysicalWork?.EntryReads,
            repeatPhysicalWorkAvailable ? (double)repeatEntryReads / samples : null,
            first.PhysicalWork?.FullDocumentReads,
            repeatPhysicalWorkAvailable ? (double)repeatFullDocumentReads / samples : null,
            first.Documents.Count,
            repeatResultCount,
            firstDigest,
            EquivalentResults: false,
            first.Access,
            first.Reason,
            rawRepeatLatencyMicroseconds);
    }

    private static void VerifyProbeExecution(
        IndexRuntimeCandidateExecution execution,
        IReadOnlyList<string> expected,
        ulong sourceCut,
        string phase)
    {
        if (execution.SourceCommitSequence != sourceCut)
        {
            throw new InvalidOperationException(
                $"{phase} used source cut {execution.SourceCommitSequence}, expected frozen cut {sourceCut}.");
        }

        if (execution.UsedSnapshotFallback || execution.Access == IndexAccessMethod.Scan)
        {
            throw new InvalidOperationException($"{phase} did not use an index path: {execution.Reason}");
        }

        VerifyIds(execution.Documents.Select(static document => document.Key.DocumentId), expected, phase);
    }

    private static string DigestIds(IReadOnlyList<Document> documents)
    {
        var canonical = string.Join('\n', documents
            .Select(static document => document.Key.DocumentId)
            .Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"The completed index generation does not exist: {source}");
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string RenderPointMirrorProbe(
        int documents,
        int samples,
        int maximumIndexBudget,
        int physicalIndexBudgetMiB,
        ulong sourceCut,
        int completedIndexes,
        PhaseMetrics buildMetrics,
        IReadOnlyList<ProbeBranchResult> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Query-independent direct index probe");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Generated: {DateTimeOffset.UtcNow:O}; documents: {documents} + {SeedMaintenanceDocuments} seeded maintenance documents; post-build writes: 0; repeat samples: {samples}; result limit: {ResultLimit}.");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"The probe builds {completedIndexes} active visible snapshot-backed index(es) with MaximumIndexes={maximumIndexBudget}, freezes source commit {sourceCut}, and copies each completed physical generation byte-for-byte into independent point-mirror and generic-entry-read stores. Both branches then execute the same projected equality query and bounded document verification against that frozen cut.");
        if (physicalIndexBudgetMiB > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Automatic-index retained and transient physical caps are {physicalIndexBudgetMiB} MiB per collection and tenant; default logical-byte and non-storage budget dimensions are unchanged.");
        }
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Snapshot build phase: {buildMetrics.ElapsedMilliseconds:F1} ms, CPU {buildMetrics.CpuMilliseconds:F1} ms, RSS Δ {buildMetrics.WorkingSetDeltaBytes} B, alloc Δ {buildMetrics.AllocatedBytes} B. EntryReads and FullDocumentReads are logical diagnostic receipts supplied by the candidate executor; latency, CPU, RSS, and allocation deltas are process-wide observations.");
        builder.AppendLine();
        builder.AppendLine("| Shape | Branch | First µs | Repeat p50 µs | Repeat p95 µs | First CPU ms | Repeat CPU ms/op | First alloc B | Repeat alloc B/op | First EntryReads | Repeat EntryReads/op | First FullDocumentReads | Repeat FullDocumentReads/op | First rows | Repeat rows | Equivalent result | Result digest | Access | Reason |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | --- | --- |");
        foreach (var row in rows)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Shape} | {row.Branch} | {row.FirstMicroseconds:F1} | {row.RepeatP50Microseconds:F1} | {row.RepeatP95Microseconds:F1} | {row.FirstCpuMilliseconds:F1} | {row.RepeatCpuMillisecondsPerOperation:F3} | {row.FirstAllocatedBytes} | {row.RepeatAllocatedBytesPerOperation:F1} | {FormatProbeCounter(row.FirstEntryReads)} | {FormatProbeCounter(row.RepeatEntryReadsPerOperation)} | {FormatProbeCounter(row.FirstFullDocumentReads)} | {FormatProbeCounter(row.RepeatFullDocumentReadsPerOperation)} | {row.FirstResultCount} | {row.RepeatResultCount} | {row.EquivalentResults} | `{row.ResultDigest}` | {row.Access} | {row.Reason.Replace("|", "\\|", StringComparison.Ordinal)} |");
        }

        builder.AppendLine();
        builder.AppendLine("Timing semantics: each branch uses an independently reopened copy of the same completed snapshot generation, so its first call and repeats have independent iterator and document-decode caches. The repeat percentile excludes the first call. This opt-in diagnostic isolates the physical point mirror from the generic bounded entry iterator; it is not a production optimization claim.");
        builder.AppendLine("Repeat p50 and p95 use the nearest-rank definition (`ceil(sampleCount × percentile) - 1`); raw repeat latency samples are preserved in `point-mirror-probe-receipt.json`.");
        return builder.ToString();
    }

    private static string FormatProbeCounter(ulong? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";

    private static string FormatProbeCounter(double? value) =>
        value?.ToString("F1", CultureInfo.InvariantCulture) ?? "—";

    private sealed record PointMirrorStoreDescriptor(
        EvaluationShape Shape,
        string StoragePath,
        IndexDefinition Definition);

    private sealed record ProbeBranchResult(
        string Shape,
        string Branch,
        double FirstMicroseconds,
        double RepeatP50Microseconds,
        double RepeatP95Microseconds,
        double FirstCpuMilliseconds,
        double RepeatCpuMillisecondsPerOperation,
        long FirstAllocatedBytes,
        double RepeatAllocatedBytesPerOperation,
        ulong? FirstEntryReads,
        double? RepeatEntryReadsPerOperation,
        ulong? FirstFullDocumentReads,
        double? RepeatFullDocumentReadsPerOperation,
        int FirstResultCount,
        int RepeatResultCount,
        string ResultDigest,
        bool EquivalentResults,
        IndexAccessMethod Access,
        string Reason,
        double[] RepeatLatencyMicroseconds);

    private static async Task<Measurement> MeasureTerrabaseAsync(
        AutomaticIndexRuntime runtime, AutomaticIndexScope scope, IndexRuntimeQuery query,
        IndexRuntimeSourceQueryExecutor sourceExecutor, IndexRuntimeExecutionBounds bounds, int samples,
        IReadOnlyList<string> expected)
    {
        var firstPhase = PhaseProbe.Start();
        var first = await runtime.QueryBoundedAsync(scope, query, bounds, sourceExecutor.ExecuteAsync).ConfigureAwait(false);
        var firstMetrics = firstPhase.Stop();
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
            if (IndexServed(result))
            {
                repeatIndexServed++;
            }
        }

        var rawRepeatLatencyMicroseconds = latencies.Select(Microseconds).ToArray();
        Array.Sort(latencies);
        return new Measurement(
            firstMetrics.ElapsedMilliseconds * 1_000, Microseconds(latencies[BenchmarkPercentile.NearestRankIndex(samples, 0.50)]),
            Microseconds(latencies[BenchmarkPercentile.NearestRankIndex(samples, 0.95)]), first.Execution.Measurement.RowsExamined,
            (double)rows / samples, first.Documents.Count, repeatCount, Correct: true,
            FirstIndexServed: IndexServed(first),
            RepeatIndexServedFraction: (double)repeatIndexServed / samples,
            FirstQueryMetrics: firstMetrics,
            RepeatLatencyMicroseconds: rawRepeatLatencyMicroseconds);
    }

    // A fallback retains the selected candidate in Plan.IndexName for
    // explainability, so the execution receipt is the source of truth for
    // which branch supplied this call.
    private static bool IndexServed(QueryPlannerResult result) =>
        result.Plan.IndexName.Length > 0
        && !result.Execution.UsedSnapshotFallback
        && result.Execution.Access != IndexAccessMethod.Scan;

    private static async Task<IReadOnlyList<IndexPhaseTiming>> MeasureIndexCatchUpAsync(
        AutomaticIndexRuntime runtime, TenantId tenant, CollectionId collection,
        IReadOnlyList<AutomaticIndexRecord> records)
    {
        var timings = new List<IndexPhaseTiming>();
        foreach (var record in records
                     .Where(static item => item.Phase == AutomaticIndexPhase.Active && item.Visible)
                     .OrderBy(static item => item.CandidateId, StringComparer.Ordinal))
        {
            var path = record.Candidate.Fields[0];
            var shape = EvaluationShapes.FirstOrDefault(item => item.Path == path);
            if (shape is null)
            {
                continue;
            }

            var phase = PhaseProbe.Start();
            var work = await runtime.MeasureOnIndexAsync(
                record.CandidateId, shape.Query(tenant, collection)).ConfigureAwait(false);
            var metrics = phase.Stop();
            if (work is null)
            {
                throw new InvalidOperationException(
                    $"The active index '{path}' could not execute its catch-up probe.");
            }

            timings.Add(new IndexPhaseTiming(path, metrics,
                new DirectProbeMeasurement(
                    metrics.ElapsedMilliseconds * 1_000,
                    RepeatP50Microseconds: null,
                    RepeatP95Microseconds: null,
                    RepeatSamples: 0,
                    RepeatLatencyMicroseconds: [])));
        }

        return timings;
    }

    private static async Task<IReadOnlyList<IndexPhaseTiming>> MeasureIndexDirectProbesAsync(
        AutomaticIndexRuntime runtime, TenantId tenant, CollectionId collection,
        IReadOnlyList<AutomaticIndexRecord> records, int repeatSamples)
    {
        var timings = new List<IndexPhaseTiming>();
        foreach (var record in records
                     .Where(static item => item.Phase == AutomaticIndexPhase.Active && item.Visible)
                     .OrderBy(static item => item.CandidateId, StringComparer.Ordinal))
        {
            var path = record.Candidate.Fields[0];
            var shape = EvaluationShapes.FirstOrDefault(item => item.Path == path);
            if (shape is null)
            {
                continue;
            }

            var firstPhase = PhaseProbe.Start();
            var first = await runtime.MeasureOnIndexAsync(
                record.CandidateId, shape.Query(tenant, collection)).ConfigureAwait(false);
            var firstMetrics = firstPhase.Stop();
            if (first is null)
            {
                throw new InvalidOperationException(
                    $"The active index '{path}' could not execute its direct probe.");
            }

            var latencies = new long[repeatSamples];
            for (var index = 0; index < repeatSamples; index++)
            {
                var watch = Stopwatch.StartNew();
                var repeat = await runtime.MeasureOnIndexAsync(
                    record.CandidateId, shape.Query(tenant, collection)).ConfigureAwait(false);
                watch.Stop();
                if (repeat is null)
                {
                    throw new InvalidOperationException(
                        $"The active index '{path}' could not execute direct probe repeat {index}.");
                }

                latencies[index] = watch.ElapsedTicks;
            }

            var rawRepeatLatencyMicroseconds = latencies.Select(Microseconds).ToArray();
            Array.Sort(latencies);
            timings.Add(new IndexPhaseTiming(path, firstMetrics,
                new DirectProbeMeasurement(
                    firstMetrics.ElapsedMilliseconds * 1_000,
                    Microseconds(latencies[BenchmarkPercentile.NearestRankIndex(repeatSamples, 0.50)]),
                    Microseconds(latencies[BenchmarkPercentile.NearestRankIndex(repeatSamples, 0.95)]),
                    repeatSamples,
                    rawRepeatLatencyMicroseconds)));
        }

        return timings;
    }

    private static async Task<SqliteComparison> RunSqliteComparisonAsync(
        int documentCount, int samples, int maximumIndexBudget, int maintenanceDocuments)
    {
        var root = Path.Combine(Path.GetTempPath(), "terrabase-bench-query-independent-sqlite-" + Guid.NewGuid().ToString("N"));
        try
        {
            var noIndex = await RunSqliteLaneAsync(
                Path.Combine(root, "no-index"), documentCount, samples, explicitIndexCount: 0, maintenanceDocuments).ConfigureAwait(false);
            var explicitLane = await RunSqliteLaneAsync(
                Path.Combine(root, "explicit-index"), documentCount, samples, maximumIndexBudget, maintenanceDocuments).ConfigureAwait(false);
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
        string root, int documentCount, int samples, int explicitIndexCount, int maintenanceDocuments)
    {
        var databasePath = Path.Combine(root, "comparison.sqlite");
        Directory.CreateDirectory(root);
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        // Keep the SQLite comparator durable while declaring the cache and
        // temporary storage budget used by both SQLite lanes. These are
        // process settings, not a hard process-wide resource cap.
        await ExecuteSqliteAsync(connection, $"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-{SqliteCacheSizeKiB}; PRAGMA mmap_size={SqliteMmapSizeBytes};");
        await ExecuteSqliteAsync(connection, "CREATE TABLE users (id TEXT PRIMARY KEY, body BLOB NOT NULL);");

        var writeWatch = Stopwatch.StartNew();
        await SeedSqliteAsync(connection, documentCount).ConfigureAwait(false);
        writeWatch.Stop();
        var storageAfterWrites = DirectoryBytes(root);

        var buildWatch = Stopwatch.StartNew();
        for (var index = 0; index < explicitIndexCount; index++)
        {
            var shape = EvaluationShapes[index];
            await ExecuteSqliteAsync(connection,
                $"CREATE INDEX {shape.SqliteIndexName} ON users(json_extract(body, '$.{shape.Path}')); ");
        }

        buildWatch.Stop();
        var maintenance = await UpdateSqliteMaintenanceAsync(connection, root, maintenanceDocuments).ConfigureAwait(false);
        var finalStorage = DirectoryBytes(root);
        var rows = new List<StrategyResult>(EvaluationShapes.Length);
        var plans = new string[EvaluationShapes.Length];
        for (var shapeIndex = 0; shapeIndex < EvaluationShapes.Length; shapeIndex++)
        {
            var shape = EvaluationShapes[shapeIndex];
            plans[shapeIndex] = await ExplainSqliteAsync(connection, shape).ConfigureAwait(false);
            var indexServed = plans[shapeIndex].Contains(shape.SqliteIndexName, StringComparison.OrdinalIgnoreCase);
            if (shapeIndex < explicitIndexCount && !indexServed)
            {
                throw new InvalidOperationException(
                    $"SQLite did not use the explicit {shape.Name} index. EXPLAIN QUERY PLAN: {plans[shapeIndex]}");
            }

            var measurement = await MeasureSqliteAsync(connection, shape, samples,
                ExpectedIds(documentCount, shape), indexServed).ConfigureAwait(false);
            rows.Add(new StrategyResult(
                shape.Name,
                explicitIndexCount > 0 ? "SQLite tuned explicit JSONB indexes" : "SQLite tuned no secondary index",
                $"{explicitIndexCount} secondary",
                measurement,
                ActiveIndex: indexServed,
                ActiveIndexCount: explicitIndexCount,
                measurement.Correct,
                QueryHistoryBeforeCandidates: null,
                InitialIngestMilliseconds: writeWatch.Elapsed.TotalMilliseconds,
                ProfileMilliseconds: 0,
                BuildMilliseconds: explicitIndexCount > 0 ? buildWatch.Elapsed.TotalMilliseconds : 0,
                InferenceMilliseconds: 0,
                InferenceCostUsd: 0,
                MaintenanceMilliseconds: maintenance.Milliseconds,
                MaintenanceStorageDeltaBytes: maintenance.StorageDeltaBytes,
                StorageAfterWritesBytes: storageAfterWrites,
                StorageBeforeIndexBytes: storageAfterWrites,
                FinalStorageBytes: finalStorage,
                Advice: "comparator",
                Access: indexServed
                    ? $"SQLite {shape.SqliteIndexName} (EXPLAIN QUERY PLAN)"
                    : "SQLite table scan (EXPLAIN QUERY PLAN)",
                IndexBuildTimings: explicitIndexCount > 0
                    ? $"explicit indexes aggregate ({explicitIndexCount}): {buildWatch.Elapsed.TotalMilliseconds:F1} ms"
                    : "",
                IndexCatchUpTimings: "not-applicable"));
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

        var firstPhase = PhaseProbe.Start();
        var first = await QuerySqliteIdsAsync(query).ConfigureAwait(false);
        var firstMetrics = firstPhase.Stop();
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

        var rawRepeatLatencyMicroseconds = latencies.Select(Microseconds).ToArray();
        Array.Sort(latencies);
        return new Measurement(
            firstMetrics.ElapsedMilliseconds * 1_000, Microseconds(latencies[BenchmarkPercentile.NearestRankIndex(samples, 0.50)]),
            Microseconds(latencies[BenchmarkPercentile.NearestRankIndex(samples, 0.95)]), null, null,
            first.Count, repeatCount, Correct: true, FirstIndexServed: indexServed,
            RepeatIndexServedFraction: indexServed ? 1 : 0,
            FirstQueryMetrics: firstMetrics,
            RepeatLatencyMicroseconds: rawRepeatLatencyMicroseconds);
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

    private static InMemoryAutomaticIndexBudgetLedger? CreateRuntimeBudgetLedger(
        AutomaticIndexScope scope,
        int physicalIndexBudgetMiB)
    {
        if (physicalIndexBudgetMiB == 0)
        {
            return null;
        }

        var capBytes = checked((ulong)physicalIndexBudgetMiB * 1024 * 1024);
        var ledger = new InMemoryAutomaticIndexBudgetLedger();
        ledger.SetTenantBudget(scope.Tenant,
            WithPhysicalByteCap(InMemoryAutomaticIndexBudgetLedger.DefaultTenantBudget, capBytes));
        ledger.SetCollectionBudget(scope,
            WithPhysicalByteCap(InMemoryAutomaticIndexBudgetLedger.DefaultCollectionBudget, capBytes));
        return ledger;
    }

    private static AutomaticIndexBudget WithPhysicalByteCap(AutomaticIndexBudget defaults, ulong capBytes) => new(
        cpuUnits: defaults.CpuUnits,
        memoryBytes: defaults.MemoryBytes,
        ioUnits: defaults.IoUnits,
        maxVisibleIndexes: defaults.MaxVisibleIndexes,
        maxStorageBytes: capBytes,
        maxTransientBytes: capBytes,
        maxWriteAmplification: defaults.MaxWriteAmplification,
        maxLogicalStorageBytes: defaults.MaxLogicalStorageBytes);

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

        var maintenance = new StorageMutation[SeedMaintenanceDocuments];
        for (var index = 0; index < SeedMaintenanceDocuments; index++)
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

        await SeedSqliteBatchAsync(connection, 0, SeedMaintenanceDocuments, maintenance: true).ConfigureAwait(false);
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
        DurableStorageEngine source, TenantId tenant, CollectionId collection, string root,
        int maintenanceDocuments)
    {
        var before = DirectoryBytes(root);
        var watch = Stopwatch.StartNew();
        var mutations = new StorageMutation[maintenanceDocuments];
        for (var index = 0; index < maintenanceDocuments; index++)
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

    private static async Task<MaintenanceResult> UpdateSqliteMaintenanceAsync(
        SqliteConnection connection, string root, int maintenanceDocuments)
    {
        var before = DirectoryBytes(root);
        var watch = Stopwatch.StartNew();
        await ExecuteSqliteAsync(connection, "BEGIN;");
        await using (var insert = new SqliteCommand(
            "INSERT INTO users (id, body) VALUES (@id, jsonb(@body)) ON CONFLICT(id) DO UPDATE SET body = excluded.body;", connection))
        {
            var id = insert.Parameters.Add("@id", SqliteType.Text);
            var body = insert.Parameters.Add("@body", SqliteType.Text);
            for (var index = 0; index < maintenanceDocuments; index++)
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

    private static string[] ExpectedIds(int documentCount, EvaluationShape shape)
    {
        var expected = Enumerable.Range(0, documentCount)
            .Where(shape.Matches)
            .Select(index => $"doc-{index:D6}");
        expected = expected.Concat(Enumerable.Range(0, SeedMaintenanceDocuments)
            .Where(shape.Matches)
            .Select(index => $"doc-maint-{index:D6}"));

        return expected.Order(StringComparer.Ordinal).ToArray();
    }

    private static string DocumentJson(int index, int valueOffset = 0) =>
        $"{{\"status\":\"s{index % 100:D2}\",\"profile\":{{\"segment\":\"g{index % 100:D2}\"}},\"value\":{(index + valueOffset) % 5}}}";

    private static string Render(
        int documents, int samples, int maximumIndexBudget, int maintenanceDocuments,
        int physicalIndexBudgetMiB,
        bool indexPerformance, IReadOnlyList<StrategyResult> rows, SqliteComparison sqlite,
        bool jevRequested, bool jevApproved, bool vonRequested)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Query-independent indexing comparison");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Generated: {DateTimeOffset.UtcNow:O}; documents: {documents} + {SeedMaintenanceDocuments} seeded maintenance documents; post-build updates: {maintenanceDocuments}; repeat samples: {samples}; evaluation queries: `{StatusPath} = {EvaluationStatus}` and `{NestedSegmentPath} = {EvaluationSegment}`; result limit: {ResultLimit}.");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Initial seed and maintenance updates use {TransactionBatchSize}-document transactions in both TerraBase and SQLite; the final seed batch may contain fewer documents when the requested document count is not divisible by the batch size.");
        builder.AppendLine("SQLite no-index and explicit-index lanes use separately seeded equivalent databases; each lane applies its own post-build maintenance batch from the same initial document state.");
        builder.AppendLine("The evaluation query object and held-out expected IDs are materialized only after candidate selection, advisor decision, and lifecycle application in indexed TerraBase lanes.");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Declared settings: MaximumIndexes={maximumIndexBudget}; transaction batch={TransactionBatchSize}; query scan budget={QueryScanBudget}; query work budget={QueryWorkBudget}; query memory budget={QueryMemoryBudgetBytes} bytes; SQLite page cache={SqliteCacheSizeKiB} KiB; SQLite mmap={SqliteMmapSizeBytes} bytes.");
        if (physicalIndexBudgetMiB == 0)
        {
            builder.AppendLine("All TerraBase arms use the same default automatic-index admission budgets: 1 MiB per collection and 4 MiB per tenant for retained physical storage, 512 KiB per collection and 2 MiB per tenant for transient storage, and 1 MiB per collection and 4 MiB per tenant for retained logical bytes. These are not SQLite resource caps; SQLite is a tuned comparator with the separately declared cache and mmap settings.");
        }
        else
        {
            var capBytes = checked((long)physicalIndexBudgetMiB * 1024 * 1024);
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"All TerraBase arms use the same explicit automatic-index caps: {physicalIndexBudgetMiB} MiB ({capBytes} bytes) per collection and per tenant for retained physical storage and transient storage. Default logical storage limits (1 MiB per collection and 4 MiB per tenant), CPU, memory, I/O, visible-index count, and write-amplification limits are retained. SQLite is a tuned comparator with the separately declared cache and mmap settings, not subject to TerraBase admission caps.");
        }
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Index-count constraint: TerraBase policy MaximumIndexes={maximumIndexBudget} with no evaluation-field hint. The tuned SQLite oracle creates exactly {maximumIndexBudget} expression index(es) over the evaluation paths in deterministic shape order, so this lane has held-out-field foreknowledge and is not a query-independent peer. SQLite uses WAL with synchronous=FULL, temp_store=MEMORY, and the declared cache/mmap settings; its table primary key remains present in both lanes.");
        builder.AppendLine("SQLite stores the same document shape as TerraBase in a JSONB body and queries it with json_extract; only the relational primary key is separate. TerraBase candidate generation receives no query history or preferred paths. Declared cache, mmap, and query budgets are reproducibility settings, not hard process-wide CPU, memory, I/O, or storage caps.");
        builder.AppendLine();
        var directProbeLane = maintenanceDocuments == 0 || indexPerformance;
        var firstQueryHeader = directProbeLane
            ? "First query µs (per shape; indexed rows after direct probe)"
            : "First query µs (per shape)";
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"| Shape | Strategy | Index budget | Active indexes | {firstQueryHeader} | First query CPU ms | First query RSS Δ | First query alloc B | Repeat p50 µs | Repeat p95 µs | First rows | Repeat rows | Access | First index served | Repeat index served | Result coverage | Query history before candidates | Initial ingest ms | Profile ms | Build ms | Index build phase detail | Inference ms | Inference input tokens | Inference output tokens | Estimated inference input cost USD | Maintenance ms | Catch-up + index probe phases | Maintenance storage Δ | Whole-root file bytes after writes | Whole-root file bytes before index | Whole-root final file bytes | Whole-root index storage Δ | Automatic-index logical document/entry B | Automatic-index recognized WAL B | Automatic-index WAL compaction staging B | Advice |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        var allRows = rows.Concat(sqlite.NoIndex).Concat(sqlite.ExplicitIndex).ToArray();
        foreach (var row in allRows)
        {
            AppendRow(builder, row, indexPerformance);
        }

        builder.AppendLine();
        AppendRegressionSection(builder, allRows, indexPerformance);
        builder.AppendLine();
        for (var shapeIndex = 0; shapeIndex < EvaluationShapes.Length; shapeIndex++)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"SQLite {EvaluationShapes[shapeIndex].Name} no-index EXPLAIN QUERY PLAN: `{sqlite.NoIndexPlans[shapeIndex]}`");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"SQLite {EvaluationShapes[shapeIndex].Name} explicit-index EXPLAIN QUERY PLAN: `{sqlite.ExplicitPlans[shapeIndex]}`");
        }
        builder.AppendLine("Timing semantics: `First query µs` is the first call for that predicate, not a percentile or minimum. Each strategy evaluates `status` first, then runs its repeats before evaluating `nested profile.segment`; therefore the nested predicate's first call is warmed by the earlier status queries and is not an engine-cold measurement. Repeat percentiles use later calls for the same predicate; the first call is excluded from repeat statistics.");
        builder.AppendLine("SQLite runs EXPLAIN QUERY PLAN and prepares commands before timing its first Execute. TerraBase's query planning occurs inside the timed runtime execution, so cross-engine first-call times include different planning work. SQLite file bytes are captured before timed queries, while TerraBase final file bytes are captured after query measurements and quiescent index inspection; cross-engine storage figures are not phase-matched.");
        builder.AppendLine("Repeat p50 and p95 use the nearest-rank definition (`ceil(sampleCount × percentile) - 1`); raw repeat latency samples are preserved in `query-independent-indexing-receipt.json`.");
        builder.AppendLine("The compact `run-receipt.json` indexes the raw JSON receipt by SHA-256; the raw receipt also stores the SHA-256 of this Markdown summary so the two evidence files can be checked together.");
        builder.AppendLine("Inference model versions and input/output tokens come from the advisor's returned evidence. Von cost is `unpriced` because this local service has no configured token price; raw receipts do not substitute the adapter's zero placeholder for a price. Inference time is client-side advisor wall time, and Von server CPU/RSS are outside this process's measurements.");
        builder.AppendLine("The deterministic, Von, and legacy JEV lanes assert `AutomaticWorkloadAnalyzer.ObservationCount == 0` immediately before profiling/candidate generation and again after candidate generation. Storage values are summed logical file lengths from `FileInfo.Length` for each lane's database and lifecycle/index files; index storage Δ is final storage minus storage before index. They exclude allocated disk-block accounting, so sparse or preallocated WAL files can differ from physical disk usage and these values do not establish physical storage superiority.");
        builder.AppendLine("Automatic-index logical document/entry bytes are the aggregate encoded key/value bytes returned by `InspectLogicalBytesAsync` across active visible indexes. Recognized WAL and WAL-compaction staging bytes are diagnostic subsets returned by `InspectStorageAsync`; `N/A` means the lane has no automatic-index store. These fields do not measure complete segment-compaction cost or total historical compaction I/O.");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Initial ingest ms is measured before any secondary index exists. The fixed {SeedMaintenanceDocuments} maintenance documents are always seeded; `--maintenance-documents` controls how many of those documents are updated after index build, so zero and {SeedMaintenanceDocuments} compare the same initial state. Maintenance ms and maintenance storage Δ measure that post-build batch; they are write-cost observations for this batch, not a sustained write-amplification estimate. Repeat samples are repeated measurements within one seeded process and do not constitute independent seeded trials; no cross-trial variance or confidence interval is claimed.");
        if (directProbeLane)
        {
            builder.AppendLine("Per-index build phases measure each lifecycle ApplyAsync call that produced an active visible index. Catch-up phases measure the post-build update drain plus one physical index probe through AutomaticIndexRuntime.MeasureOnIndexAsync; they are not pure catch-up-only timings. CPU, RSS, and allocation deltas are process-wide diagnostics for each timed phase. File sizes are logical FileInfo.Length sums and are not physical I/O counters.");
            if (maintenanceDocuments == 0)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"The zero-maintenance invocation is the direct-probe lane: each active index runs one direct first probe followed by {DirectProbeRepeatSamples} direct repeats through AutomaticIndexRuntime.MeasureOnIndexAsync. The report records direct first, p50, and p95 timings in `Catch-up + index probe phases`; the full runtime first query and repeat p50/p95 are measured afterward and the first query is warmed by the direct probe.");
            }
            else
            {
                builder.AppendLine("This invocation is the diagnostic lane: its `First query` values follow the per-index catch-up probes and are warmed, so omit them from cold first-query comparisons. Pair it with an identically seeded invocation that omits `--index-perf` for cold first-query timing.");
            }
        }
        else
        {
            builder.AppendLine("This invocation records per-predicate first-call timings after post-build maintenance and without diagnostic index probes. The `status` predicate runs first; `nested profile.segment` follows the status repeats and is warmed as described above. Pair it with an identically seeded `--index-perf` invocation for per-index lifecycle build and post-build catch-up plus probe phases; diagnostic-lane timings follow extra probes and are not comparable to this lane.");
        }
        var jevAdviceIncluded = rows.Any(static row => row.Strategy == "Legacy JEV-assisted");
        var jevFallbackIncluded = rows.Any(static row => row.Strategy == "Deterministic fallback after legacy JEV attempt");
        var jevStatus = jevAdviceIncluded
            ? "legacy JEV inference produced a measured row"
            : jevFallbackIncluded
                ? "no legacy JEV advice row; deterministic fallback is identified separately"
                : jevRequested && !jevApproved
                    ? "UNRUN: --jev was requested without an approved TERRABASE_JEV_API_KEY"
                    : "UNRUN: --jev was not supplied";
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Legacy JEV lane status: {jevStatus}. Model probabilities/confidence are not treated as measured index benefit.");
        var vonAdviceIncluded = rows.Any(static row => row.Strategy == "Von-assisted");
        var vonInferenceEvidence = rows.Any(static row => row.InferenceRequestedModel?.StartsWith("von-", StringComparison.Ordinal) == true
            && row.InferenceInputTokens.HasValue);
        var vonFallbackIncluded = rows.Any(static row => row.Strategy == "Deterministic fallback after Von attempt");
        var vonStatus = !vonRequested
            ? "UNRUN: --von was not supplied"
            : vonAdviceIncluded
                ? "local Von inference produced a measured row"
                : vonInferenceEvidence
                    ? "local Von inference returned model and usage evidence, but deterministic fallback was selected"
                : vonFallbackIncluded
                    ? "no Von advice row; deterministic fallback is identified separately with its reason code"
                    : "UNRUN: no Von lane result was recorded";
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Von lane status: {vonStatus}. `--von` requests model `von-1.2.0` with a 30-second client deadline. Server CPU and RSS are outside this process's measurements.");

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, StrategyResult row, bool indexPerformance)
    {
        var history = row.QueryHistoryBeforeCandidates is { } count ? count.ToString(CultureInfo.InvariantCulture) : "—";
        var firstRows = Number(row.Measurement.FirstRowsExamined);
        var repeatRows = Number(row.Measurement.RepeatRowsExamined);
        var delta = row.FinalStorageBytes - row.StorageBeforeIndexBytes - row.MaintenanceStorageDeltaBytes;
        var diagnosticFirstQuery = row.IndexCatchUpTimings.Length > 1
            && row.IndexCatchUpTimings != "not-applicable"
            && row.Access.StartsWith("TerraBase", StringComparison.Ordinal);
        var firstQuery = diagnosticFirstQuery ? "—" : row.Measurement.FirstQueryMicroseconds.ToString("F1", CultureInfo.InvariantCulture);
        var firstCpu = diagnosticFirstQuery ? "—" : row.Measurement.FirstQueryMetrics.CpuMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
        var firstRss = diagnosticFirstQuery ? "—" : row.Measurement.FirstQueryMetrics.WorkingSetDeltaBytes.ToString(CultureInfo.InvariantCulture);
        var firstAllocated = diagnosticFirstQuery ? "—" : row.Measurement.FirstQueryMetrics.AllocatedBytes.ToString(CultureInfo.InvariantCulture);
        var logicalDocumentEntryBytes = row.AutomaticIndexStorage is { } automaticStorage
            ? automaticStorage.LogicalDocumentEntryBytes.ToString(CultureInfo.InvariantCulture)
            : "N/A";
        var walBytes = row.AutomaticIndexStorage is { } storageWithWal
            ? storageWithWal.WalBytes.ToString(CultureInfo.InvariantCulture)
            : "N/A";
        var walCompactionStagingBytes = row.AutomaticIndexStorage is { } storageWithStaging
            ? storageWithStaging.WalCompactionStagingBytes.ToString(CultureInfo.InvariantCulture)
            : "N/A";
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"| {row.Shape} | {row.Strategy} | {row.IndexBudget} | {row.ActiveIndexCount} | {firstQuery} | {firstCpu} | {firstRss} | {firstAllocated} | {row.Measurement.RepeatP50Microseconds:F1} | {row.Measurement.RepeatP95Microseconds:F1} | {firstRows} | {repeatRows} | {row.Access} | {BoolText(row.Measurement.FirstIndexServed)} | {row.Measurement.RepeatIndexServedFraction.ToString("P0", CultureInfo.InvariantCulture)} | {row.Correct} | {history} | {row.InitialIngestMilliseconds:F1} | {row.ProfileMilliseconds:F1} | {row.BuildMilliseconds:F1} | {row.IndexBuildTimings} | {row.InferenceMilliseconds:F1} | {TokenText(row.InferenceInputTokens)} | {TokenText(row.InferenceOutputTokens)} | {InferenceCostText(row)} | {row.MaintenanceMilliseconds:F1} | {row.IndexCatchUpTimings} | {row.MaintenanceStorageDeltaBytes} | {row.StorageAfterWritesBytes} | {row.StorageBeforeIndexBytes} | {row.FinalStorageBytes} | {delta} | {logicalDocumentEntryBytes} | {walBytes} | {walCompactionStagingBytes} | {row.Advice} |");
    }

    private static void AppendRegressionSection(
        StringBuilder builder, IReadOnlyList<StrategyResult> rows, bool indexPerformance)
    {
        builder.AppendLine("## Regressions versus the same-engine no-index baseline");
        builder.AppendLine();
        builder.AppendLine("Positive latency or maintenance values mean slower than that engine's no-secondary-index lane. Storage is the final logical file-size delta in bytes. TerraBase diagnostic rows omit first-query deltas because their first query follows an index probe; SQLite first queries remain cold even when `--index-perf` is supplied.");
        builder.AppendLine();
        builder.AppendLine("| Shape | Strategy | Baseline | First query Δ | Repeat p50 Δ | Maintenance Δ | Final storage Δ |");
        builder.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            var baselineStrategy = row.Strategy.StartsWith("SQLite ", StringComparison.Ordinal)
                ? "SQLite tuned no secondary index"
                : "No secondary index";
            var baseline = rows.FirstOrDefault(candidate =>
                candidate.Shape == row.Shape && candidate.Strategy == baselineStrategy);
            if (baseline is null)
            {
                continue;
            }

            var diagnosticFirstQuery = row.IndexCatchUpTimings.Length > 1
                && row.IndexCatchUpTimings != "not-applicable"
                && row.Access.StartsWith("TerraBase", StringComparison.Ordinal);
            var firstDelta = diagnosticFirstQuery
                ? "n/a (diagnostic)"
                : PercentDelta(row.Measurement.FirstQueryMicroseconds, baseline.Measurement.FirstQueryMicroseconds);
            var repeatDelta = PercentDelta(row.Measurement.RepeatP50Microseconds, baseline.Measurement.RepeatP50Microseconds);
            var maintenanceDelta = PercentDelta(row.MaintenanceMilliseconds, baseline.MaintenanceMilliseconds);
            var storageDelta = row.FinalStorageBytes - baseline.FinalStorageBytes;
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Shape} | {row.Strategy} | {baselineStrategy} | {firstDelta} | {repeatDelta} | {maintenanceDelta} | {storageDelta:+#,##0;-#,##0;0} B |");
        }
    }

    private static string PercentDelta(double value, double baseline)
    {
        if (baseline == 0)
        {
            return value == 0 ? "0.0%" : "n/a (baseline 0)";
        }

        return ((value - baseline) / baseline).ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture);
    }

    private static string TokenText(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "—";

    private static string InferenceCostText(StrategyResult row) =>
        row.InferenceRequestedModel?.StartsWith("von-", StringComparison.Ordinal) == true
            && row.InferenceInputTokens.HasValue
            ? "unpriced"
            : row.InferenceCostUsd?.ToString("F8", CultureInfo.InvariantCulture) ?? "—";

    private static string AccessLabel(Measurement measurement, string indexed, string scan) =>
        measurement.FirstIndexServed ? indexed : scan;

    private static string FormatPhaseTimings(IReadOnlyList<IndexPhaseTiming> timings)
    {
        if (timings.Count == 0)
        {
            return "—";
        }

        return string.Join("; ", timings.Select(static timing =>
        {
            var probe = timing.DirectProbe;
            var direct = probe is { RepeatSamples: > 0 }
                ? $", direct first {probe.FirstMicroseconds:F1} µs, direct p50 {probe.RepeatP50Microseconds!.Value:F1} µs, direct p95 {probe.RepeatP95Microseconds!.Value:F1} µs ({probe.RepeatSamples} repeats)"
                : probe is not null
                    ? $", direct first {probe.FirstMicroseconds:F1} µs"
                    : string.Empty;
            return $"{timing.Path}: {timing.Metrics.ElapsedMilliseconds:F1} ms, CPU {timing.Metrics.CpuMilliseconds:F1} ms, RSS Δ {timing.Metrics.WorkingSetDeltaBytes} B, alloc Δ {timing.Metrics.AllocatedBytes} B{direct}";
        }));
    }

    private static string BoolText(bool value) => value ? "True" : "False";

    private static string Number(double? value) =>
        value is { } number && double.IsFinite(number) ? number.ToString("F1", CultureInfo.InvariantCulture) : "—";

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

    private sealed class PhaseProbe
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly Process process = Process.GetCurrentProcess();
        private readonly TimeSpan initialCpu;
        private readonly long initialWorkingSet;
        private readonly long initialAllocated;

        private PhaseProbe()
        {
            process.Refresh();
            initialCpu = process.TotalProcessorTime;
            initialWorkingSet = process.WorkingSet64;
            initialAllocated = GC.GetTotalAllocatedBytes(false);
        }

        public static PhaseProbe Start() => new();

        public PhaseMetrics Stop()
        {
            watch.Stop();
            process.Refresh();
            return new PhaseMetrics(
                watch.Elapsed.TotalMilliseconds,
                (process.TotalProcessorTime - initialCpu).TotalMilliseconds,
                process.WorkingSet64 - initialWorkingSet,
                GC.GetTotalAllocatedBytes(false) - initialAllocated);
        }
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

    private readonly record struct PhaseMetrics(
        double ElapsedMilliseconds, double CpuMilliseconds,
        long WorkingSetDeltaBytes, long AllocatedBytes);

    private sealed record IndexPhaseTiming(
        string Path, PhaseMetrics Metrics, DirectProbeMeasurement? DirectProbe);

    private sealed record DirectProbeMeasurement(
        double FirstMicroseconds, double? RepeatP50Microseconds,
        double? RepeatP95Microseconds, int RepeatSamples,
        double[] RepeatLatencyMicroseconds);

    private sealed record Measurement(
        double FirstQueryMicroseconds, double RepeatP50Microseconds, double RepeatP95Microseconds,
        double? FirstRowsExamined, double? RepeatRowsExamined, int FirstResultCount, int RepeatResultCount, bool Correct,
        bool FirstIndexServed, double RepeatIndexServedFraction, PhaseMetrics FirstQueryMetrics,
        double[] RepeatLatencyMicroseconds);

    private sealed record StrategyResult(
        string Shape, string Strategy, string IndexBudget, Measurement Measurement, bool ActiveIndex, int ActiveIndexCount, bool Correct,
        int? QueryHistoryBeforeCandidates, double InitialIngestMilliseconds, double ProfileMilliseconds,
        double BuildMilliseconds, double InferenceMilliseconds, decimal? InferenceCostUsd,
        double MaintenanceMilliseconds, long MaintenanceStorageDeltaBytes,
        long StorageAfterWritesBytes, long StorageBeforeIndexBytes, long FinalStorageBytes,
        string Advice, string Access, string IndexBuildTimings, string IndexCatchUpTimings,
        AutomaticIndexStorageDiagnostics? AutomaticIndexStorage = null,
        IReadOnlyList<IndexPhaseTiming>? IndexBuildPhaseDetails = null,
        IReadOnlyList<IndexPhaseTiming>? IndexCatchUpPhaseDetails = null,
        int? InferenceInputTokens = null,
        int? InferenceOutputTokens = null,
        string? InferenceRequestedModel = null,
        string? InferenceActualModel = null);

    private sealed record AutomaticIndexStorageDiagnostics(
        ulong LogicalDocumentEntryBytes,
        ulong WalBytes,
        ulong WalCompactionStagingBytes);

    private sealed record QueryIndependentRawReceipt(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        int Documents,
        int RepeatSamples,
        int MaximumIndexBudget,
        int MaintenanceDocuments,
        int PhysicalIndexBudgetMiB,
        bool IndexPerformance,
        string SummaryFile,
        string SummarySha256,
        IReadOnlyList<StrategyResult> Rows,
        IReadOnlyList<string> SqliteNoIndexPlans,
        IReadOnlyList<string> SqliteExplicitPlans);

    private sealed record PointMirrorRawReceipt(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        int Documents,
        int RepeatSamples,
        int MaximumIndexBudget,
        int PhysicalIndexBudgetMiB,
        ulong SourceCommitSequence,
        int CompletedIndexes,
        PhaseMetrics BuildMetrics,
        string SummaryFile,
        string SummarySha256,
        IReadOnlyList<ProbeBranchResult> Rows);
}
