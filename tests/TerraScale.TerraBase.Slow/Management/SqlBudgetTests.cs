using System.Net;
using System.Text.Json;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Slow.Management;

public sealed class SqlBudgetTests
{
    [Test]
    public async Task Statements_past_the_engine_work_budget_are_too_expensive_even_before_their_first_row()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        // 10 000 documents under a 256-condition filter cost SQLite well over 5 million instructions, though no row matches.
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        foreach (var chunk in Enumerable.Range(0, 10_000).Chunk(32))
        {
            await Task.WhenAll(chunk.Select(async i =>
            {
                using var put = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", $"d{i:D4}")), new { n = i });
                put.EnsureSuccessStatusCode();
            }));
        }

        var nothing = Nothing(256);
        using var filtered = await RunAsync(api, database, $"SELECT _id FROM items WHERE {nothing} LIMIT 1");
        using var sorted = await RunAsync(api, database, $"SELECT _id FROM items WHERE {nothing} ORDER BY n LIMIT 1");
        // d0000 matches first; the budget then stops the scan for more rows.
        using var afterFirstRow = await RunAsync(api, database, $"SELECT _id FROM items WHERE n = 0 OR {Nothing(255)} ORDER BY _id");
        var recommended = (await api.Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri($"{database.Path}/index-recommendations")))
            .GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToList();
        // The interrupted statement's session serves the next one.
        using var resumed = await RunAsync(api, database, "SELECT _id FROM items ORDER BY _id LIMIT 1");

        await Assert.That(filtered.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await filtered.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
        await Assert.That(sorted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await sorted.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
        await Assert.That(afterFirstRow.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await afterFirstRow.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
        // The stopped statements still count their unindexed fields.
        await Assert.That(recommended).Contains("items:f000");
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows")[0].GetProperty("id").GetString()).IsEqualTo(ManagementApi.DocumentId("d0000"));
    }

    // A filter of conditions on distinct unindexed fields that no document matches.
    private static string Nothing(int conditions) => string.Join(" OR ", Enumerable.Range(0, conditions).Select(i => $"f{i:D3} = -1"));

    private static Task<HttpResponseMessage> RunAsync(ManagementApi api, ManagedDatabaseRef database, string sql) =>
        api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/sql"), new { sql });
}
