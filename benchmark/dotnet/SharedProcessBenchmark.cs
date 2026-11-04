using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Testing;
using static TerraBase.Testing.SharedProcess;

namespace TerraBase.E2E
{
    [Category("benchmark-shared-process")]
    public sealed class SharedProcessBenchmark
    {
        private const int Databases = 1000;

        [Test]
        [Scenario(Nodes.InProcess)]
        [Timeout(3600 * 1000)]
        public async Task Measures_a_thousand_databases_and_a_runaway_neighbour(CancellationToken cancellationToken)
        {
            var output = Environment.GetEnvironmentVariable("TERRABASE_BENCH_SHARED_OUT");
            Skip.When(string.IsNullOrEmpty(output), "shared-process benchmark: set TERRABASE_BENCH_SHARED_OUT");
            var bounded = await ThousandDatabasesAsync(null, cancellationToken);
            var unbounded = await ThousandDatabasesAsync(Databases * 2, cancellationToken);
            var neighbour = await NeighbourAsync(cancellationToken);
            var line = new JsonObject
            {
                ["bounded"] = bounded,
                ["unbounded"] = unbounded,
                ["neighbour"] = neighbour,
                ["cpus"] = Environment.ProcessorCount,
            };
            await File.AppendAllTextAsync(output!, line.ToJsonString() + "\n", cancellationToken);
        }

        private static async Task<JsonObject> ThousandDatabasesAsync(int? maxWriters, CancellationToken cancellationToken)
        {
            await using var api = await ManagementApi.StartAsync(services: maxWriters is { } bound ? services => BoundWriters(services, bound) : null);
            var organization = await api.CreateOrganizationAsync("Bench");
            var project = await api.CreateProjectAsync(organization, "Project");
            await api.SetPlanAsync(api.User, organization, "enterprise", cancellationToken);
            using var client = api.DataClient(await api.Server.IssueNarrowedAsync(organization, project, null, "documents:read", "documents:write"));
            var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
            var before = Rss();
            var clock = Stopwatch.StartNew();
            await Parallel.ForEachAsync(Enumerable.Range(0, Databases), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (i, _) =>
            {
                var database = (await api.CreateManagedDatabaseAsync(organization, project, $"db{i:D4}")).Data;
                Ok(await SqlAsync(client, database, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)"));
                Ok(await SqlAsync(client, database, "INSERT INTO notes (body) VALUES (?)", $"note {i}"));
            });
            var created = clock.Elapsed;
            var writes = new List<double>(Databases);
            for (var i = 0; i < Databases; i++)
            {
                var write = Stopwatch.StartNew();
                Ok(await SqlAsync(client, $"/v1/projects/{project}/databases/db{i:D4}", "INSERT INTO notes (body) VALUES (?)", $"again {i}"));
                writes.Add(write.Elapsed.TotalMilliseconds);
            }

            for (var i = 0; i < Databases; i++)
            {
                var (_, body) = Ok(await SqlAsync(client, $"/v1/projects/{project}/databases/db{i:D4}", "SELECT count(*) FROM notes"));
                if (JsonElement.Parse(body).GetProperty("results")[0].GetProperty("rows")[0][0].GetInt64() != 2)
                {
                    throw new InvalidOperationException($"database {i} lost a row");
                }
            }

            return new JsonObject
            {
                ["max_writers"] = engine.Pool.MaxWriters,
                ["open_writers"] = engine.Pool.OpenWriters,
                ["rss_before_mib"] = before,
                ["rss_after_mib"] = Rss(),
                ["create_seconds"] = Math.Round(created.TotalSeconds, 1),
                ["rewrite_p50_ms"] = Math.Round(Percentile(writes, .5), 2),
                ["rewrite_p99_ms"] = Math.Round(Percentile(writes, .99), 2),
            };
        }

        private static async Task<JsonObject> NeighbourAsync(CancellationToken cancellationToken)
        {
            await using var api = await ManagementApi.StartAsync(settings => settings["storage-threads"] = "4");
            var noisy = await api.CreateManagedDatabaseAsync();
            var quiet = await api.CreateManagedDatabaseAsync();
            using var noisyClient = api.DataClient(await api.Server.IssueNarrowedAsync(noisy.Organization, noisy.Project, null, "documents:read", "documents:write"));
            using var quietClient = api.DataClient(await api.Server.IssueNarrowedAsync(quiet.Organization, quiet.Project, null, "documents:read", "documents:write"));
            Ok(await SqlAsync(quietClient, quiet.Data, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)"));
            Ok(await SqlAsync(quietClient, quiet.Data, "INSERT INTO notes (id, body) VALUES (1, 'quiet')"));
            var alone = await ReadsAsync(quietClient, quiet.Data, 200);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var statuses = new List<HttpStatusCode>();
            var runaway = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var (status, _) = await SqlAsync(noisyClient, noisy.Data, Endless);
                    lock (statuses)
                    {
                        statuses.Add(status);
                    }
                }
            })).ToList();
            List<double> loaded;
            try
            {
                loaded = await ReadsAsync(quietClient, quiet.Data, 400);
            }
            finally
            {
                await stop.CancelAsync();
                await Task.WhenAll(runaway);
            }

            return new JsonObject
            {
                ["alone_p50_ms"] = Math.Round(Percentile(alone, .5), 2),
                ["alone_p99_ms"] = Math.Round(Percentile(alone, .99), 2),
                ["loaded_p50_ms"] = Math.Round(Percentile(loaded, .5), 2),
                ["loaded_p99_ms"] = Math.Round(Percentile(loaded, .99), 2),
                ["runaway_requests"] = statuses.Count,
                ["runaway_throttled"] = statuses.Count(status => status == HttpStatusCode.TooManyRequests),
            };
        }

        private static async Task<List<double>> ReadsAsync(HttpClient client, string database, int count)
        {
            var samples = new List<double>(count);
            using var pace = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
            for (var i = 0; i < count && await pace.WaitForNextTickAsync(); i++)
            {
                var clock = Stopwatch.StartNew();
                Ok(await SqlAsync(client, database, "SELECT body FROM notes WHERE id = 1"));
                samples.Add(clock.Elapsed.TotalMilliseconds);
            }

            return samples;
        }

        private static (HttpStatusCode Status, string Body) Ok((HttpStatusCode Status, string Body) response)
        {
            return response.Status == HttpStatusCode.OK ? response : throw new InvalidOperationException($"SQL answered {(int)response.Status}: {response.Body}");
        }

        private static long Rss()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var line = File.ReadLines("/proc/self/status").First(text => text.StartsWith("VmRSS:", StringComparison.Ordinal));
            return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) / 1024;
        }
    }
}
