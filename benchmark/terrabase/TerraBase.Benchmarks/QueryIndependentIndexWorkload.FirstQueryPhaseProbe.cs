using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Indexing.Automatic;
using TerraBase.Indexing.Runtime;
using TerraBase.Storage.DatabaseBinding;

namespace TerraBase.Benchmarks;

internal static partial class QueryIndependentIndexWorkload
{
    private static async Task<int> RunFirstQueryPhaseProbeAsync(
        int documents, int maximumIndexes, int physicalBudgetMiB, string? outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory ?? Path.Combine(
            "bench", "query-independent-first-query-phase-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(output);
        var traceEnabled = Environment.GetEnvironmentVariable("TERRABASE_FIRST_QUERY_TRACE") == "1";
        var lanes = new List<FirstQueryPhaseLane>(4);
        foreach (var updates in new[] { 0, SeedMaintenanceDocuments })
        {
            lanes.Add(await RunFirstQueryPhaseLaneAsync(documents, maximumIndexes, physicalBudgetMiB, updates,
                    diagnostic: false, traceEnabled)
                .ConfigureAwait(false));
            lanes.Add(await RunFirstQueryPhaseLaneAsync(documents, maximumIndexes, physicalBudgetMiB, updates,
                    diagnostic: true, traceEnabled)
                .ConfigureAwait(false));
        }

        var candidateId = lanes[0].CandidateId;
        if (lanes.Any(lane => lane.CandidateId != candidateId || lane.HistoryBeforeCandidates != 0
            || lane.HistoryBeforeQuery != 0 || !lane.Correct || lane.FirstQueryObservationDelta != 1))
            throw new InvalidOperationException("First-query phase lanes did not preserve candidate, empty query history, exact IDs, and one observation.");

        var subject = RunReceipt.CaptureSubject();
        var runnerAssemblySha = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(typeof(QueryIndependentIndexWorkload).Assembly.Location).ConfigureAwait(false)))
            .ToLowerInvariant();
        var receipt = new FirstQueryPhaseReceipt(
            traceEnabled ? 2 : 1, DateTimeOffset.UtcNow, documents, SeedMaintenanceDocuments, maximumIndexes,
            physicalBudgetMiB, traceEnabled,
            subject.Commit, subject.TreeDigest, subject.WorkingTreeDigest, runnerAssemblySha, lanes);
        var receiptPath = Path.Combine(output, "first-query-phase-receipt.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, RawJsonOptions) + Environment.NewLine)
            .ConfigureAwait(false);
        var receiptSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(receiptPath).ConfigureAwait(false)))
            .ToLowerInvariant();
        var markdown = RenderFirstQueryPhaseProbe(documents, lanes, receiptSha, traceEnabled);
        var markdownPath = Path.Combine(output, "first-query-phase.md");
        await File.WriteAllTextAsync(markdownPath, markdown).ConfigureAwait(false);
        Console.Write(markdown);
        Console.WriteLine($"Raw receipt: {receiptPath}");
        return 0;
    }

    private static async Task<FirstQueryPhaseLane> RunFirstQueryPhaseLaneAsync(
        int documents, int maximumIndexes, int physicalBudgetMiB, int updates, bool diagnostic, bool traceEnabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "terrabase-first-query-phase-" + Guid.NewGuid().ToString("N"));
        var rootId = Path.GetFileName(root);
        var tenant = new TenantId("benchmark-tenant");
        var collection = new CollectionId(Collection);
        var scope = new AutomaticIndexScope(tenant.Value, "benchmark-database", Collection);
        try
        {
            await using var source = new global::TerraBase.StorageEngine.StorageEngine(root);
            await SeedTerrabaseAsync(source, tenant, collection, documents).ConfigureAwait(false);
            await using var runtime = await AutomaticIndexRuntime.OpenAsync(source,
                new AutomaticIndexRuntimeOptions { DisablePageCache = true },
                CreateRuntimeBudgetLedger(scope, physicalBudgetMiB)).ConfigureAwait(false);
            EnsureEmptyQueryHistory(runtime, "before phase-probe candidate generation");
            await using var snapshot = await source.BeginSnapshotAsync().ConfigureAwait(false);
            var profile = await new CollectionIndexProfiler().ProfileAsync(source, snapshot, scope).ConfigureAwait(false);
            var candidates = await new SchemaStatisticsCandidateSource().GetCandidatesAsync(profile,
                new QueryIndependentIndexPolicy { MaximumIndexes = maximumIndexes }).ConfigureAwait(false);
            EnsureEmptyQueryHistory(runtime, "after phase-probe candidate generation");
            var historyBeforeCandidates = runtime.Analyzer.ObservationCount;
            var advice = await new SchemaStatisticsAdvisor().AdviseAsync(candidates).ConfigureAwait(false);
            var statusRecommendation = advice.Recommendations.FirstOrDefault(recommendation =>
                candidates.Candidates.Any(candidate => candidate.Definition.CandidateId == recommendation.CandidateId
                    && candidate.Field.Path == StatusPath));
            if (statusRecommendation is null)
                throw new InvalidOperationException("Deterministic advice did not recommend the status candidate.");
            using (var lifecycle = new QueryIndependentIndexLifecycleManager(runtime, scope))
            {
                await lifecycle.ApplyAsync(candidates, advice with
                {
                    Recommendations = ImmutableArray.Create(statusRecommendation),
                }).ConfigureAwait(false);
            }

            var candidateId = statusRecommendation.CandidateId;
            var active = runtime.Coordinator.Snapshot(scope).Indexes.Single(record =>
                record.CandidateId == candidateId && record.Phase == AutomaticIndexPhase.Active && record.Visible);
            _ = await UpdateTerrabaseMaintenanceAsync(source, tenant, collection, root, updates).ConfigureAwait(false);
            // The held-out predicate does not exist until indexing and updates finish.
            var query = EvaluationShapes[0].Query(tenant, collection);
            var expected = ExpectedIds(documents, EvaluationShapes[0]);
            var historyBeforeQuery = runtime.Analyzer.ObservationCount;
            if (historyBeforeQuery != 0)
                throw new InvalidOperationException("The phase-probe query history was populated before its first query.");

            var inspection = new List<FirstQueryIndexInspection>();
            PhaseMetrics? planning = null;
            string? planningAccess = null;
            string? planningReason = null;
            PhaseMetrics? direct = null;
            ulong? directWork = null;
            ulong? watermarkBeforeDirect = null;
            ulong? watermarkAfterDirect = null;
            FirstQueryPhysicalBranches? physicalBranches = null;
            if (diagnostic)
            {
                foreach (var record in runtime.Coordinator.Snapshot(scope, int.MaxValue).Indexes
                    .Where(static record => record.Phase == AutomaticIndexPhase.Active && record.Visible)
                    .OrderBy(static record => record.CandidateId, StringComparer.Ordinal))
                {
                    var phase = PhaseProbe.Start();
                    var physical = await runtime.Backend.InspectPhysicalAsync(record.CandidateId).ConfigureAwait(false);
                    var metrics = phase.Stop();
                    inspection.Add(new FirstQueryIndexInspection(record.CandidateId, metrics,
                        physical.Exists, physical.Visible, physical.AppliedPosition));
                }

                var planCandidate = new QueryPlannerCandidate(runtime.Backend.Store(candidateId), source,
                    new QueryPlannerCostStats(), hasPinnedSnapshotCapability: true, sourceFallback: null);
                var fencedQuery = new IndexRuntimeQuery(query.Tenant, query.Collection, query.Predicates,
                    query.Projection, source.ChangeFeedCursor.CommitSequence, query.AfterDocumentId, query.ResultLimit);
                var planningPhase = PhaseProbe.Start();
                var explanation = new QueryPlanner(planCandidate).Explain(fencedQuery);
                planning = planningPhase.Stop();
                planningAccess = explanation.Access.ToString();
                planningReason = explanation.Reason;

                watermarkBeforeDirect = runtime.Backend.Store(candidateId).Watermark.CommitSequence;
                var directPhase = PhaseProbe.Start();
                var measured = await runtime.MeasureOnIndexAsync(candidateId, query).ConfigureAwait(false);
                direct = directPhase.Stop();
                if (measured is null)
                    throw new InvalidOperationException("The phase-probe status index could not execute its direct physical probe.");
                directWork = measured.Value.Work.WorkUnits;
                watermarkAfterDirect = runtime.Backend.Store(candidateId).Watermark.CommitSequence;

                // Mirror the service's point -> range -> iterator branch order
                // with independent budgets on this already warmed diagnostic
                // root. These probes identify branch eligibility and physical
                // work; they are not internal spans of the cold first query.
                var store = runtime.Backend.Store(candidateId);
                var matcher = new RuntimeIndexCodec.IndexPredicateMatcher(query.Predicates);
                bool Verify(Document document) => document.Key.Tenant == query.Tenant
                    && document.Key.Collection == query.Collection && matcher.Matches(document);
                var sourceCut = source.ChangeFeedCursor.CommitSequence;
                var pointPhase = PhaseProbe.Start();
                var point = store.TryPointSeek(query,
                    IndexRuntimeExecutionBounds.ForPage(ResultLimit, null, QueryScanBudget, QueryMemoryBudgetBytes, QueryWorkBudget),
                    Verify, new IndexRuntimeExecutionBudget(IndexRuntimeExecutionBounds.ForPage(
                        ResultLimit, null, QueryScanBudget, QueryMemoryBudgetBytes, QueryWorkBudget)), sourceCut);
                var pointMetrics = pointPhase.Stop();
                PhaseMetrics? rangeMetrics = null;
                PhaseMetrics? iteratorMetrics = null;
                IndexRuntimeCandidateExecution? selectedPhysical = point;
                if (selectedPhysical is null)
                {
                    var rangeBounds = IndexRuntimeExecutionBounds.ForPage(ResultLimit, null,
                        QueryScanBudget, QueryMemoryBudgetBytes, QueryWorkBudget);
                    var rangePhase = PhaseProbe.Start();
                    selectedPhysical = store.TryRangeSeek(query, rangeBounds, Verify,
                        new IndexRuntimeExecutionBudget(rangeBounds), sourceCut);
                    rangeMetrics = rangePhase.Stop();
                }
                if (selectedPhysical is null)
                {
                    var iteratorBounds = IndexRuntimeExecutionBounds.ForPage(ResultLimit, null,
                        QueryScanBudget, QueryMemoryBudgetBytes, QueryWorkBudget);
                    var iteratorPhase = PhaseProbe.Start();
                    selectedPhysical = await store.ExecuteBoundedAsync(query, iteratorBounds, Verify,
                        new IndexRuntimeExecutionBudget(iteratorBounds), sourceCut).ConfigureAwait(false);
                    iteratorMetrics = iteratorPhase.Stop();
                }
                VerifyIds(selectedPhysical.Documents.Select(static document => document.Key.DocumentId), expected,
                    "phase-probe direct physical branch");
                physicalBranches = new FirstQueryPhysicalBranches(pointMetrics, point is not null,
                    rangeMetrics, rangeMetrics is not null && iteratorMetrics is null,
                    iteratorMetrics, selectedPhysical.Access.ToString(), selectedPhysical.Documents.Count,
                    selectedPhysical.PhysicalWork?.EntryReads, selectedPhysical.PhysicalWork?.FullDocumentReads);
            }

            var binding = new DatabaseBinding(new DatabaseBindingDescriptor(
                new DatabaseBindingKey(tenant, new DatabaseRouteId("benchmark")), EntityId.New(), root,
                DatabaseStorageBackend.StorageEngine, DatabaseBindingMode.Explicit, Region.Gru, new HashSet<Region> { Region.Gru }),
                new DatabaseScopedStorageProvider(source, tenant, ownsInner: false), root) { DurableEngine = source };
            var executor = new IndexRuntimeSourceQueryExecutor(binding, collection);
            var bounds = IndexRuntimeExecutionBounds.ForPage(ResultLimit, null,
                QueryScanBudget, QueryMemoryBudgetBytes, QueryWorkBudget);
            var sourceFallbackCalls = 0;
            PhaseMetrics? sourceFallbackMetrics = null;
            async ValueTask<IndexRuntimeCandidateExecution> TimedSource(
                IndexRuntimeQuery sourceQuery, IndexRuntimeExecutionBounds sourceBounds,
                IndexRuntimeExecutionBudget budget, Func<Document, bool> verify,
                ulong requiredCommitSequence, CancellationToken token)
            {
                var phase = PhaseProbe.Start();
                var result = await executor.ExecuteAsync(sourceQuery, sourceBounds, budget, verify,
                    requiredCommitSequence, token).ConfigureAwait(false);
                sourceFallbackMetrics = phase.Stop();
                sourceFallbackCalls++;
                return result;
            }

            using var activityTrace = traceEnabled ? new FirstQueryActivityTrace() : null;
            var queryPhase = PhaseProbe.Start();
            var result = await runtime.QueryBoundedAsync(scope, query, bounds, TimedSource).ConfigureAwait(false);
            var queryMetrics = queryPhase.Stop();
            var traceSpans = activityTrace?.Complete() ?? [];
            var actualIds = result.Documents.Select(static document => document.Key.DocumentId)
                .Order(StringComparer.Ordinal).ToArray();
            VerifyIds(actualIds, expected, "phase-probe first held-out query");
            var observationDelta = runtime.Analyzer.ObservationCount - historyBeforeQuery;
            return new FirstQueryPhaseLane(rootId, updates, diagnostic ? "diagnostic-boundaries" : "cold-first-query",
                candidateId, active.AppliedPosition, source.ChangeFeedCursor.CommitSequence,
                historyBeforeCandidates, historyBeforeQuery, observationDelta,
                queryMetrics, result.Plan.Access.ToString(), result.Plan.Reason,
                result.Execution.Access.ToString(), result.Execution.UsedSnapshotFallback,
                result.Execution.Measurement.RowsExamined, actualIds, true,
                sourceFallbackCalls, sourceFallbackMetrics, inspection,
                planning, planningAccess, planningReason, direct, directWork,
                watermarkBeforeDirect, watermarkAfterDirect, physicalBranches, traceSpans);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string RenderFirstQueryPhaseProbe(int documents, IReadOnlyList<FirstQueryPhaseLane> lanes,
        string receiptSha, bool traceEnabled)
    {
        var builder = new StringBuilder("# Held-out status first-query phase probe\n\n");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Same deterministic status candidate, {documents:N0} plus 100 seeded documents, fresh root per row, empty query history before execution, and exact result IDs in the raw receipt.");
        builder.AppendLine();
        builder.AppendLine("| Updates | Lane | First query ms | Index inspections ms | Pure planner ms | Direct index probe ms | Point ms | Range ms | Iterator ms | Source fallback ms | Rows | Access | Result IDs | Observation delta |");
        builder.AppendLine("|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|");
        foreach (var lane in lanes)
        {
            var inspections = lane.IndexInspections.Sum(item => item.Metrics.ElapsedMilliseconds);
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {lane.PostBuildUpdates} | {lane.Lane} | {lane.FirstQuery.ElapsedMilliseconds:F3} | {inspections:F3} | {lane.Planning?.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture) ?? "—"} | {lane.DirectIndexProbe?.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture) ?? "—"} | {lane.PhysicalBranches?.Point.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture) ?? "—"} | {lane.PhysicalBranches?.Range?.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture) ?? "—"} | {lane.PhysicalBranches?.Iterator?.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture) ?? "—"} | {lane.SourceFallback?.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture) ?? "—"} | {lane.RowsExamined} | {lane.ExecutionAccess} | {lane.ResultIds.Count} | {lane.FirstQueryObservationDelta} |");
        }
        builder.AppendLine();
        builder.AppendLine("The cold lane executes the held-out predicate before any diagnostic probe. The diagnostic lane times physical inspection, a standalone planner Explain with the current source cut, a direct index probe, and the point/range/iterator branch attempts in that order on another freshly built equivalent root. The direct probe includes any index catch-up plus physical execution. Only eligible physical branches execute; a range or iterator dash means a prior branch served the query. Its following full query is warmed by those probes. These boundary measurements are not additive spans inside the cold first query. CPU and allocation metrics are process-wide, so concurrent work can contribute.");
        if (traceEnabled)
        {
            builder.AppendLine();
            builder.AppendLine("## Real first-query ActivitySource spans");
            builder.AppendLine();
            builder.AppendLine("Enabled by `TERRABASE_FIRST_QUERY_TRACE=1`. These nested spans come from the same cold query; child durations are included in their parent. No tenant, document, predicate, or candidate identifiers are emitted.");
            builder.AppendLine();
            builder.AppendLine("| Updates | Span | Elapsed ms | Tags |");
            builder.AppendLine("|---:|---|---:|---|");
            foreach (var lane in lanes.Where(static lane => lane.Lane == "cold-first-query"))
            {
                foreach (var span in lane.TraceSpans)
                {
                    var tags = string.Join(", ", span.Tags.Select(static tag => $"{tag.Key}={tag.Value}"));
                    builder.AppendLine(CultureInfo.InvariantCulture,
                        $"| {lane.PostBuildUpdates} | {span.Name} | {span.ElapsedMilliseconds:F3} | {tags} |");
                }
            }
        }
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Raw receipt SHA-256: `{receiptSha}`. The raw receipt includes exact IDs, source commit and working-tree digests; `run-receipt.json` records runner provenance and artifact digests.");
        return builder.ToString();
    }

    private sealed record FirstQueryIndexInspection(string CandidateId, PhaseMetrics Metrics,
        bool Exists, bool Visible, ulong AppliedPosition);

    private sealed record FirstQueryPhysicalBranches(PhaseMetrics Point, bool PointServed,
        PhaseMetrics? Range, bool RangeServed, PhaseMetrics? Iterator,
        string Access, int Rows, ulong? EntryReads, ulong? FullDocumentReads);

    private sealed record FirstQueryPhaseLane(string RootId, int PostBuildUpdates, string Lane,
        string CandidateId, ulong IndexAppliedPositionAtBuild, ulong SourceCommitSequence,
        int HistoryBeforeCandidates, int HistoryBeforeQuery, int FirstQueryObservationDelta,
        PhaseMetrics FirstQuery, string PlanAccess, string PlanReason, string ExecutionAccess,
        bool UsedSnapshotFallback, ulong RowsExamined, IReadOnlyList<string> ResultIds, bool Correct,
        int SourceFallbackCalls, PhaseMetrics? SourceFallback,
        IReadOnlyList<FirstQueryIndexInspection> IndexInspections,
        PhaseMetrics? Planning, string? PlanningAccess, string? PlanningReason,
        PhaseMetrics? DirectIndexProbe, ulong? DirectWorkUnits,
        ulong? WatermarkBeforeDirect, ulong? WatermarkAfterDirect,
        FirstQueryPhysicalBranches? PhysicalBranches,
        IReadOnlyList<FirstQueryTraceSpan> TraceSpans);

    private sealed record FirstQueryPhaseReceipt(int SchemaVersion, DateTimeOffset GeneratedAtUtc,
        int Documents, int SeedMaintenanceDocuments, int MaximumIndexes, int PhysicalIndexBudgetMiB,
        bool TraceEnabled,
        string SubjectCommit, string SubjectTreeDigest, string? SubjectWorkingTreeDigest,
        string RunnerAssemblySha256,
        IReadOnlyList<FirstQueryPhaseLane> Lanes);
}
