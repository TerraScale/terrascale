using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TerraBase.Schema;
using TerraBase.Storage.Constraints;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>
/// Relationship-path qualification: loads a parent/child corpus through the
/// strict constraint path, then measures foreign-key writes, reverse
/// one-hop range reads, and restrict-delete enforcement as the existing
/// edge count grows. Receipts from before and after an engine change are
/// the improvement record.
/// </summary>
public static class RelationshipWorkload
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static bool IsRequested(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--workload=", StringComparison.OrdinalIgnoreCase)
                && argument["--workload=".Length..].Equals("relationship", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (argument.Equals("--workload", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length
                && args[index + 1].Equals("relationship", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        RelationshipOptions options;
        try
        {
            options = RelationshipOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid relationship workload configuration: {exception.Message}");
            RelationshipOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            RelationshipOptions.PrintHelp();
            return 0;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var (receipt, summary) = await RunAsync(options, outputDirectory).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "relationship-receipt.json"),
                JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "relationship-summary.md"),
                summary).ConfigureAwait(false);
            Console.WriteLine($"Relationship workload receipt: {outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<(RelationshipReceipt Receipt, string Summary)> RunAsync(
        RelationshipOptions options,
        string outputDirectory)
    {
        var sourcePath = Path.Combine(outputDirectory, "source");
        if (Directory.Exists(sourcePath))
        {
            Directory.Delete(sourcePath, recursive: true);
        }

        var tenant = new TenantId("relationship-bench");
        var parents = new CollectionId("parents");
        var children = new CollectionId("children");
        var schemas = new SchemaRegistry();
        schemas.Register(tenant, parents, new SchemaMetadata(
            "parents",
            1,
            typeof(ParentDocument),
            [new FieldMetadata("name", typeof(string), new SchemaValueDescriptor(SchemaValueType.Text, nullable: true, canonicalEntityId: false))]));
        schemas.Register(tenant, children, new SchemaMetadata(
            "children",
            1,
            typeof(ChildDocument),
            [new FieldMetadata("parentId", typeof(string), new SchemaValueDescriptor(SchemaValueType.Text, nullable: true, canonicalEntityId: false))],
            [new RelationshipMetadata(
                "parent",
                "children",
                "parents",
                RelationshipKind.ManyToOne,
                DeletePolicy.Restrict,
                foreignKey: "parentId")]));

        // Phase 1: parents (plain writes), then children through the strict
        // constraint path. Child-write latency is recorded in fixed windows
        // so the growth with the existing edge count is visible in the receipt.
        var loadWindows = new List<LoadWindow>();
        var loadStarted = Stopwatch.GetTimestamp();
        await using (var engine = new DurableStorageEngine(sourcePath))
        await using (var store = new DocumentStore(engine, new MetricsRegistry(), schemas))
        {
            for (var index = 0; index < options.Parents; index++)
            {
                using var body = JsonDocument.Parse($"{{\"name\":\"parent-{index.ToString(CultureInfo.InvariantCulture)}\"}}");
                var result = await store.UpsertAsync(
                    new DocumentKey(tenant, parents, $"parent-{index}"),
                    body.RootElement,
                    DocumentPrecondition.Missing).ConfigureAwait(false);
                if (result.Outcome != WriteOutcome.Created)
                {
                    throw new InvalidDataException($"Parent create failed: {result.Outcome}.");
                }
            }

            var windowLatencies = new List<double>(options.WindowSize);
            var windowStartEdges = 0;
            for (var index = 0; index < options.Edges; index++)
            {
                var parentIndex = index % options.Parents;
                using var body = JsonDocument.Parse(
                    $"{{\"parentId\":\"parent-{parentIndex.ToString(CultureInfo.InvariantCulture)}\"}}");
                var writeStarted = Stopwatch.GetTimestamp();
                var result = await store.UpsertAsync(
                    new DocumentKey(tenant, children, $"child-{index.ToString(CultureInfo.InvariantCulture)}"),
                    body.RootElement,
                    DocumentPrecondition.Missing).ConfigureAwait(false);
                windowLatencies.Add(Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds);
                if (result.Outcome != WriteOutcome.Created)
                {
                    throw new InvalidDataException($"Child create failed: {result.Outcome}.");
                }

                if (windowLatencies.Count == options.WindowSize)
                {
                    loadWindows.Add(new LoadWindow(
                        windowStartEdges,
                        windowStartEdges + options.WindowSize,
                        Percentile(windowLatencies, 0.5),
                        Percentile(windowLatencies, 0.95)));
                    windowStartEdges += options.WindowSize;
                    windowLatencies.Clear();
                }
            }

            // Phase 2: foreign-key updates that re-point an existing child at
            // a different parent, forcing edge removal plus restage with the
            // full cardinality read set.
            var updateSamples = new List<double>(options.UpdateSamples);
            for (var sample = 0; sample < options.UpdateSamples; sample++)
            {
                var childIndex = (sample * (options.Edges / Math.Max(1, options.UpdateSamples))) % options.Edges;
                var parentIndex = (childIndex + 1 + sample) % options.Parents;
                using var body = JsonDocument.Parse(
                    $"{{\"parentId\":\"parent-{parentIndex.ToString(CultureInfo.InvariantCulture)}\"}}");
                var updateStarted = Stopwatch.GetTimestamp();
                var result = await store.UpsertAsync(
                    new DocumentKey(tenant, children, $"child-{childIndex.ToString(CultureInfo.InvariantCulture)}"),
                    body.RootElement).ConfigureAwait(false);
                updateSamples.Add(Stopwatch.GetElapsedTime(updateStarted).TotalMilliseconds);
                if (result.Outcome != WriteOutcome.Updated)
                {
                    throw new InvalidDataException($"Child update failed: {result.Outcome}.");
                }
            }

            // Phase 3: reverse one-hop range reads. This is the exact query
            // shape incoming delete policies issue for every parent delete.
            var reverseSamples = new List<double>(options.ReadSamples);
            var reverseCounts = new List<int>(options.ReadSamples);
            for (var sample = 0; sample < options.ReadSamples; sample++)
            {
                var parentIndex = sample % options.Parents;
                var endpoint = new DocumentKey(tenant, parents, $"parent-{parentIndex.ToString(CultureInfo.InvariantCulture)}");
                var readStarted = Stopwatch.GetTimestamp();
                var feedSnapshot = await engine.BeginChangeFeedSnapshotAsync().ConfigureAwait(false);
                var records = await engine.ReadConstraintRecordsAsync(
                    feedSnapshot.StorageSnapshot,
                    new ConstraintRecordQuery(endpoint.Tenant, endpoint, ConstraintRecordKind.RelationReverse, "parent")).ConfigureAwait(false);
                await feedSnapshot.DisposeAsync().ConfigureAwait(false);
                reverseSamples.Add(Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds);
                reverseCounts.Add(records.Count);
            }

            // Phase 4: restrict-delete enforcement. Every attempt performs
            // the reverse range read and rejects the complete plan. Parents
            // drained by the update phase are skipped so every sample really
            // exercises the restrict read set.
            var restrictSamples = new List<double>(options.DeleteSamples);
            for (var sample = 0; sample < options.DeleteSamples; sample++)
            {
                var parentIndex = sample % options.Parents;
                var endpoint = ResolveParentWithChildren(engine, tenant, parents, parentIndex, options.Parents);
                var deleteStarted = Stopwatch.GetTimestamp();
                var result = await store.DeleteAsync(endpoint).ConfigureAwait(false);
                restrictSamples.Add(Stopwatch.GetElapsedTime(deleteStarted).TotalMilliseconds);
                if (result.Outcome != WriteOutcome.Conflict)
                {
                    throw new InvalidDataException($"Restrict delete must conflict: {result.Outcome}.");
                }
            }

            // Phase 5: one-hop reads through the relationship read surface.
            var oneHopSamples = new List<double>(options.ReadSamples);
            for (var sample = 0; sample < options.ReadSamples; sample++)
            {
                var endpoint = ResolveParentWithChildren(engine, tenant, parents, sample % options.Parents, options.Parents);
                var readStarted = Stopwatch.GetTimestamp();
                var related = await store.ListRelatedAsync(
                    tenant, endpoint, "parent", RelationshipDirection.Reverse, limit: 100).ConfigureAwait(false);
                oneHopSamples.Add(Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds);
                if (related.Count == 0)
                {
                    throw new InvalidDataException("One-hop reads must return the parent's children.");
                }
            }

            // Phase 6: bounded multi-hop traversal through the read surface.
            var traversalSamples = new List<double>(options.ReadSamples);
            var traversalEndpoints = 0;
            for (var sample = 0; sample < options.ReadSamples; sample++)
            {
                var start = ResolveParentWithChildren(engine, tenant, parents, sample % options.Parents, options.Parents);
                var traversalStarted = Stopwatch.GetTimestamp();
                var traversal = await store.TraverseRelationshipsAsync(
                    tenant, start, "parent", RelationshipDirection.Reverse,
                    bounds: new RelationshipTraversalBounds(MaximumHops: 2, MaximumResults: 100, MaximumBytes: 64 * 1024, MaximumWork: 200)).ConfigureAwait(false);
                traversalSamples.Add(Stopwatch.GetElapsedTime(traversalStarted).TotalMilliseconds);
                traversalEndpoints += traversal.Endpoints.Count;
            }

            // Phase 7: the post-restore maintenance pass over the full
            // corpus. A no-op rebuild still derives every record and runs the
            // stale scan, so its wall time is the restore catch-up cost.
            var rebuildStarted = Stopwatch.GetTimestamp();
            var rebuildMutations = await engine.RebuildConstraintRecordsAsync(tenant, schemas).ConfigureAwait(false);
            var rebuildElapsed = Stopwatch.GetElapsedTime(rebuildStarted);

            var loadElapsed = Stopwatch.GetElapsedTime(loadStarted);
            var receipt = new RelationshipReceipt(
                options.Edges,
                options.Parents,
                TimeSpan.FromMilliseconds(loadElapsed.TotalMilliseconds),
                loadWindows.ToArray(),
                Sample(updateSamples),
                Sample(reverseSamples),
                reverseCounts.Count == 0 ? 0 : (int)Math.Round(reverseCounts.Average()),
                Sample(restrictSamples),
                Sample(oneHopSamples),
                Sample(traversalSamples),
                traversalSamples.Count == 0 ? 0 : (int)Math.Round((double)traversalEndpoints / traversalSamples.Count),
                rebuildElapsed,
                rebuildMutations);
            return (receipt, RenderSummary(receipt));
        }
    }

    /// <summary>Returns the first parent at or after the index that still has children.</summary>
    private static DocumentKey ResolveParentWithChildren(
        DurableStorageEngine engine,
        TenantId tenant,
        CollectionId parents,
        int parentIndex,
        int parentCount)
    {
        for (var attempt = 0; attempt < parentCount; attempt++)
        {
            var candidate = new DocumentKey(tenant, parents, $"parent-{((parentIndex + attempt) % parentCount).ToString(CultureInfo.InvariantCulture)}");
            var feedSnapshot = engine.BeginChangeFeedSnapshotAsync().AsTask().GetAwaiter().GetResult();
            var records = engine.ReadConstraintRecordsAsync(
                feedSnapshot.StorageSnapshot,
                new ConstraintRecordQuery(candidate.Tenant, candidate, ConstraintRecordKind.RelationReverse, "parent")).AsTask().GetAwaiter().GetResult();
            feedSnapshot.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (records.Count != 0)
            {
                return candidate;
            }
        }

        throw new InvalidDataException("No parent with children remains for the restrict-delete phase.");
    }

    private static LatencySample Sample(List<double> values)
    {
        if (values.Count == 0)
        {
            return new LatencySample(0, 0, 0, 0, 0);
        }

        var ordered = values.OrderBy(static value => value).ToArray();
        return new LatencySample(
            ordered[0],
            Percentile(values, 0.5),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            ordered[^1]);
    }

    private static double Percentile(List<double> values, double quantile)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        var position = (ordered.Length - 1) * quantile;
        var floor = (int)Math.Floor(position);
        var ceiling = (int)Math.Ceiling(position);
        return floor == ceiling
            ? ordered[floor]
            : ordered[floor] + (ordered[ceiling] - ordered[floor]) * (position - floor);
    }

    private static string RenderSummary(RelationshipReceipt receipt) => $"""
        # Relationship workload

        | Metric | Value |
        | --- | --- |
        | Edges (children) | {receipt.Edges.ToString("N0", CultureInfo.InvariantCulture)} |
        | Parents | {receipt.Parents.ToString("N0", CultureInfo.InvariantCulture)} |
        | Load total | {receipt.LoadElapsed.TotalSeconds.ToString("N2", CultureInfo.InvariantCulture)} s |
        | FK update p50 / p95 / p99 | {Ms(receipt.ForeignKeyUpdate.P50)} / {Ms(receipt.ForeignKeyUpdate.P95)} / {Ms(receipt.ForeignKeyUpdate.P99)} ms |
        | Reverse one-hop p50 / p95 / p99 | {Ms(receipt.ReverseOneHop.P50)} / {Ms(receipt.ReverseOneHop.P95)} / {Ms(receipt.ReverseOneHop.P99)} ms |
        | Mean reverse edges per parent | {receipt.MeanReverseEdges.ToString("N1", CultureInfo.InvariantCulture)} |
        | Restrict delete p50 / p95 / p99 | {Ms(receipt.RestrictDelete.P50)} / {Ms(receipt.RestrictDelete.P95)} / {Ms(receipt.RestrictDelete.P99)} ms |
        | One-hop read API p50 / p95 / p99 | {Ms(receipt.OneHopRead.P50)} / {Ms(receipt.OneHopRead.P95)} / {Ms(receipt.OneHopRead.P99)} ms |
        | Traversal p50 / p95 / p99 | {Ms(receipt.Traversal.P50)} / {Ms(receipt.Traversal.P95)} / {Ms(receipt.Traversal.P99)} ms |
        | Mean traversal endpoints | {receipt.MeanTraversalEndpoints.ToString("N1", CultureInfo.InvariantCulture)} |
        | No-op rebuild pass | {receipt.RebuildElapsed.TotalSeconds.ToString("N2", CultureInfo.InvariantCulture)} s ({receipt.RebuildMutations.ToString("N0", CultureInfo.InvariantCulture)} mutations) |

        ## Child-write latency while loading (window p50 / p95, ms)

        | Window (edges) | p50 | p95 |
        | --- | --- | --- |
        {string.Join("\n        ", receipt.LoadWindows.Select(static window =>
            $"| {window.FromEdges.ToString("N0", CultureInfo.InvariantCulture)}–{window.ToEdges.ToString("N0", CultureInfo.InvariantCulture)} | {Ms(window.P50Milliseconds)} | {Ms(window.P95Milliseconds)} |"))}
        """;

    private static string Ms(double milliseconds) => milliseconds.ToString("N3", CultureInfo.InvariantCulture);

    private sealed record ParentDocument
    {
        public string Name { get; set; } = "";
    }

    private sealed record ChildDocument
    {
        public string ParentId { get; set; } = "";
    }

    private sealed record RelationshipReceipt(
        int Edges,
        int Parents,
        TimeSpan LoadElapsed,
        LoadWindow[] LoadWindows,
        LatencySample ForeignKeyUpdate,
        LatencySample ReverseOneHop,
        int MeanReverseEdges,
        LatencySample RestrictDelete,
        LatencySample OneHopRead,
        LatencySample Traversal,
        int MeanTraversalEndpoints,
        TimeSpan RebuildElapsed,
        int RebuildMutations);

    private sealed record LoadWindow(int FromEdges, int ToEdges, double P50Milliseconds, double P95Milliseconds);

    private sealed record LatencySample(double Min, double P50, double P95, double P99, double Max);

    private sealed class RelationshipOptions
    {
        public int Edges { get; private set; } = 2_000;

        public int Parents { get; private set; } = 100;

        public int WindowSize { get; private set; } = 250;

        public int UpdateSamples { get; private set; } = 50;

        public int ReadSamples { get; private set; } = 100;

        public int DeleteSamples { get; private set; } = 50;

        public string OutputDirectory { get; private set; } = "bench/receipts/relationship";

        public bool ShowHelp { get; private set; }

        public static RelationshipOptions Parse(string[] args)
        {
            var options = new RelationshipOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument.Equals("--help", StringComparison.OrdinalIgnoreCase)
                    || argument.Equals("-h", StringComparison.OrdinalIgnoreCase))
                {
                    options.ShowHelp = true;
                    return options;
                }

                if (!argument.StartsWith("--", StringComparison.Ordinal)
                    || argument.StartsWith("--workload", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name;
                string? value;
                var separator = argument.IndexOf('=');
                if (separator >= 0)
                {
                    name = argument[..separator];
                    value = argument[(separator + 1)..];
                }
                else
                {
                    name = argument;
                    value = index + 1 < args.Length ? args[index + 1] : null;
                    index++;
                }

                switch (name)
                {
                    case "--edges":
                        options.Edges = ParseInt(value, nameof(options.Edges));
                        break;
                    case "--parents":
                        options.Parents = ParseInt(value, nameof(options.Parents));
                        break;
                    case "--window":
                        options.WindowSize = ParseInt(value, nameof(options.WindowSize));
                        break;
                    case "--update-samples":
                        options.UpdateSamples = ParseInt(value, nameof(options.UpdateSamples));
                        break;
                    case "--read-samples":
                        options.ReadSamples = ParseInt(value, nameof(options.ReadSamples));
                        break;
                    case "--delete-samples":
                        options.DeleteSamples = ParseInt(value, nameof(options.DeleteSamples));
                        break;
                    case "--output":
                        options.OutputDirectory = value ?? throw new ArgumentException("--output requires a directory.");
                        break;
                    default:
                        break;
                }
            }

            if (options.Edges < 1 || options.Parents < 1 || options.WindowSize < 1)
            {
                throw new ArgumentException("Edges, parents, and window must be positive.");
            }

            return options;
        }

        private static int ParseInt(string? value, string name) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"{name} requires a positive integer.");

        public static void PrintHelp() => Console.WriteLine(
            """
            Relationship workload
            Usage: --workload=relationship [--edges=N] [--parents=P] [--window=W]
                   [--update-samples=U] [--read-samples=R] [--delete-samples=D] [--output=DIR]
            """);
    }
}
