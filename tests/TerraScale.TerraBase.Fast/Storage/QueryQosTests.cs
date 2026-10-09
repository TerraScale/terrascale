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
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()), managementCredential: true);
        using var plans = api.Service(api.User);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        // Seed the isolated database directly; every observed behavior runs over the real HTTPS listener.
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 10001)
            INSERT INTO documents SELECT 'items', printf('00000000000000000000%06d', n), 1, '{"n":1}' FROM rows;
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
        // The first over-budget candidate has invalid legacy JSON. The guard must reject it before
        // either SQLite JSON predicates or managed materialization reads its body.
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database,
            "UPDATE documents SET body = 'invalid-json' WHERE collection = 'items' AND id = printf('00000000000000000000%06d', 10001);");
        // Change the predicate to avoid a previously cached Pro result.
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
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new QueryClock()), managementCredential: true);
        using var plans = api.Service(api.User);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        var queryPath = ManagementApi.Uri($"{database.Data}/collections/items/query");
        using var hobby = await data.PostAsJsonAsync(queryPath, new { limit = 1 });
        await ErrorAssert.ErrorAsync(Task.FromResult(hobby), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        using var upgraded = await plans.PutAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/plan"), new { plan = "pro" });
        upgraded.EnsureSuccessStatusCode();
        using var pro = await data.PostAsJsonAsync(queryPath, new { limit = 1 });
        await Assert.That(pro.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_filtered_scan_accounts_json_bytes_even_when_no_document_matches()
    {
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()), managementCredential: true);
        using var plans = api.Service(api.User);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 17)
            INSERT INTO documents SELECT 'items', printf('00000000000000000000%06d', n), 1, $body FROM rows;
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
            INSERT INTO documents SELECT 'items', printf('00000000000000000000%06d', n), 1, '{"n":1}' FROM rows;
            """);
        using var counted = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/collections/items/query"), new { limit = 1, count = true });
        await ErrorAssert.ErrorAsync(Task.FromResult(counted), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        using var resumed = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/collections/items/query"), new { limit = 1 });
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // Read-pool checks advance 1.5 seconds: beyond Hobby's deadline, within Pro's.
    // Catalog audit writes use their write lane and must not expire while setting up a plan change.
    private sealed class QueryClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Thread.CurrentThread.Name == "terrabase-sqlite"
            ? Interlocked.Add(ref _timestamp, 1500) : Volatile.Read(ref _timestamp);
    }

}
