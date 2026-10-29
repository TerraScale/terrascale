using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Documents;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.E2E
{
    [Category("benchmark-host")]
    public sealed class VsTursoBenchmark
    {
        internal const int Keys = 10_000;
        private static readonly (string Name, int Bytes)[] Sizes = [("small", 256), ("medium", 4096)];
        private static readonly string[] Workloads = ["write", "read", "mixed"];
        private static readonly int[] DefaultConcurrencies = [1, 16];

        [Test]
        [Scenario(Nodes.InProcess)]
        [Timeout(6 * 3600 * 1000)]
        public async Task Measures_raw_Turso_against_the_TerraBase_engine_and_API(CancellationToken cancellationToken)
        {
            var output = Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_OUT");
            Skip.When(string.IsNullOrEmpty(output), "the vs-Turso benchmark: run it with benchmark/dotnet/VsTurso/run.sh");
            var seconds = int.Parse(Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_SECONDS") ?? "5", CultureInfo.InvariantCulture);
            var repeats = int.Parse(Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_REPEATS") ?? "3", CultureInfo.InvariantCulture);
            var plugins = typeof(VsTursoBenchmark).Assembly.GetTypes()
                .Where(type => typeof(IVsTursoLane).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
                .Select(type => (IVsTursoLane)Activator.CreateInstance(type)!).ToList();
            var lanes = (Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_LANES")
                ?? string.Join(',', ["turso", "turso-cdc", "engine", "api", "floor", .. plugins.Select(plugin => plugin.Name)])).Split(',');
            var workloads = (Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_WORKLOADS") ?? string.Join(',', Workloads)).Split(',');
            int[] concurrencies = Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_CONCURRENCY") is { Length: > 0 } list
                ? [.. list.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture))]
                : DefaultConcurrencies;
            var duration = TimeSpan.FromSeconds(seconds);
            var cells = new List<Cell>();
            await using var log = new StreamWriter(output!, append: false);
            await log.WriteLineAsync($"# host={Environment.MachineName} cpus={Environment.ProcessorCount} serverGC={GCSettings.IsServerGC} runtime={Environment.Version} seconds={seconds} repeats={repeats}");

            var directory = Directory.CreateTempSubdirectory("vs-turso-").FullName;
            try
            {
                var single = new List<(string Lane, ITarget Target)>();
                foreach (var lane in lanes.Where(lane => lane is "turso" or "turso-cdc"))
                {
                    single.Add((lane, new TursoTarget(Path.Combine(directory, lane + ".db"), capture: lane == "turso-cdc")));
                }

                using (var attribution = new ServerAttribution())
                await using (var node = lanes.Any(lane => lane is "engine" or "api" or "api-exceptions" or "floor" || plugins.Any(plugin => plugin.Name == lane))
                    ? await ManagementApi.StartAsync(services: attribution.Install) : null)
                {
                    if (node is not null)
                    {
                        using var owner = node.As(node.User);
                        var database = await node.CreateManagedDatabaseAsync("bench");
                        await node.SetPlanAsync(node.User, database.Organization, "enterprise", cancellationToken);
                        await node.VerifyCatalogAsync(CancellationToken.None);
                        var keyId = EntityIds.New().ToString();
                        var key = keyId + "_" + new Faker().Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");
                        var token = node.Issuer.Mint(keyId, descriptor =>
                        {
                            descriptor.Claims["project_id"] = database.Project;
                            descriptor.Claims["scope"] = "databases:read databases:write documents:read documents:write";
                            descriptor.Claims["key_id"] = keyId;
                            descriptor.Claims["key_gen"] = TestIssuer.KeyGeneration(key);
                            descriptor.Expires = DateTime.UtcNow.AddHours(12);
                        });
                        node.Issuer.GrantClient(key, () => token);
                        await log.WriteLineAsync($"# token: synthetic API key {keyId} (key_id, key_gen), project-scoped, 12 h");
                        using (var data = node.WithToken(token))
                        {
                            await ManagementApi.CreateCollectionAsync(data, database, "bench", cancellationToken);
                        }

                        single.AddRange(Targets(
                            lanes, string.Empty, node.Server.Services, new TenantContext(database.Organization, PlanTier.Enterprise, database.Project, database.Id),
                            node.Server.ClientAddress!, token, $"{database.Data}/collections/bench/documents/by-key?id=", "bench"));
                        var setup = new VsTursoNode(node, new TenantContext(database.Organization, PlanTier.Enterprise, database.Project, database.Id), token, "bench", directory);
                        foreach (var plugin in plugins.Where(plugin => lanes.Contains(plugin.Name)))
                        {
                            single.Add((plugin.Name, await plugin.StartAsync(setup, cancellationToken)));
                        }
                    }

                    await RunAsync(single, workloads, concurrencies, repeats, duration, cells, log, node is null ? null : attribution, cancellationToken);
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
            if (cells.Count == 0 || cells.Any(cell => cell.Errors != 0 || cell.OpsPerSecond == 0))
            {
                throw new InvalidOperationException("The benchmark contains failed or empty cells; retain the evidence and reject the run.");
            }
        }

        private static List<(string, ITarget)> Targets(
            string[] lanes, string suffix, IServiceProvider services, TenantContext database, Uri address, string token, string path, string collection)
        {
            var targets = new List<(string, ITarget)>();
            var documents = services.GetRequiredService<IDocumentService>();
            var engine = new EngineTarget(documents is AttributedDocuments attributed ? attributed.Inner : documents, database, collection);
            if (lanes.Contains("engine" + suffix))
            {
                targets.Add(("engine" + suffix, engine));
            }

            if (lanes.Contains("api" + suffix))
            {
                targets.Add(("api" + suffix, new ApiTarget(address, token, path, engine)));
            }

            if (lanes.Contains("api-exceptions" + suffix))
            {
                targets.Add(("api-exceptions" + suffix, new ApiTarget(address, token, path, engine)));
            }

            if (lanes.Contains("floor" + suffix))
            {
                targets.Add(("floor" + suffix, new ApiTarget(address, token, "/healthz?", engine, floor: true)));
            }

            return targets;
        }

        private static async Task RunAsync(
            List<(string Lane, ITarget Target)> targets, string[] workloads, int[] concurrencies, int repeats, TimeSpan duration, List<Cell> cells, StreamWriter log,
            ServerAttribution? attribution, CancellationToken cancellationToken)
        {
            foreach (var (lane, target) in targets)
            {
                foreach (var (size, bytes) in Sizes)
                {
                    var body = new Bodies(size, bytes);
                    for (var first = 0; first < Keys; first += 500)
                    {
                        await target.PreloadAsync([.. Enumerable.Range(first, 500).Select(key => (Bodies.Id(size, key), body.Next(key)))]);
                    }
                }

                await log.WriteLineAsync($"# preloaded {lane}");
                await log.FlushAsync(cancellationToken);
            }

            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                foreach (var (size, bytes) in Sizes)
                {
                    var body = new Bodies(size, bytes);
                    foreach (var workload in workloads)
                    {
                        foreach (var concurrency in concurrencies)
                        {
                            foreach (var (lane, target) in targets)
                            {
                                var window = workload == "read" && lane is "sql" or "pg"
                                    ? TimeSpan.FromSeconds(Math.Max(30, duration.TotalSeconds)) : duration;
                                var timing = lane is "api" or "api-exceptions" or "api-json" or "sdk" ? attribution : null;
                                var cell = await MeasureAsync(target, body, workload, concurrency, window, timing, lane) with { Lane = lane, Repeat = repeat };
                                cells.Add(cell);
                                await log.WriteLineAsync(JsonSerializer.Serialize(cell, VsTursoJson.Default.Cell));
                                await log.FlushAsync(cancellationToken);
                                if (attribution is not null && (lane is "api" or "api-exceptions" or "api-json" or "sdk") && cell.Server is null)
                                {
                                    throw new InvalidOperationException($"{lane}: HTTP attribution is missing; reject this run.");
                                }
                            }
                        }
                    }
                }
            }
        }

        private static async Task<Cell> MeasureAsync(ITarget target, Bodies body, string workload, int concurrency, TimeSpan duration, ServerAttribution? attribution, string lane)
        {
            var countExceptions = lane is "api-exceptions";
            var warmup = TimeSpan.FromSeconds(10);
            var start = Stopwatch.GetTimestamp();
            var measureFrom = start + (long)(warmup.TotalSeconds * Stopwatch.Frequency);
            var end = measureFrom + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            attribution?.Open(measureFrom, end);
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
            long canceledExceptions = 0;
            void CountCanceled(object? _, FirstChanceExceptionEventArgs args)
            {
                if (args.Exception is not OperationCanceledException)
                {
                    return;
                }

                var now = Stopwatch.GetTimestamp();
                if (now >= measureFrom && now <= end)
                {
                    Interlocked.Increment(ref canceledExceptions);
                }
            }

            if (countExceptions)
            {
                AppDomain.CurrentDomain.FirstChanceException += CountCanceled;
            }

            try
            {
                await Task.WhenAll(workers);
            }
            finally
            {
                if (countExceptions)
                {
                    AppDomain.CurrentDomain.FirstChanceException -= CountCanceled;
                }
            }

            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            var allocated = GC.GetTotalAllocatedBytes() - gcBefore;
            var server = attribution?.Close();
            var all = latencies.SelectMany(list => list).Order().ToArray();
            double Micros(double quantile)
            {
                return all.Length == 0 ? 0 : all[Math.Min(all.Length - 1, (int)(quantile * all.Length))] * 1e6 / Stopwatch.Frequency;
            }

            var ops = all.Length;
            return new Cell(
                string.Empty, 0, body.Size, workload, concurrency, Math.Round(ops / duration.TotalSeconds, 3), Math.Round(Micros(0.50), 1), Math.Round(Micros(0.99), 1),
                ops == 0 ? 0 : Math.Round(cpu.TotalMicroseconds / ops, 1), ops == 0 ? 0 : allocated / ops, errors, ops, duration.TotalSeconds, server, countExceptions ? canceledExceptions : null);
        }

        private static string Summary(List<Cell> cells)
        {
            var text = new StringBuilder();
            text.AppendLine("SQL lanes perform point SELECTs through the current full-table projection. api-query writes one-put transactions; api-batch reads/writes 16 documents per request. Other point lanes handle one document. These are different API paths, not equivalent storage work.");
            text.AppendLine();
            text.AppendLine("| Size | Workload | Conc | Lane | ops/s median (min–max) | vs baseline | p50 µs | p99 µs | CPU µs/op | alloc B/op | errors |");
            text.AppendLine("| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var group in cells.GroupBy(cell => (cell.Size, cell.Workload, cell.Concurrency, cell.Lane)))
            {
                var runs = group.ToList();
                var ops = Median(runs.Select(run => run.OpsPerSecond));
                const string against = "turso";
                var baseline = cells.Where(cell => cell.Lane == against && cell.Size == group.Key.Size && cell.Workload == group.Key.Workload && cell.Concurrency == group.Key.Concurrency)
                    .Select(cell => cell.OpsPerSecond).ToList();
                var versus = baseline.Count == 0 || group.Key.Lane == against || group.Key.Lane == "api-batch" ? "" : $"{(ops / Median(baseline) - 1) * 100:+0.0;-0.0} % vs {against}";
                text.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key.Size} | {group.Key.Workload} | {group.Key.Concurrency} | {group.Key.Lane} | {ops:N0} ({runs.Min(run => run.OpsPerSecond):N0}–{runs.Max(run => run.OpsPerSecond):N0}) | {versus} | {Median(runs.Select(run => run.P50Micros)):0.0} | {Median(runs.Select(run => run.P99Micros)):0.0} | {Median(runs.Select(run => run.CpuMicrosPerOp)):0.0} | {Median(runs.Select(run => (double)run.AllocatedBytesPerOp)):N0} | {runs.Sum(run => run.Errors)} |");
            }

            text.AppendLine();
            text.AppendLine("Per-request wall time inside the node (hosting activity), medians of each repeat's quantiles; instrumentation included, not CPU.");
            text.AppendLine();
            text.AppendLine("| Size | Workload | Conc | Lane | requests (sampled) | server p50 / p99 µs | engine p50 / p99 µs | overhead p50 / p99 µs | overhead / engine p50 |");
            text.AppendLine("| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (var group in cells.Where(cell => cell.Server is not null).GroupBy(cell => (cell.Size, cell.Workload, cell.Concurrency, cell.Lane)))
            {
                var runs = group.Select(cell => cell.Server!).ToList();
                double Of(Func<ServerStats, double> value)
                {
                    return Median(runs.Select(value));
                }
                text.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key.Size} | {group.Key.Workload} | {group.Key.Concurrency} | {group.Key.Lane} | {Of(run => run.Requests):N0} ({Of(run => run.Sampled):N0}) | {Of(run => run.ServerP50Micros):0.0} / {Of(run => run.ServerP99Micros):0.0} | {Of(run => run.EngineP50Micros):0.0} / {Of(run => run.EngineP99Micros):0.0} | {Of(run => run.OverheadP50Micros):0.0} / {Of(run => run.OverheadP99Micros):0.0} | {Of(run => run.OverheadP50Micros) / Of(run => run.EngineP50Micros) * 100:0.0} % |");
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
            double CpuMicrosPerOp, long AllocatedBytesPerOp, int Errors, int Samples, double Seconds, ServerStats? Server = null, long? CanceledExceptions = null);

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
                return string.Create(CultureInfo.InvariantCulture, $"01JZZZZZZZZZZZ{char.ToUpperInvariant(size[0])}{key:D11}");
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

    }

    internal interface ITarget : IAsyncDisposable
    {
        Task PutAsync(string id, byte[] json);

        Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents);

        Task GetAsync(string id);
    }

    [System.Text.Json.Serialization.JsonSerializable(typeof(VsTursoBenchmark.Cell))]
    internal sealed partial class VsTursoJson : System.Text.Json.Serialization.JsonSerializerContext;
}
