using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class ScanBudgetTests
{
    [Test]
    public async Task Counts_statistics_schema_and_usage_share_the_query_work_budget()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await ManagementApi.CreateCollectionAsync(data, database, "probe");
        // More than five million VM instructions even for the narrowest count scan. The setup writes only the
        // isolated database; all budget failures and the probes are exercised through real listeners.
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 1100000)
            INSERT INTO documents SELECT 'items', printf('00000000-0000-7000-8000-%012x', n), 1, '{"n":1}' FROM rows;
            """);
        foreach (var path in new[]
        {
            $"{database.Path}/collections",
            $"{database.Path}/collections/items/schema",
            $"{database.Path}/usage",
            $"{database.ProjectPath}/usage",
        })
        {
            using var response = await api.Client.GetAsync(ManagementApi.Uri(path));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(path);
            await AssertNoProgressHandlerLeftAsync(data, database, path);
        }
    }

    [Test]
    public async Task Project_usage_refuses_more_databases_than_one_request_reads()
    {
        // Test the database-count boundary independently of the request's five-second execution budget.
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider());
        var first = await api.CreateManagedDatabaseAsync("db-0");
        for (var i = 1; i < 100; i++)
        {
            await api.CreateManagedDatabaseAsync(first.Organization, first.Project, $"db-{i}");
        }

        using var hundred = await api.Client.GetAsync(ManagementApi.Uri($"{first.ProjectPath}/usage"));
        var usage = await hundred.Content.ReadFromJsonAsync<JsonElement>();
        await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "db-100");
        using var more = await api.Client.GetAsync(ManagementApi.Uri($"{first.ProjectPath}/usage"));

        await Assert.That(hundred.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(usage.GetProperty("databases").GetInt64()).IsEqualTo(100);
        await ErrorAssert.ErrorAsync(more, HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
    }

    // Requests run one at a time here, so the next one on this database takes the pooled session the rejected one
    // returned. A write installs no progress handler of its own and runs far more than the 1000 SQLite instructions
    // between handler calls, so a handler left behind, whose budget is spent, would interrupt it.
    private static async Task AssertNoProgressHandlerLeftAsync(HttpClient data, ManagedDatabaseRef database, string rejected)
    {
        var ops = Enumerable.Range(0, 50)
            .Select(_ => new { op = "put", collection = "probe", id = EntityIds.New().ToString(), document = new { n = 1 } })
            .ToArray();
        using var probe = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/transactions"), new { ops });
        await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(rejected);
    }
}
