using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Tenancy;

namespace TerraBase.E2E
{
    /// <summary>
    /// Splits every measured HTTP request one node answers into server time (ASP.NET Core's HttpRequestIn activity: hosting's
    /// context creation to the response's completion), engine time (its <see cref="IDocumentService.GetAsync"/> and
    /// <see cref="IDocumentService.WriteConfirmedAsync"/> calls) and their difference, the API's overhead. All wall time, not
    /// CPU; the listener, the activity and one sample per request add their own cost to what they measure (VsTurso/ATTRIBUTION.md).
    /// </summary>
    internal sealed class ServerAttribution : IDisposable
    {
        // A capped reservoir: a cell keeps at most this many paired timings (256 KiB) at any read rate.
        private const int Capacity = 16_384;
        private const string Hosting = "Microsoft.AspNetCore";
        private const string RequestIn = "Microsoft.AspNetCore.Hosting.HttpRequestIn";

        // Unique per instance, so another node's attribution in this process never reads or records these samples.
        private readonly string _key = "vs-turso-attribution-" + Guid.NewGuid().ToString("N");
        private readonly ActivityListener _listener;
        private Capture? _active;

        public ServerAttribution()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == Hosting,
                Sample = (ref _) => ActivitySamplingResult.PropagationData,
                ActivityStarted = activity =>
                {
                    var now = Stopwatch.GetTimestamp();
                    if (activity.OperationName == RequestIn && Volatile.Read(ref _active) is { } capture && now >= capture.From && now <= capture.Until)
                    {
                        activity.SetCustomProperty(_key, new Sample(capture));
                    }
                },
                ActivityStopped = activity =>
                {
                    if (activity.GetCustomProperty(_key) is Sample { Calls: > 0 } sample)
                    {
                        sample.Capture.Record(activity.Duration.Ticks, Interlocked.Read(ref sample.EngineTicks));
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        /// <summary>Replaces the node's <see cref="IDocumentService"/> singleton with one that times it inside requests.</summary>
        public void Install(IServiceCollection services)
        {
            var original = services.Last(descriptor => descriptor.ServiceType == typeof(IDocumentService) && !descriptor.IsKeyedService);
            var factory = original.ImplementationFactory ?? throw new InvalidOperationException("IDocumentService is no longer registered by a factory");
            services.Remove(original);
            services.AddSingleton<IDocumentService>(provider => new AttributedDocuments((IDocumentService)factory(provider), this));
        }

        /// <summary>Starts one cell's capture using the client's monotonic measurement window.</summary>
        public void Open(long from, long until)
        {
            Volatile.Write(ref _active, new Capture(from, until));
        }

        public ServerStats? Close()
        {
            return Interlocked.Exchange(ref _active, null)?.Close();
        }

        /// <summary>The current request's sample when it belongs to the open capture; null outside one.</summary>
        internal Sample? Join()
        {
            if (Volatile.Read(ref _active) is not { } capture)
            {
                return null;
            }

            for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
            {
                if (activity.OperationName == RequestIn && activity.Source.Name == Hosting)
                {
                    if (activity.GetCustomProperty(_key) is Sample joined && joined.Capture == capture)
                    {
                        Interlocked.Increment(ref joined.Calls);
                        return joined;
                    }

                    return null;
                }
            }

            return null;
        }

        public void Dispose()
        {
            _listener.Dispose();
        }

        internal sealed class Sample(Capture capture)
        {
            public long EngineTicks;
            public int Calls;

            public Capture Capture => capture;
        }

        internal sealed class Capture(long from, long until)
        {
            private readonly (long Server, long Engine)[] _samples = new (long, long)[Capacity];
            private long _seen;
            private bool _closed;

            public long From => from;

            public long Until => until;

            public void Record(long serverTicks, long engineTicks)
            {
                lock (_samples)
                {
                    if (_closed || Stopwatch.GetTimestamp() > Until)
                    {
                        return;
                    }

                    var seen = _seen++;
                    var slot = seen < Capacity ? seen : Random.Shared.NextInt64(seen + 1);
                    if (slot < Capacity)
                    {
                        // Record the pair under the same lock; Close excludes all later writers.
                        _samples[slot] = (serverTicks, engineTicks);
                    }
                }
            }

            /// <summary>Stops recording; requests hosting completes later are dropped. Null when no request was measured.</summary>
            public ServerStats? Close()
            {
                lock (_samples)
                {
                    _closed = true;
                }

                var count = (int)Math.Min(_seen, Capacity);
                if (count == 0)
                {
                    return null;
                }

                var server = _samples.Take(count).Select(sample => sample.Server).Order().ToArray();
                var engine = _samples.Take(count).Select(sample => sample.Engine).Order().ToArray();
                var overhead = _samples.Take(count).Select(sample => sample.Server - sample.Engine).Order().ToArray();
                static double Micros(long[] sorted, double quantile)
                {
                    return Math.Round(sorted[Math.Min(sorted.Length - 1, (int)(quantile * sorted.Length))] / 10.0, 1);
                }

                return new ServerStats(
                    _seen, count, Micros(server, 0.50), Micros(server, 0.99), Micros(engine, 0.50), Micros(engine, 0.99), Micros(overhead, 0.50), Micros(overhead, 0.99));
            }
        }
    }

    /// <summary>Per-request wall-time quantiles in µs, each sorted on its own: overhead p50 is not server p50 minus engine p50.</summary>
    internal sealed record ServerStats(
        long Requests, int Sampled, double ServerP50Micros, double ServerP99Micros, double EngineP50Micros, double EngineP99Micros,
        double OverheadP50Micros, double OverheadP99Micros);

    /// <summary>Times the two calls the document routes make; every other member forwards untouched.</summary>
    internal sealed class AttributedDocuments(IDocumentService inner, ServerAttribution attribution) : IDocumentService
    {
        public IDocumentService Inner => inner;

        public ValueTask<DocumentRead> GetAsync(DocumentKey key, CancellationToken cancellationToken)
        {
            return attribution.Join() is { } sample ? TimedAsync(sample, Stopwatch.GetTimestamp(), inner.GetAsync(key, cancellationToken)) : inner.GetAsync(key, cancellationToken);
        }

        public ValueTask<WriteOutcome> WriteConfirmedAsync(
            TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, bool waitForRemote, CancellationToken cancellationToken)
        {
            return attribution.Join() is { } sample
                ? TimedAsync(sample, Stopwatch.GetTimestamp(), inner.WriteConfirmedAsync(database, operations, indexErrors, waitForRemote, cancellationToken))
                : inner.WriteConfirmedAsync(database, operations, indexErrors, waitForRemote, cancellationToken);
        }

        // began is read before the call starts, so its synchronous part counts as engine time.
        private static async ValueTask<T> TimedAsync<T>(ServerAttribution.Sample sample, long began, ValueTask<T> call)
        {
            try
            {
                return await call;
            }
            finally
            {
                Interlocked.Add(ref sample.EngineTicks, Stopwatch.GetElapsedTime(began).Ticks);
            }
        }

        public ValueTask<CollectionSchemaPreviewResult> PreviewCollectionSchemaAsync(TenantContext database, string name, JsonElement schema, string? cursor, CancellationToken cancellationToken)
        {
            return inner.PreviewCollectionSchemaAsync(database, name, schema, cursor, cancellationToken);
        }

        public ValueTask<CollectionRulesResult> GetCollectionRulesAsync(TenantContext database, string name, CancellationToken cancellationToken)
        {
            return inner.GetCollectionRulesAsync(database, name, cancellationToken);
        }

        public ValueTask<CollectionRulesResult> SetCollectionRulesAsync(TenantContext database, string name, CollectionAccessRules rules, CancellationToken cancellationToken)
        {
            return inner.SetCollectionRulesAsync(database, name, rules, cancellationToken);
        }

        public ValueTask<CollectionSchemaResult> GetCollectionSchemaAsync(TenantContext database, string name, CancellationToken cancellationToken)
        {
            return inner.GetCollectionSchemaAsync(database, name, cancellationToken);
        }

        public ValueTask<CollectionSchemaResult> SetCollectionSchemaAsync(TenantContext database, string name, JsonElement? schema, CancellationToken cancellationToken)
        {
            return inner.SetCollectionSchemaAsync(database, name, schema, cancellationToken);
        }

        public ValueTask<CollectionTtlResult> GetCollectionTtlAsync(TenantContext database, string name, CancellationToken cancellationToken)
        {
            return inner.GetCollectionTtlAsync(database, name, cancellationToken);
        }

        public ValueTask<CollectionTtlResult> SetCollectionTtlAsync(TenantContext database, string name, string? path, CancellationToken cancellationToken)
        {
            return inner.SetCollectionTtlAsync(database, name, path, cancellationToken);
        }

        public ValueTask<CollectionResult> CreateCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
        {
            return inner.CreateCollectionAsync(database, name, cancellationToken);
        }

        public ValueTask<CollectionResult> GetCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
        {
            return inner.GetCollectionAsync(database, name, cancellationToken);
        }

        public ValueTask<Found<CollectionPage>> ListCollectionsAsync(TenantContext database, string? afterName, int limit, CancellationToken cancellationToken)
        {
            return inner.ListCollectionsAsync(database, afterName, limit, cancellationToken);
        }

        public ValueTask<CollectionResult> DropCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
        {
            return inner.DropCollectionAsync(database, name, cancellationToken);
        }

        public void AcceptRead(DocumentKey key, StoredDocument document)
        {
            inner.AcceptRead(key, document);
        }

        public void AcceptRead(TenantContext database, string collection, QueryPage page)
        {
            inner.AcceptRead(database, collection, page);
        }

        public ValueTask<QueryResult> QueryAsync(TenantContext database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken)
        {
            return inner.QueryAsync(database, query, countLimit, cancellationToken);
        }

        public IAsyncEnumerable<ExportRow> ExportAsync(TenantContext database, string? afterCollection, string? afterId, CancellationToken cancellationToken)
        {
            return inner.ExportAsync(database, afterCollection, afterId, cancellationToken);
        }

        public ValueTask<WriteOutcome> WriteAsync(TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken)
        {
            return inner.WriteAsync(database, operations, indexErrors, cancellationToken);
        }

        public ValueTask<ApplyOutcome> ApplyAsync(IStorageTransaction transaction, DocumentOperation operation, CancellationToken cancellationToken)
        {
            return inner.ApplyAsync(transaction, operation, cancellationToken);
        }

        public ValueTask<QueryResult> QueryAsync(
            IStorageTransaction transaction, DocumentQuery query, long? countLimit, CancellationToken cancellationToken, QueryObservation? observation = null)
        {
            return inner.QueryAsync(transaction, query, countLimit, cancellationToken, observation);
        }
    }
}
