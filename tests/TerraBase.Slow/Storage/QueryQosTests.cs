using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class QueryQosTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Filtered_scans_count_rejected_rows_and_follow_plan_changes_on_an_open_store(bool sql)
    {
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
        using var plans = api.Service(api.User);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 10001)
            INSERT INTO "items" (id, doc) SELECT printf('00000000000000000000%06d', n), '{"n":1}' FROM rows;
            INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2) SELECT 'items', id, '', 65536, 0, 1, 0, 0 FROM "items";
            """);
        using var hobby = await QueryAsync();
        await ErrorAssert.ErrorAsync(Task.FromResult(hobby), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");

        using var upgraded = await plans.PutAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/plan"), new { plan = "pro" });
        upgraded.EnsureSuccessStatusCode();
        using var pro = await QueryAsync();
        await Assert.That(pro.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var response = await pro.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(response.GetProperty(sql ? "rows" : "items").GetArrayLength()).IsEqualTo(0);

        using var downgraded = await plans.PutAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/plan"), new { plan = "hobby" });
        downgraded.EnsureSuccessStatusCode();
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database,
            "UPDATE \"items\" SET doc = 'invalid-json' WHERE id = printf('00000000000000000000%06d', 10001);");
        using var again = await QueryAsync("absent");
        await ErrorAssert.ErrorAsync(Task.FromResult(again), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        using var resumed = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/collections/items/query"), new { limit = 1 });
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.OK);

        Task<HttpResponseMessage> QueryAsync(string field = "missing")
        {
            return sql
                ? api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/sql"), new { sql = $"SELECT _id FROM items WHERE {field} = 0 LIMIT 1" })
                : data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/collections/items/query"), new { filter = new { exists = "/" + field }, limit = 1 });
        }
    }

    [Test]
    public async Task Query_deadlines_follow_the_authenticated_plan()
    {
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new QueryClock()));
        using var plans = api.Service(api.User);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        var queryPath = ManagementApi.Uri($"{database.Data}/collections/items/query");
        var listPath = ManagementApi.Uri($"{database.Data}/collections/items/documents?limit=1");
        using var hobby = await data.PostAsJsonAsync(queryPath, new { limit = 1 });
        await ErrorAssert.ErrorAsync(Task.FromResult(hobby), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        using var hobbyList = await data.GetAsync(listPath);
        await ErrorAssert.ErrorAsync(Task.FromResult(hobbyList), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        using var upgraded = await plans.PutAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/plan"), new { plan = "pro" });
        upgraded.EnsureSuccessStatusCode();
        using var pro = await data.PostAsJsonAsync(queryPath, new { limit = 1 });
        await Assert.That(pro.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var proList = await data.GetAsync(listPath);
        await Assert.That(proList.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await proList.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(page.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(page.GetProperty("next_cursor").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task A_cursor_remains_usable_after_the_plan_deadline_interrupts_its_continuation()
    {
        var clock = new QueryClock(step: 0);
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            INSERT INTO "items" (id, doc) VALUES ('0000000000E00800000000000A', $body), ('0000000000E00800000000000B', $body);
            INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2) SELECT 'items', id, '', 65536, 0, 1, 0, 0 FROM "items";
            """, ("$body", JsonSerializer.Serialize(new { s = new string('\0', 64 * 1024) })));
        var path = ManagementApi.Uri($"{database.Data}/collections/items/query");
        var sort = Enumerable.Range(0, 4).Select(_ => new { path = "/s" }).ToArray();
        using var first = await data.PostAsJsonAsync(path, new { sort, limit = 1 });
        first.EnsureSuccessStatusCode();
        var cursor = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("next_cursor").GetString();
        await Assert.That(cursor).IsNotNull();

        clock.AdvanceReads();
        using var expired = await data.PostAsJsonAsync(path, new { sort, limit = 1, cursor });
        await ErrorAssert.ErrorAsync(Task.FromResult(expired), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");

        await api.SetPlanAsync(api.User, database.Organization, "pro");
        using var resumed = await data.PostAsJsonAsync(path, new { sort, limit = 1, cursor });
        resumed.EnsureSuccessStatusCode();
        var result = await resumed.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(result.GetProperty("items")[0].GetProperty("id").GetString()).IsEqualTo("0000000000E00800000000000B");
        await Assert.That(result.GetProperty("next_cursor").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task A_filtered_scan_accounts_json_bytes_even_when_no_document_matches()
    {
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
        using var plans = api.Service(api.User);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 17)
            INSERT INTO "items" (id, doc) SELECT printf('00000000000000000000%06d', n), $body FROM rows;
            INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2) SELECT 'items', id, '', 65536, 0, 1, 0, 0 FROM "items";
            """, ("$body", "{\"pad\":\"" + new string('a', 1024 * 1024 - 10) + "\"}"));
        var query = new { filter = new { exists = "/missing" }, limit = 1 };
        using var hobby = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/collections/items/query"), query);
        await ErrorAssert.ErrorAsync(Task.FromResult(hobby), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        using var upgraded = await plans.PutAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/plan"), new { plan = "pro" });
        upgraded.EnsureSuccessStatusCode();
        using var pro = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/collections/items/query"), query);
        await Assert.That(pro.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await pro.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Filtered_count_limits_include_nonmatching_candidates()
    {
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 10001)
            INSERT INTO "items" (id, doc) SELECT printf('00000000000000000000%06d', n), '{"n":1}' FROM rows;
            INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2) SELECT 'items', id, '', 65536, 0, 1, 0, 0 FROM "items";
            """);
        using var counted = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/collections/items/query"), new { limit = 1, count = true });
        await ErrorAssert.ErrorAsync(Task.FromResult(counted), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        using var resumed = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/collections/items/query"), new { limit = 1 });
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private sealed class QueryClock(long step = 1500) : TimeProvider
    {
        private long _timestamp;
        private long _step = step;

        public override long TimestampFrequency => 1000;

        public void AdvanceReads() => Volatile.Write(ref _step, 1500);

        public override long GetTimestamp() => Thread.CurrentThread.Name == "terrabase-turso"
            ? Interlocked.Add(ref _timestamp, Volatile.Read(ref _step)) : Volatile.Read(ref _timestamp);
    }
}
