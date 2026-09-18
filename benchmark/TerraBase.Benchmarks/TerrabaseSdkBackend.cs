#if TERRASCALE_SDK
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using TerraBase.Api;
using TerraBase.StorageEngine;
using TerraScale.DotNet;

namespace TerraBase.Benchmarks;

/// <summary>
/// The TerraBase lane driven through the official TerraScale.DotNet SDK
/// (<see cref="TerraScaleClient"/>) instead of a hand-rolled HTTP client, so the
/// measured interface is the one real consumers use.
/// </summary>
internal static partial class EngineComparisonWorkload
{
    private sealed record BenchDocument(string Name, int Group, string Payload);

    private sealed class TerrabaseSdkBackend : IComparisonBackend
    {
        private static readonly JsonSerializerOptions DocumentJsonOptions = new(JsonSerializerDefaults.Web);

        private readonly WebApplication app;
        private readonly EmbeddedTerraBaseHost embeddedHost;
        private readonly HttpClient http;
        private readonly TerraScaleClient client;
        private readonly string payload;
        private readonly TerraScaleDocumentQuery[] queries;

        private TerrabaseSdkBackend(
            WebApplication app,
            EmbeddedTerraBaseHost embeddedHost,
            HttpClient http,
            TerraScaleClient client,
            string payload,
            TerraScaleDocumentQuery[] queries)
        {
            this.app = app;
            this.embeddedHost = embeddedHost;
            this.http = http;
            this.client = client;
            this.payload = payload;
            this.queries = queries;
        }

        public string Engine => "terrabase-sdk";

        public WebApplication App => app;

        public string? EngineNotes => "durable StorageEngine backend; the TerraScale.DotNet SDK (TerraScaleClient) over in-process embedded dispatch (TerraBaseHost.CreateEmbedded: the full routing/placement/endpoint pipeline with no socket — the embedded class LiteDB itself occupies); automatic index on the name field built and activated before warm-up";

        public static async ValueTask<TerrabaseSdkBackend> OpenAsync(
            string storagePath,
            int datasetSize,
            int payloadSize,
            StorageWalDurability durability = StorageWalDurability.Synced,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(storagePath);
            var payload = new string('x', payloadSize);
            var queries = Enumerable.Range(0, datasetSize)
                .Select(index => new TerraScaleDocumentQuery(
                    Predicates:
                    [
                        new TerraScaleDocumentPredicate(
                            "name",
                            TerraScaleQueryOperator.Equal,
                            JsonSerializer.SerializeToElement(TerrabaseHttpBackend.SeedName(index))),
                    ],
                    Projection: ["payload"],
                    Limit: 10))
                .ToArray();
            var embeddedHost = await TerrabaseHttpBackend.CreateBenchmarkEmbeddedHostAsync(
                storagePath, durability, cancellationToken).ConfigureAwait(false);
            try
            {
                var http = new HttpClient(embeddedHost.CreateHandler())
                {
                    // Loopback keeps the SDK's endpoint validation happy; the
                    // handler dispatches in process and never dials a socket.
                    BaseAddress = new Uri("http://127.0.0.1/"),
                };
                var client = new TerraScaleClient(
                    http,
                    new TerraScaleClientOptions
                    {
                        BaseAddress = http.BaseAddress!,
                        ApiKey = "benchmark-key",
                        // Let the host own request identity; per-operation
                        // client-side ids only add work to the measured path.
                        RequestIdFactory = null,
                    });
                return new TerrabaseSdkBackend(embeddedHost.Application, embeddedHost, http, client, payload, queries);
            }
            catch
            {
                await embeddedHost.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask SeedAsync(int index, CancellationToken cancellationToken)
        {
            var document = new BenchDocument(TerrabaseHttpBackend.SeedName(index), index % 100, payload);
            var created = await client.Documents.CreateAsync(DatabaseRoute, Collection, TerrabaseHttpBackend.SeedId(index), document, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(created.ChangeToken))
            {
                throw new InvalidOperationException($"TerraBase SDK seed {TerrabaseHttpBackend.SeedId(index)} returned no revision.");
            }
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> ReadAsync(int index, int slot, CancellationToken cancellationToken)
        {
            var document = await client.Documents.GetAsync(DatabaseRoute, Collection, TerrabaseHttpBackend.SeedId(index), cancellationToken).ConfigureAwait(false);
            if (document.Data.GetProperty("name").GetString() != TerrabaseHttpBackend.SeedName(index))
            {
                throw new InvalidOperationException($"TerraBase SDK point read returned the wrong document for {TerrabaseHttpBackend.SeedId(index)}.");
            }

            return (JsonMarshal.GetRawUtf8Value(document.Data).Length, 0);
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> QueryAsync(int index, int slot, CancellationToken cancellationToken)
        {
            var result = await client.Documents.QueryAsync(DatabaseRoute, Collection, queries[index], cancellationToken).ConfigureAwait(false);
            if (result.Documents.Count != 1 || result.Documents[0].Id != TerrabaseHttpBackend.SeedId(index))
            {
                throw new InvalidOperationException($"TerraBase SDK query for {TerrabaseHttpBackend.SeedName(index)} did not return exactly {TerrabaseHttpBackend.SeedId(index)}.");
            }

            long bytes = 0;
            foreach (var row in result.Documents)
            {
                bytes += JsonMarshal.GetRawUtf8Value(row.Value).Length;
            }

            return (bytes, 0);
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> WriteAsync(long sequence, int slot, CancellationToken cancellationToken)
        {
            var document = new BenchDocument(TerrabaseHttpBackend.WriteName(sequence), (int)(sequence % 100), payload);
            var created = await client.Documents.CreateAsync(DatabaseRoute, Collection, TerrabaseHttpBackend.WriteId(sequence), document, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(created.ChangeToken))
            {
                throw new InvalidOperationException($"TerraBase SDK write {TerrabaseHttpBackend.WriteId(sequence)} returned no revision.");
            }

            return (0, JsonSerializer.SerializeToUtf8Bytes(document, DocumentJsonOptions).Length);
        }

        public async ValueTask<ComparisonQueryPlan?> CaptureQueryPlanAsync(CancellationToken cancellationToken)
        {
            var result = await client.Documents.QueryAsync(DatabaseRoute, Collection, queries[0], cancellationToken).ConfigureAwait(false);
            return new ComparisonQueryPlan(
                Access: result.Plan.Access is { Length: > 0 } access ? access : "unknown",
                IndexName: result.Plan.IndexName,
                UsedSnapshotFallback: result.Plan.UsedSnapshotFallback,
                Reason: result.Plan.Reason,
                RowsExamined: result.Work.RowsExamined,
                WorkUnits: result.Work.WorkUnits);
        }

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            await embeddedHost.DisposeAsync().ConfigureAwait(false);
        }
    }
}
#endif
