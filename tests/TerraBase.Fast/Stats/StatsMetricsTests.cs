using Bogus;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Testing;

namespace TerraBase.Fast.Stats;

public sealed class StatsMetricsTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_closed_window_exports_databases_operations_and_the_busiest_rank()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        const int writes = 3;
        var names = Enumerable.Range(0, writes).Select(_ => Faker.Random.AlphaNumeric(12)).ToArray();
        foreach (var name in names)
        {
            using var put = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", name)),
                new { text = Faker.Lorem.Sentence(12) });
            put.EnsureSuccessStatusCode();
        }

        using var read = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", names[0])));
        read.EnsureSuccessStatusCode();

        // The first window covers the nominal five seconds; it closes on the tick that follows this advance.
        time.Advance(TimeSpan.FromSeconds(5));
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await api.Server.MetricAsync("terrabase_stats_operations_total", "kind=\"write\"", token) == writes);

        await Assert.That(await api.Server.MetricAsync("terrabase_stats_databases")).IsGreaterThanOrEqualTo(1);
        await Assert.That(await api.Server.MetricAsync("terrabase_stats_operations_total", "kind=\"read\"")).IsEqualTo(1);
        await Assert.That(await api.Server.MetricAsync("terrabase_stats_top_operations_per_second", "rank=\"1\"")).IsGreaterThan(0);
    }
}
