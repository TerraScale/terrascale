using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Documents;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.E2E
{
    /// <summary>
    /// TerraBase against the raw vendored Turso driver on one host, with identical workloads (benchmark/dotnet/VsTurso, run by
    /// <c>benchmark/dotnet/VsTurso/run.sh</c>). Compiled into TerraBase.E2E because the local nodes need its harness; without
    /// <c>TERRABASE_VS_TURSO_OUT</c> it skips: it is a benchmark, not a scenario.
    /// Lanes: <c>turso</c> (one upsert per transaction, WAL, synchronous FULL), <c>turso-cdc</c> (the same with change capture,
    /// as TerraBase's writer runs), <c>engine</c> and <c>api</c> (one local node: <see cref="IDocumentService"/> in process, and
    /// its public HTTP API over loopback), <c>engine-3</c> and <c>api-3</c> (gru of gru, sjc and fra, answered after a remote ack).
    /// </summary>
    [Category("benchmark-host")]
    public sealed class VsTursoBenchmark
    {
        private const int Keys = 10_000;
        private static readonly (string Name, int Bytes)[] Sizes = [("small", 256), ("medium", 4096)];
        private static readonly string[] Workloads = ["write", "read", "mixed"];
        private static readonly int[] Concurrencies = [1, 16];

        [Test]
        [Scenario(Nodes.InProcess)]
        [Timeout(6 * 3600 * 1000)]
        public async Task Measures_raw_Turso_against_the_TerraBase_engine_and_API(CancellationToken cancellationToken)
        {
            var output = Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_OUT");
            Skip.When(string.IsNullOrEmpty(output), "the vs-Turso benchmark: run it with benchmark/dotnet/VsTurso/run.sh");
            var seconds = int.Parse(Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_SECONDS") ?? "5", CultureInfo.InvariantCulture);
            var repeats = int.Parse(Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_REPEATS") ?? "3", CultureInfo.InvariantCulture);
            var lanes = (Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_LANES") ?? "turso,turso-cdc,engine,api,floor,engine-3,api-3").Split(',');
            var workloads = (Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_WORKLOADS") ?? string.Join(',', Workloads)).Split(',');
            var duration = TimeSpan.FromSeconds(seconds);
            var cells = new List<Cell>();
            await using var log = new StreamWriter(output!, append: false);
            await log.WriteLineAsync($"# host={Environment.MachineName} cpus={Environment.ProcessorCount} serverGC={GCSettings.IsServerGC} runtime={Environment.Version} seconds={seconds} repeats={repeats}");

            var directory = Directory.CreateTempSubdirectory("vs-turso-").FullName;
            try
            {
                var single = new List<(string Lane, ITarget Target)>();
                foreach (var lane in lanes.Where(lane => lane.StartsWith("turso", StringComparison.Ordinal)))
                {
                    single.Add((lane, new TursoTarget(Path.Combine(directory, lane + ".db"), capture: lane == "turso-cdc")));
                }

                // One node outside a cell: change capture, change groups and the feed run, nothing ships and no write waits for a remote ack.
                await using (var node = lanes.Any(lane => lane is "engine" or "api" or "floor") ? await ManagementApi.StartAsync() : null)
                {
                    if (node is not null)
                    {
                        using var owner = node.As(node.User);
                        var database = await node.CreateManagedDatabaseAsync("bench");
                        await node.SetPlanAsync(node.User, database.Organization, "enterprise", cancellationToken);
                        await node.VerifyProjectBindingAsync(database.Organization, database.Project, owner);
                        var token = node.Issuer.Mint(node.User, descriptor =>
                        {
                            descriptor.Claims["project_id"] = database.Project;
                            descriptor.Claims["scope"] = "databases:read databases:write documents:read documents:write";
                            descriptor.Expires = DateTime.UtcNow.AddHours(12);
                        });
                        using (var data = node.WithToken(token))
                        {
                            await ManagementApi.CreateCollectionAsync(data, database, "bench", cancellationToken);
                        }

                        single.AddRange(Targets(
                            lanes, string.Empty, node.Server.Services, new TenantContext(database.Organization, PlanTier.Enterprise, database.Project, database.Id),
                            node.Server.ClientAddress!, token, $"{database.Data}/collections/bench/documents/by-key?id=", "bench"));
                    }

                    await RunAsync(single, workloads, repeats, duration, cells, log, cancellationToken);
                }

                await using (var cluster = lanes.Any(lane => lane.EndsWith("-3", StringComparison.Ordinal)) ? await Cluster.StartAsync([Cluster.Gru, Cluster.Sjc, Cluster.Fra]) : null)
                {
                    if (cluster is not null)
                    {
                        var (services, database) = cluster.Engine(Cluster.Gru);
                        var (address, token, _) = cluster.Endpoint(Cluster.Gru);
                        var targets = Targets(
                            lanes, "-3", services, database, address, token, $"{cluster.DatabasePath}/collections/{cluster.Collection}/documents/by-key?id=", cluster.Collection);
                        await RunAsync(targets, workloads, repeats, duration, cells, log, cancellationToken);
                    }
                }

                foreach (var (_, target) in single)
                {
                    await target.DisposeAsync();
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }

            await log.WriteLineAsync();
            await log.WriteLineAsync(Summary(cells));
        }

        private static List<(string, ITarget)> Targets(
            string[] lanes, string suffix, IServiceProvider services, TenantContext database, Uri address, string token, string path, string collection)
        {
            var targets = new List<(string, ITarget)>();
            var engine = new EngineTarget(services.GetRequiredService<IDocumentService>(), database, collection);
            if (lanes.Contains("engine" + suffix))
            {
                targets.Add(("engine" + suffix, engine));
            }

            if (lanes.Contains("api" + suffix))
            {
                targets.Add(("api" + suffix, new ApiTarget(address, token, path, engine)));
            }

            // The HTTP floor: the same client and listener answering GET /healthz, so no auth, catalog or storage.
            if (lanes.Contains("floor" + suffix))
            {
                targets.Add(("floor" + suffix, new ApiTarget(address, token, "/healthz?", engine, floor: true)));
            }

            return targets;
        }

        private static async Task RunAsync(
            List<(string Lane, ITarget Target)> targets, string[] workloads, int repeats, TimeSpan duration, List<Cell> cells, StreamWriter log, CancellationToken cancellationToken)
        {
            foreach (var (lane, target) in targets)
            {
                foreach (var (size, bytes) in Sizes)
                {
                    // Every key exists before any cell runs, so reads always hit and writes always replace.
                    var body = new Bodies(size, bytes);
                    for (var first = 0; first < Keys; first += 500)
                    {
                        await target.PreloadAsync([.. Enumerable.Range(first, 500).Select(key => (Bodies.Id(size, key), body.Next(key)))]);
                    }
                }

                await log.WriteLineAsync($"# preloaded {lane}");
                await log.FlushAsync(cancellationToken);
            }

            // Lanes interleave inside each repeat and cell, so a drift of the host hits every lane alike.
            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                foreach (var (size, bytes) in Sizes)
                {
                    var body = new Bodies(size, bytes);
                    foreach (var workload in workloads)
                    {
                        foreach (var concurrency in Concurrencies)
                        {
                            foreach (var (lane, target) in targets)
                            {
                                var cell = await MeasureAsync(target, body, workload, concurrency, duration) with { Lane = lane, Repeat = repeat };
                                cells.Add(cell);
                                await log.WriteLineAsync(JsonSerializer.Serialize(cell, VsTursoJson.Default.Cell));
                                await log.FlushAsync(cancellationToken);
                            }
                        }
                    }
                }
            }
        }

        private static async Task<Cell> MeasureAsync(ITarget target, Bodies body, string workload, int concurrency, TimeSpan duration)
        {
            var warmup = TimeSpan.FromSeconds(1);
            var start = Stopwatch.GetTimestamp();
            var measureFrom = start + (long)(warmup.TotalSeconds * Stopwatch.Frequency);
            var end = measureFrom + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            var latencies = new List<long>[concurrency];
            var errors = 0;
            var cpuBefore = TimeSpan.Zero;
            var gcBefore = 0L;
            var cpuMarked = 0;
            var workers = Enumerable.Range(0, concurrency).Select(worker => Task.Run(async () =>
            {
                var random = new Random(worker * 7919 + concurrency);
                var mine = latencies[worker] = [];
                var counter = (long)worker << 40;
                while (true)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (now >= end)
                    {
                        return;
                    }

                    if (now >= measureFrom && Interlocked.Exchange(ref cpuMarked, 1) == 0)
                    {
                        cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                        gcBefore = GC.GetTotalAllocatedBytes();
                    }

                    var id = Bodies.Id(body.Size, random.Next(Keys));
                    var write = workload == "write" || (workload == "mixed" && random.Next(100) < 20);
                    var json = write ? body.Next(++counter) : null;
                    var began = Stopwatch.GetTimestamp();
                    try
                    {
                        if (json is not null)
                        {
                            await target.PutAsync(id, json);
                        }
                        else
                        {
                            await target.GetAsync(id);
                        }
                    }
                    catch (Exception) when (began >= measureFrom)
                    {
                        Interlocked.Increment(ref errors);
                        continue;
                    }

                    if (began >= measureFrom && Stopwatch.GetTimestamp() <= end)
                    {
                        mine.Add(Stopwatch.GetTimestamp() - began);
                    }
                }
            })).ToArray();
            await Task.WhenAll(workers);
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            var allocated = GC.GetTotalAllocatedBytes() - gcBefore;
            var all = latencies.SelectMany(list => list).Order().ToArray();
            double Micros(double quantile)
            {
                return all.Length == 0 ? 0 : all[Math.Min(all.Length - 1, (int)(quantile * all.Length))] * 1e6 / Stopwatch.Frequency;
            }

            var ops = all.Length;
            return new Cell(
                string.Empty, 0, body.Size, workload, concurrency, Math.Round(ops / duration.TotalSeconds), Math.Round(Micros(0.50), 1), Math.Round(Micros(0.99), 1),
                ops == 0 ? 0 : Math.Round(cpu.TotalMicroseconds / ops, 1), ops == 0 ? 0 : allocated / ops, errors);
        }

        private static string Summary(List<Cell> cells)
        {
            var text = new StringBuilder();
            text.AppendLine("| Size | Workload | Conc | Lane | ops/s median (min–max) | vs turso | p50 µs | p99 µs | CPU µs/op | alloc B/op | errors |");
            text.AppendLine("| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var group in cells.GroupBy(cell => (cell.Size, cell.Workload, cell.Concurrency, cell.Lane)))
            {
                var runs = group.ToList();
                var ops = Median(runs.Select(run => run.OpsPerSecond));
                var baseline = cells.Where(cell => cell.Lane == "turso" && cell.Size == group.Key.Size && cell.Workload == group.Key.Workload && cell.Concurrency == group.Key.Concurrency)
                    .Select(cell => cell.OpsPerSecond).ToList();
                var versus = baseline.Count == 0 ? "" : $"{(ops / Median(baseline) - 1) * 100:+0.0;-0.0} %";
                text.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key.Size} | {group.Key.Workload} | {group.Key.Concurrency} | {group.Key.Lane} | {ops:N0} ({runs.Min(run => run.OpsPerSecond):N0}–{runs.Max(run => run.OpsPerSecond):N0}) | {versus} | {Median(runs.Select(run => run.P50Micros)):0.0} | {Median(runs.Select(run => run.P99Micros)):0.0} | {Median(runs.Select(run => run.CpuMicrosPerOp)):0.0} | {Median(runs.Select(run => (double)run.AllocatedBytesPerOp)):N0} | {runs.Sum(run => run.Errors)} |");
            }

            return text.ToString();
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        }

        internal sealed record Cell(
            string Lane, int Repeat, string Size, string Workload, int Concurrency, double OpsPerSecond, double P50Micros, double P99Micros,
            double CpuMicrosPerOp, long AllocatedBytesPerOp, int Errors);

        /// <summary>JSON documents of one size: a fixed shape with a 13-digit counter, so no write repeats the stored body.</summary>
        private sealed class Bodies
        {
            private readonly byte[] _template;
            private readonly int _counterAt;

            public Bodies(string size, int bytes)
            {
                Size = size;
                const string head = "{\"name\":\"Ada Lovelace\",\"n\":\"0000000000000\",\"tags\":[\"alpha\",\"beta\",\"gamma\"],\"score\":12.5,\"active\":true,\"pad\":\"";
                var pad = bytes - head.Length - 2;
                _template = Encoding.UTF8.GetBytes(head + new string('x', pad) + "\"}");
                _counterAt = head.IndexOf("0000000000000", StringComparison.Ordinal);
            }

            public string Size { get; }

            public static string Id(string size, int key)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{size[0]}-{key:D6}");
            }

            public byte[] Next(long counter)
            {
                var json = (byte[])_template.Clone();
                for (var digit = 12; digit >= 0; digit--, counter /= 10)
                {
                    json[_counterAt + digit] = (byte)('0' + (int)(counter % 10));
                }

                return json;
            }
        }

        private interface ITarget : IAsyncDisposable
        {
            Task PutAsync(string id, byte[] json);

            /// <summary>Writes <paramref name="documents"/> before measuring, in as few commits as the target allows.</summary>
            Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents);

            Task GetAsync(string id);
        }

        /// <summary>
        /// The raw driver as an application would use it with TerraBase's durability: one writer connection (TerraBase has one
        /// per database), each write its own BEGIN IMMEDIATE upsert and COMMIT with synchronous FULL (the WAL is synced before
        /// the commit returns), Turso's default WAL journal, and a connection per concurrent reader. Same table shape as a
        /// TerraBase collection: (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL).
        /// </summary>
        private sealed class TursoTarget : ITarget
        {
            private readonly string _connectionString;
            private readonly SqliteConnection _writer;
            private readonly SqliteCommand _upsert;
            private readonly SemaphoreSlim _gate = new(1, 1);
            private readonly System.Collections.Concurrent.ConcurrentBag<(SqliteConnection, SqliteCommand)> _readers = [];

            public TursoTarget(string file, bool capture)
            {
                _connectionString = $"Data Source={file}";
                _writer = new SqliteConnection(_connectionString);
                _writer.Open();
                Execute(_writer, "PRAGMA synchronous = FULL;");
                Execute(_writer, "CREATE TABLE docs (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);");
                if (capture)
                {
                    Execute(_writer, SqliteSession.CaptureOn);
                }

                _upsert = _writer.CreateCommand();
                _upsert.CommandText = "INSERT INTO docs (id, doc) VALUES ($id, $doc) ON CONFLICT (id) DO UPDATE SET doc = excluded.doc;";
                _upsert.Parameters.AddWithValue("$id", string.Empty);
                _upsert.Parameters.AddWithValue("$doc", string.Empty);
            }

            public async Task PutAsync(string id, byte[] json)
            {
                await _gate.WaitAsync();
                try
                {
                    using var transaction = _writer.BeginTransaction(deferred: false);
                    _upsert.Transaction = transaction;
                    _upsert.Parameters["$id"].Value = id;
                    _upsert.Parameters["$doc"].Value = Encoding.UTF8.GetString(json);
                    _upsert.ExecuteNonQuery();
                    transaction.Commit();
                }
                finally
                {
                    _gate.Release();
                }
            }

            public Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
            {
                using var transaction = _writer.BeginTransaction(deferred: false);
                _upsert.Transaction = transaction;
                foreach (var (id, json) in documents)
                {
                    _upsert.Parameters["$id"].Value = id;
                    _upsert.Parameters["$doc"].Value = Encoding.UTF8.GetString(json);
                    _upsert.ExecuteNonQuery();
                }

                transaction.Commit();
                return Task.CompletedTask;
            }

            public Task GetAsync(string id)
            {
                if (!_readers.TryTake(out var reader))
                {
                    var connection = new SqliteConnection(_connectionString);
                    connection.Open();
                    var command = connection.CreateCommand();
                    command.CommandText = "SELECT doc FROM docs WHERE id = $id;";
                    command.Parameters.AddWithValue("$id", string.Empty);
                    reader = (connection, command);
                }

                try
                {
                    reader.Item2.Parameters["$id"].Value = id;
                    if (reader.Item2.ExecuteScalar() is not string)
                    {
                        throw new InvalidOperationException($"turso: {id} is missing");
                    }
                }
                finally
                {
                    _readers.Add(reader);
                }

                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                _upsert.Dispose();
                _writer.Dispose();
                foreach (var (connection, command) in _readers)
                {
                    command.Dispose();
                    connection.Dispose();
                }

                _gate.Dispose();
                return ValueTask.CompletedTask;
            }

            private static void Execute(SqliteConnection connection, string sql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        /// <summary>The engine path every request handler calls: <see cref="IDocumentService.WriteConfirmedAsync"/> and <see cref="IDocumentService.GetAsync"/>.</summary>
        private sealed class EngineTarget(IDocumentService documents, TenantContext database, string collection) : ITarget
        {
            private static readonly Precondition None = new(null, IfMatchAny: false, IfNoneMatch: false);

            public async Task PutAsync(string id, byte[] json)
            {
                var outcome = await documents.WriteConfirmedAsync(database, [new PutOperation(collection, id, json, None)], indexErrors: false, CancellationToken.None);
                if (outcome is not WriteResult { Durability: WriteResult.LocalDurable or WriteResult.RemoteDurable })
                {
                    throw new InvalidOperationException($"engine: {id} was not written: {Describe(outcome)}");
                }
            }

            private static string Describe(WriteOutcome outcome)
            {
                return outcome switch
                {
                    WriteResult written => written.Durability,
                    DocumentRejected rejected => rejected.Error switch
                    {
                        NotFound error => error.ToString(),
                        Conflict error => error.ToString(),
                        Validation error => error.ToString(),
                        Unavailable error => error.ToString(),
                        OutcomeUnknown error => error.ToString(),
                        _ => "rejected",
                    },
                };
            }

            public async Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> batch)
            {
                for (var first = 0; first < batch.Count; first += 100)
                {
                    var operations = batch.Skip(first).Take(100).Select(document => (DocumentOperation)new PutOperation(collection, document.Id, document.Json, None)).ToList();
                    if (await documents.WriteConfirmedAsync(database, operations, indexErrors: false, CancellationToken.None) is var outcome && outcome is not WriteResult)
                    {
                        throw new InvalidOperationException($"engine: the preload was not written: {Describe(outcome)}");
                    }
                }
            }

            public async Task GetAsync(string id)
            {
                if (await documents.GetAsync(new DocumentKey(database, collection, id), CancellationToken.None) is not StoredDocument)
                {
                    throw new InvalidOperationException($"engine: {id} is missing");
                }
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>The public API over loopback: HTTP/1.1 keep-alive, one pooled connection per concurrent worker, bodies read in full.</summary>
        private sealed class ApiTarget : ITarget
        {
            private static readonly MediaTypeHeaderValue Json = new("application/json");
            private readonly HttpClient _client;
            private readonly string _path;

            private readonly ITarget _preload;

            // The engine target of the same node preloads: the API would only add its own cost to a phase nobody measures.
            private readonly bool _floor;

            public ApiTarget(Uri address, string token, string path, ITarget preload, bool floor = false)
            {
                _preload = preload;
                _floor = floor;
                _client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = Timeout.InfiniteTimeSpan, UseCookies = false })
                {
                    BaseAddress = address,
                    DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
                };
                _path = path;
            }

            public async Task PutAsync(string id, byte[] json)
            {
                if (_floor)
                {
                    await GetAsync(id);
                    return;
                }

                using var content = new ByteArrayContent(json);
                content.Headers.ContentType = Json;
                using var response = await _client.PutAsync(new Uri(_path + id, UriKind.Relative), content);
                _ = await response.Content.ReadAsByteArrayAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"api: PUT {id} answered {(int)response.StatusCode}");
                }
            }

            public Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
            {
                return _floor ? Task.CompletedTask : _preload.PreloadAsync(documents);
            }

            public async Task GetAsync(string id)
            {
                using var response = await _client.GetAsync(new Uri(_path + id, UriKind.Relative));
                _ = await response.Content.ReadAsByteArrayAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"api: GET {id} answered {(int)response.StatusCode}");
                }
            }

            public ValueTask DisposeAsync()
            {
                _client.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [System.Text.Json.Serialization.JsonSerializable(typeof(VsTursoBenchmark.Cell))]
    internal sealed partial class VsTursoJson : System.Text.Json.Serialization.JsonSerializerContext;
}
