using System.Net;
using System.Text.Json;
using Bogus;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Management.Queries;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class IndexApiTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] Email = ["email"];
    private static readonly string[] Sku = ["sku"];
    private static readonly string[] CustomerEmail = ["customer.email"];
    private static readonly string[] StatusTotal = ["status", "total"];

    [Test]
    public async Task Indexes_are_created_listed_and_dropped()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var indexes = $"{database.Path}/indexes";
        var name = "by_" + Faker.Random.String2(8, "abcdefghijklmnopqrstuvwxyz");

        using var byDefault = await api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "orders", fields = CustomerEmail });
        var created = await byDefault.Content.ReadFromJsonAsync<JsonElement>();
        using var named = await api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "orders", fields = StatusTotal, name });
        using var sameName = await api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "items", fields = Sku, name });
        using var sameFields = await api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "orders", fields = StatusTotal, name = "other" });
        using var otherCollection = await api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "items", fields = StatusTotal });
        var all = await api.Client.GetFromJsonAsync<JsonElement>(Uri(indexes));
        var items = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{indexes}?collection=items"));
        using var badFilter = await api.Client.GetAsync(Uri($"{indexes}?collection=Bad!"));
        using var dropped = await api.Client.DeleteAsync(Uri($"{indexes}/{name}"));
        using var droppedAgain = await api.Client.DeleteAsync(Uri($"{indexes}/{name}"));
        using var unknown = await api.Client.DeleteAsync(Uri($"{indexes}/Not-An-Index"));
        var remaining = await api.Client.GetFromJsonAsync<JsonElement>(Uri(indexes));

        await Assert.That(byDefault.StatusCode).IsEqualTo(HttpStatusCode.Created);

        var defaultName = created.GetProperty("name").GetString()!;
        await Assert.That(defaultName).Matches("^orders__customer_demail_[0-9a-f]{8}$");
        await Assert.That(byDefault.Headers.Location!.ToString()).IsEqualTo($"{indexes}/{defaultName}");
        await Assert.That(created.GetProperty("collection").GetString()).IsEqualTo("orders");
        await Assert.That(created.GetProperty("fields")[0].GetString()).IsEqualTo("customer.email");
        await Assert.That(created.GetProperty("state").GetString()).IsEqualTo("active");
        await Assert.That(named.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(sameName.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(sameFields.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(otherCollection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(Names(all)).IsEquivalentTo(new[] { name, "items_status_total", defaultName }.Order().ToList());
        await Assert.That(string.Join(',', Names(all))).IsEqualTo(string.Join(',', Names(all).Order(StringComparer.Ordinal)));
        await Assert.That(Names(items)).IsEquivalentTo(["items_status_total"]);
        await Assert.That(badFilter.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(dropped.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(droppedAgain.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Names(remaining)).IsEquivalentTo(["items_status_total", defaultName]);
    }

    [Test]
    public async Task Creating_an_index_does_not_analyze_indexes_of_other_collections()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "p1")), new { email = Faker.Internet.Email() })).EnsureSuccessStatusCode();
        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "people", fields = Email })).EnsureSuccessStatusCode();
        var before = await ReadIndexStatsAsync(api, database, "tb_index_people_email");

        for (var i = 2; i <= 8; i++)
        {
            (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", $"p{i}")), new { email = Faker.Internet.Email() })).EnsureSuccessStatusCode();
        }

        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "orders", fields = Sku })).EnsureSuccessStatusCode();
        var after = await ReadIndexStatsAsync(api, database, "tb_index_people_email");

        await Assert.That(before).IsNotNull();
        await Assert.That(after).IsEqualTo(before);
    }

    private static async Task<string?> ReadIndexStatsAsync(ManagementApi api, ManagedDatabaseRef database, string index)
    {
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(api.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stat FROM sqlite_stat1 WHERE idx = $index;";
        command.Parameters.AddWithValue("$index", index);
        return await command.ExecuteScalarAsync() as string;
    }

    [Test]
    public async Task An_index_on_a_larger_collection_is_used_and_leaves_other_statistics_alone()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "p1")), new { email = Faker.Internet.Email() })).EnsureSuccessStatusCode();
        await ManagementApi.CreateCollectionAsync(data, database, "orders");

        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(0) UNION ALL SELECT n + 1 FROM rows WHERE n < 999)
            INSERT INTO "orders" (id, doc) SELECT printf('00000000-0000-7000-8000-%012x', n),
                json_object('sku', 'sku-' || n) FROM rows;
            INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2) SELECT 'orders', id, '', 65536, 0, 1, 0, 0 FROM "orders";
            """);

        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "people", fields = Email })).EnsureSuccessStatusCode();
        var peopleStats = await ReadIndexStatsAsync(api, database, "tb_index_people_email");
        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "orders", fields = Sku })).EnsureSuccessStatusCode();
        using var check = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql/check"), new { sql = "SELECT * FROM orders WHERE sku = 'sku-750'" });
        check.EnsureSuccessStatusCode();
        var plan = await check.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(await ReadIndexStatsAsync(api, database, "tb_index_orders_sku")).StartsWith("1000 ");
        await Assert.That(await ReadIndexStatsAsync(api, database, "tb_index_people_email")).IsEqualTo(peopleStats);
        await Assert.That(plan.GetProperty("indexes").EnumerateArray().Select(index => index.GetString()!)).Contains("orders_sku");
    }

    private static List<string> Names(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
