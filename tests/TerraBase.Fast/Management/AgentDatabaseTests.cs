using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class AgentDatabaseTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task An_agent_database_is_created_forked_equal_to_its_source_and_deleted_at_the_end_of_its_ttl()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: clock);
        var source = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, source.Project);
        var names = Enumerable.Range(0, 5).Select(_ => Faker.Name.FullName()).ToList();
        await SqlAsync(client, source.Data, new { sql = "CREATE TABLE agents (id INTEGER PRIMARY KEY, name TEXT NOT NULL)" });
        await SqlAsync(client, source.Data, [.. names.Select(name => (object)new { sql = "INSERT INTO agents (name) VALUES (?)", args = new object[] { name } })]);

        var agent = await CreatedAsync(await client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{source.Project}/databases"), new { name = "agent", ttlSeconds = 60 }));
        var fork = await CreatedAsync(await client.PostAsJsonAsync(ManagementApi.Uri(source.Data + "/branches"), new { name = "fork", ttlSeconds = 3600 }));
        var forkRows = await RowsAsync(client, $"/v1/projects/{source.Project}/databases/fork", "SELECT name FROM agents ORDER BY id");

        await Assert.That(agent.GetProperty("expiresAt").GetDateTimeOffset()).IsEqualTo(clock.GetUtcNow().AddSeconds(60));
        await Assert.That(fork.GetProperty("branchedFrom").GetString()).IsEqualTo(source.Id);
        await Assert.That(forkRows).IsEquivalentTo(names);

        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            using var read = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{source.Project}/databases/agent"), token);
            return read.StatusCode == HttpStatusCode.NotFound;
        }, TimeSpan.FromSeconds(60));
        using var kept = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{source.Project}/databases/fork"));
        await Assert.That(kept.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var cleared = await (await client.DeleteAsync(ManagementApi.Uri($"/v1/projects/{source.Project}/databases/fork/ttl"))).Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(cleared.GetProperty("expiresAt").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task A_token_narrowed_to_one_database_is_refused_on_another_and_cannot_set_a_ttl()
    {
        await using var api = await ManagementApi.StartAsync();
        var source = await api.CreateManagedDatabaseAsync();
        using var owner = ProjectClient(api, source.Project);
        var other = await CreatedAsync(await owner.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{source.Project}/databases"), new { name = "other", ttlSeconds = 600 }));
        using var agent = ProjectClient(api, source.Project, database: source.Id, scope: "documents:read documents:write");

        using var own = await agent.PostAsJsonAsync(ManagementApi.Uri(source.Data + "/sql/execute"), new { statements = new[] { new { sql = "SELECT 1" } } });
        using var elsewhere = await agent.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{source.Project}/databases/{other.GetProperty("id").GetString()}/sql/execute"),
            new { statements = new[] { new { sql = "SELECT 1" } } });
        using var ttl = await agent.PutAsJsonAsync(ManagementApi.Uri(source.Data + "/ttl"), new { ttlSeconds = 60 });

        await Assert.That(own.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(elsewhere.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(ttl.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    private static HttpClient ProjectClient(ManagementApi api, string project, string? database = null,
        string scope = "databases:read databases:write documents:read documents:write") =>
        api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = scope;
            if (database is not null)
            {
                token.Claims["database"] = database;
            }
        }));

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    private static async Task SqlAsync(HttpClient client, string database, params object[] statements)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(database + "/sql/execute"), new { statements });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<string>> RowsAsync(HttpClient client, string database, string sql)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(database + "/sql/execute"), new { statements = new[] { new { sql } } });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. body.GetProperty("results")[0].GetProperty("rows").EnumerateArray().Select(row => row[0].GetString()!)];
    }
}
