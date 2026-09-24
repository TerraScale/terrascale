using System.Text.Json;
using TerraBase.Indexing;
using TerraBase.Indexing.Automatic;
using TerraBase.Indexing.Runtime;
using DurableStorageEngine = global::TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>
/// The TerraBase lane driven directly against the durable StorageEngine and
/// the automatic-index runtime in-process — the same engine paths the HTTP
/// host serves, with no transport, no ASP.NET pipeline, and no JSON-DSL
/// parsing, so the comparison against embedded SQLite measures the engine.
/// </summary>
internal static partial class EngineComparisonWorkload
{
    private sealed class TerrabaseEngineBackend : IComparisonBackend
    {
        private readonly DurableStorageEngine source;
        private readonly AutomaticIndexRuntime runtime;
        private readonly AutomaticIndexScope scope;
        private readonly TenantId tenant;
        private readonly CollectionId collection;
        private readonly string payload;
        private readonly IndexRuntimeQuery[] queries;

        private TerrabaseEngineBackend(
            DurableStorageEngine source,
            AutomaticIndexRuntime runtime,
            AutomaticIndexScope scope,
            string payload,
            IndexRuntimeQuery[] queries)
        {
            this.source = source;
            this.runtime = runtime;
            this.scope = scope;
            this.payload = payload;
            this.queries = queries;
            tenant = new TenantId("benchmark-tenant");
            collection = new CollectionId(Collection);
        }

        public string Engine => "terrabase-engine";

        public string? EngineNotes =>
            "durable StorageEngine + AutomaticIndexRuntime in-process (no HTTP); automatic index on the name field built and activated before warm-up";

        public static async ValueTask<TerrabaseEngineBackend> OpenAsync(
            string storagePath,
            int datasetSize,
            int payloadSize,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(storagePath);
            var payload = new string('x', payloadSize);
            var source = new DurableStorageEngine(storagePath);
            try
            {
                var runtime = await AutomaticIndexRuntime.OpenAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
                var scope = new AutomaticIndexScope("benchmark-tenant", TerrabaseHttpBackend.BenchmarkDatabaseUlid.ToString(), Collection);
                var tenant = new TenantId("benchmark-tenant");
                var collection = new CollectionId(Collection);
                var queries = Enumerable.Range(0, datasetSize)
                    .Select(index => new IndexRuntimeQuery(
                        tenant,
                        collection,
                        [IndexPredicate.Equal("name", TerrabaseHttpBackend.SeedName(index))],
                        projection: ["payload"],
                        resultLimit: 10))
                    .ToArray();
                return new TerrabaseEngineBackend(source, runtime, scope, payload, queries);
            }
            catch
            {
                await source.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask SeedAsync(int index, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(TerrabaseHttpBackend.SeedBody(index, payload));
            await source.UpsertAsync(
                new DocumentKey(tenant, collection, TerrabaseHttpBackend.SeedId(index)),
                body.RootElement,
                DocumentPrecondition.Missing,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Builds and activates the automatic index on the name field after seeding.</summary>
        public async Task BuildNameIndexAsync(CancellationToken cancellationToken)
        {
            var candidate = new AutomaticIndexCandidate(scope, ["name"]);
            await runtime.ApplyManualAsync(new ScoredIndexCandidate(candidate, new CandidateScore(100, 90, 5, 1)), cancellationToken).ConfigureAwait(false);
            await runtime.BuildAsync(candidate.CandidateId, cancellationToken).ConfigureAwait(false);
            var shadow = Enumerable.Repeat(new LogicalWork(100, 100, 200), AutomaticIndexShadowVerifier.MinimumSamples).ToArray();
            var indexed = Enumerable.Repeat(new LogicalWork(1, 1, 2), AutomaticIndexShadowVerifier.MinimumSamples).ToArray();
            var verified = await runtime.VerifyShadowAsync(candidate.CandidateId, shadow, indexed, null, cancellationToken).ConfigureAwait(false);
            if (verified.Record.Phase != AutomaticIndexPhase.Active)
            {
                throw new InvalidOperationException($"The benchmark name index did not activate ({verified.Record.Phase}).");
            }
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> ReadAsync(int index, int slot, CancellationToken cancellationToken)
        {
            var document = await source.ReadAsync(
                new DocumentKey(tenant, collection, TerrabaseHttpBackend.SeedId(index)),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"TerraBase point read missed {TerrabaseHttpBackend.SeedId(index)}.");
            if (document.Body.GetProperty("name").GetString() != TerrabaseHttpBackend.SeedName(index))
            {
                throw new InvalidOperationException($"TerraBase point read returned the wrong document for {TerrabaseHttpBackend.SeedId(index)}.");
            }

            return (System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(document.Body).Length, 0);
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> QueryAsync(int index, int slot, CancellationToken cancellationToken)
        {
            var result = await runtime.QueryAsync(scope, queries[index], cancellationToken).ConfigureAwait(false);
            if (result.Documents.Count != 1 || result.Documents[0].Key.DocumentId != TerrabaseHttpBackend.SeedId(index))
            {
                throw new InvalidOperationException($"TerraBase query for {TerrabaseHttpBackend.SeedName(index)} did not return exactly {TerrabaseHttpBackend.SeedId(index)}.");
            }

            var bytes = 0L;
            foreach (var document in result.Documents)
            {
                bytes += System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(document.Body).Length;
            }

            return (bytes, 0);
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> WriteAsync(long sequence, int slot, CancellationToken cancellationToken)
        {
            var body = $"{{\"name\":\"{TerrabaseHttpBackend.WriteName(sequence)}\",\"group\":{sequence % 100},\"payload\":\"{payload}\"}}";
            using var document = JsonDocument.Parse(body);
            await source.UpsertAsync(
                new DocumentKey(tenant, collection, TerrabaseHttpBackend.WriteId(sequence)),
                document.RootElement,
                DocumentPrecondition.Any,
                cancellationToken).ConfigureAwait(false);
            return (0, body.Length);
        }

        public async ValueTask<ComparisonQueryPlan?> CaptureQueryPlanAsync(CancellationToken cancellationToken)
        {
            var result = await runtime.QueryAsync(scope, queries[0], cancellationToken).ConfigureAwait(false);
            var plan = result.Plan;
            return new ComparisonQueryPlan(
                Access: plan.Access.ToString(),
                IndexName: plan.IndexName,
                UsedSnapshotFallback: false,
                Reason: plan.Reason,
                RowsExamined: 0,
                WorkUnits: 0);
        }

        public async ValueTask DisposeAsync()
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
