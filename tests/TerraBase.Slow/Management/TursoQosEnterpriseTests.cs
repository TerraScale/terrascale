using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class TursoQosEnterpriseTests
{
    [Test]
    public async Task Enterprise_removes_plan_scan_limits_but_keeps_the_bounded_sorter()
    {
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
        using var plans = api.Service(api.User);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 100001)
            INSERT INTO "items" (id, doc) SELECT printf('00000000000000000000%06d', n), '{"n":1}' FROM rows;
            INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2) SELECT 'items', id, '', 65536, 0, 1, 0, 0 FROM "items";
            """);
        var planPath = ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/plan");
        using var upgraded = await plans.PutAsJsonAsync(planPath, new { plan = "pro" });
        upgraded.EnsureSuccessStatusCode();
        var queryPath = ManagementApi.Uri($"{database.Data}/collections/items/query");
        var query = new { filter = new { exists = "/missing" }, limit = 1 };
        using var pro = await data.PostAsJsonAsync(queryPath, query);
        await ErrorAssert.ErrorAsync(Task.FromResult(pro), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        using var enterprisePlan = await plans.PutAsJsonAsync(planPath, new { plan = "enterprise" });
        enterprisePlan.EnsureSuccessStatusCode();
        using var enterprise = await data.PostAsJsonAsync(queryPath, query);
        await Assert.That(enterprise.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await enterprise.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength()).IsEqualTo(0);
        using var sorted = await data.PostAsJsonAsync(queryPath, new { sort = new[] { new { path = "/n" } }, limit = 1 });
        await ErrorAssert.ErrorAsync(Task.FromResult(sorted), HttpStatusCode.BadRequest, "sort_requires_index", "the query exceeds the sort budget");
    }

    [Test]
    public async Task Enterprise_can_execute_beyond_the_pro_turso_instruction_limit()
    {
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
        using var plans = api.Service(api.User);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 10000)
            INSERT INTO "items" (id, doc) SELECT printf('00000000000000000000%06d', n), '{"n":1}' FROM rows;
            INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2) SELECT 'items', id, '', 65536, 0, 1, 0, 0 FROM "items";
            """);
        var planPath = ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/plan");
        using var upgraded = await plans.PutAsJsonAsync(planPath, new { plan = "pro" });
        upgraded.EnsureSuccessStatusCode();
        var nothing = string.Join(" OR ", Enumerable.Range(0, 256).Select(i => $"f{i:D3} = -1"));
        var sqlPath = ManagementApi.Uri($"{database.Path}/sql");
        var sql = new { sql = $"SELECT _id FROM items WHERE {nothing} LIMIT 1" };
        using var pro = await api.Client.PostAsJsonAsync(sqlPath, sql);
        await ErrorAssert.ErrorAsync(Task.FromResult(pro), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        using var enterprisePlan = await plans.PutAsJsonAsync(planPath, new { plan = "enterprise" });
        enterprisePlan.EnsureSuccessStatusCode();
        using var enterprise = await api.Client.PostAsJsonAsync(sqlPath, sql);
        await Assert.That(enterprise.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await enterprise.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").GetArrayLength()).IsEqualTo(0);
    }
}
