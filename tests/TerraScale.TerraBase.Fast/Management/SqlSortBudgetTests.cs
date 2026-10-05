using System.Net;
using System.Text.Json;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Management;

public sealed class SqlSortBudgetTests
{
    private static readonly string[] IndexFields = ["n"];
    [Test]
    public async Task Unindexed_sort_bounds_candidate_bytes_and_an_index_avoids_the_sort()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        // Nine bodies of about 0.95 MiB, stored without uploads, pass the sort's 8 MiB candidate budget.
        var payload = new string('x', 1_000_000);
        for (var n = 0; n < 9; n++)
        {
            await api.PutStoredAsync(data, database, "items", $"d{n}", $$"""{"n":{{n}},"payload":"{{payload}}"}""");
        }

        const string Sql = "SELECT n FROM items ORDER BY n DESC LIMIT 1";
        using var unindexed = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/sql"), new { sql = Sql });
        await Assert.That(unindexed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var index = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/indexes"),
            new { collection = "items", fields = IndexFields });
        index.EnsureSuccessStatusCode();
        // The same descending query, now read backwards along the index.
        using var indexed = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/sql"), new { sql = Sql });
        indexed.EnsureSuccessStatusCode();
        var body = await indexed.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("rows")[0].GetProperty("value").GetProperty("n").GetInt32()).IsEqualTo(8);
        await Assert.That(body.GetProperty("truncated").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Unindexed_sort_bounds_candidates_and_a_small_query_still_works_after_rejection()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 10001)
            INSERT INTO documents SELECT 'items', printf('00000000-0000-7000-8000-%012x', n), 1, '{"n":1}' FROM rows;
            """);

        using var sorted = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/sql"),
            new { sql = "SELECT n FROM items ORDER BY n LIMIT 1" });
        await Assert.That(sorted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var ordinary = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/sql"),
            new { sql = "SELECT n FROM items LIMIT 1" });
        ordinary.EnsureSuccessStatusCode();
        var body = await ordinary.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("matched").GetInt32()).IsEqualTo(1);
    }
}
