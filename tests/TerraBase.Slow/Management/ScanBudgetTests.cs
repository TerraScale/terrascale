using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class ScanBudgetTests
{
    [Test]
    public async Task Maintained_counts_and_usage_succeed_while_schema_scans_exhaust_the_query_budget()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await ManagementApi.CreateCollectionAsync(data, database, "probe");

        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 1100000)
            INSERT INTO "items" (id, doc) SELECT printf('00000000-0000-7000-8000-%012x', n), '{"n":1}' FROM rows;
            INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2) SELECT 'items', id, '', 65536, 0, 1, 0, 0 FROM "items";
            INSERT INTO _tb_collection_stats (collection, documents, bytes) SELECT 'items', count(*), sum(octet_length(doc)) FROM "items";
            """);
        using var collections = await api.Client.GetAsync(ManagementApi.Uri($"{database.Path}/collections"));
        await Assert.That(collections.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var listed = await collections.Content.ReadFromJsonAsync<JsonElement>();
        var items = listed.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "items");
        var probe = listed.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "probe");
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await Assert.That(items.GetProperty("documents").GetInt64()).IsEqualTo(1_100_000);
        await Assert.That(items.GetProperty("bytes").GetInt64()).IsEqualTo(7_700_000);
        await Assert.That(probe.GetProperty("documents").GetInt64()).IsEqualTo(0);
        await Assert.That(probe.GetProperty("bytes").GetInt64()).IsEqualTo(0);

        foreach (var path in new[]
        {
            $"{database.Path}/usage",
            $"{database.ProjectPath}/usage",
        })
        {
            using var response = await api.Client.GetAsync(ManagementApi.Uri(path));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(path);
            var usage = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(usage.GetProperty("documents").GetInt64()).IsEqualTo(1_100_000).Because(path);
            await Assert.That(usage.GetProperty("logicalBytes").GetInt64()).IsEqualTo(7_700_000).Because(path);
        }

        var schemaPath = $"{database.Path}/collections/items/schema";
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(ManagementApi.Uri(schemaPath)),
            HttpStatusCode.BadRequest, "invalid_query", "schema too expensive");
        await AssertWriteAfterRejectionAsync(data, database, schemaPath);
    }

    [Test]
    public async Task Project_usage_refuses_more_databases_than_one_request_reads()
    {

        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider());
        var first = await api.CreateManagedDatabaseAsync("db-0");
        await api.SetPlanAsync(api.User, first.Organization, "enterprise");
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

    private static async Task AssertWriteAfterRejectionAsync(HttpClient data, ManagedDatabaseRef database, string rejected)
    {
        var ops = Enumerable.Range(0, 50)
            .Select(_ => new { op = "put", collection = "probe", id = EntityIds.New().ToString(), document = new { n = 1 } })
            .ToArray();
        using var probe = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/transactions"), new { ops });
        await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(rejected);
    }
}
