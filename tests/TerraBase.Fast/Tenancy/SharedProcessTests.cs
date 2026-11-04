using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;
using static TerraBase.Testing.SharedProcess;

namespace TerraBase.Fast.Tenancy;

public sealed class SharedProcessTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_runaway_tenant_is_interrupted_and_throttled_while_another_tenants_reads_stay_fast()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["storage-threads"] = "4");
        var noisy = await api.CreateManagedDatabaseAsync();
        var quiet = await api.CreateManagedDatabaseAsync();
        using var noisyClient = api.DataClient(await api.Server.IssueNarrowedAsync(noisy.Organization, noisy.Project, null, "sql:read", "sql:write"));
        using var quietClient = api.DataClient(await api.Server.IssueNarrowedAsync(quiet.Organization, quiet.Project, null, "sql:read", "sql:write"));
        await Ok(SqlAsync(quietClient, quiet.Data, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)"));
        await Ok(SqlAsync(quietClient, quiet.Data, "INSERT INTO notes (id, body) VALUES (1, ?)", Faker.Lorem.Sentence()));
        var baseline = await ReadsAsync(quietClient, quiet.Data, 20);

        using var stop = new CancellationTokenSource();
        var outcomes = new List<(HttpStatusCode Status, string Body, TimeSpan Took)>();
        var throttledSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runaway = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var clock = Stopwatch.StartNew();
                var (status, body) = await SqlAsync(noisyClient, noisy.Data, Endless);
                lock (outcomes)
                {
                    outcomes.Add((status, body, clock.Elapsed));
                }

                if (status == HttpStatusCode.TooManyRequests)
                {
                    throttledSeen.TrySetResult();
                }
            }
        })).ToList();
        List<double> loaded;
        try
        {
            loaded = await ReadsAsync(quietClient, quiet.Data, 60);
            await throttledSeen.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(runaway);
        }

        var interrupted = outcomes.Where(outcome => outcome.Body.Contains("\"query_canceled\"", StringComparison.Ordinal)).ToList();
        var (_, throttled, _) = outcomes.First(outcome => outcome.Status == HttpStatusCode.TooManyRequests);

        await Assert.That(interrupted.Count).IsGreaterThan(0);
        await Assert.That(interrupted.Min(outcome => outcome.Took)).IsLessThan(TimeSpan.FromSeconds(2.5));
        await Assert.That(JsonElement.Parse(throttled).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("tenant_busy");
        await Assert.That(Percentile(loaded, .99)).IsLessThan(Math.Max(Percentile(baseline, .99) * 4, 400));
    }

    private static async Task<List<double>> ReadsAsync(HttpClient client, string database, int count)
    {
        var samples = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            var clock = Stopwatch.StartNew();
            await Ok(SqlAsync(client, database, "SELECT body FROM notes WHERE id = 1"));
            samples.Add(clock.Elapsed.TotalMilliseconds);
        }

        return samples;
    }

    private static async Task<string> Ok(Task<(HttpStatusCode Status, string Body)> request)
    {
        var (status, body) = await request;
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
        return body;
    }
}
