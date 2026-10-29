using System.Net;
using System.Text.Json;
using Bogus;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class SavedQueryApiTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Saved_queries_are_created_listed_updated_and_deleted()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var queries = $"{database.ProjectPath}/saved-queries";
        var name = Faker.Hacker.Phrase();
        if (name.Length > 128)
        {
            name = name[..128];
        }

        using var created = await api.Client.PostAsJsonAsync(Uri(queries), new { name, sql = "SELECT * FROM people WHERE age > 30" });
        var query = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = query.GetProperty("id").GetString()!;
        using var second = await api.Client.PostAsJsonAsync(Uri(queries), new { name = "Second", sql = "SELECT _id FROM people" });
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var read = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{queries}/{id}"));
        var list = await api.Client.GetFromJsonAsync<JsonElement>(Uri(queries));
        var firstPage = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{queries}?limit=1"));
        var secondPage = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{queries}?limit=1&after={firstPage.GetProperty("next").GetString()}"));
        using var updated = await api.Client.PutAsJsonAsync(Uri($"{queries}/{id}"), new { name = "Renamed", sql = "SELECT name FROM people ORDER BY name" });
        var updatedBody = await updated.Content.ReadFromJsonAsync<JsonElement>();
        using var deleted = await api.Client.DeleteAsync(Uri($"{queries}/{id}"));
        using var gone = await api.Client.GetAsync(Uri($"{queries}/{id}"));
        using var updateGone = await api.Client.PutAsJsonAsync(Uri($"{queries}/{id}"), new { name = "Again", sql = "SELECT * FROM people" });
        using var deleteGone = await api.Client.DeleteAsync(Uri($"{queries}/{id}"));
        var remaining = await api.Client.GetFromJsonAsync<JsonElement>(Uri(queries));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"{queries}/{id}");
        await Assert.That(query.GetProperty("name").GetString()).IsEqualTo(name.Trim());
        await Assert.That(query.GetProperty("sql").GetString()).IsEqualTo("SELECT * FROM people WHERE age > 30");
        await Assert.That(query.GetProperty("updatedAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(read.GetProperty("createdAt").GetDateTimeOffset()).IsEqualTo(query.GetProperty("createdAt").GetDateTimeOffset());
        await Assert.That(string.Join(',', Ids(list))).IsEqualTo($"{id},{secondId}");
        await Assert.That(Ids(firstPage)).IsEquivalentTo([id]);
        await Assert.That(Ids(secondPage)).IsEquivalentTo([secondId]);
        await Assert.That(secondPage.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(updatedBody.GetProperty("name").GetString()).IsEqualTo("Renamed");
        await Assert.That(updatedBody.GetProperty("sql").GetString()).IsEqualTo("SELECT name FROM people ORDER BY name");
        await Assert.That(updatedBody.GetProperty("updatedAt").ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(updateGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(deleteGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Ids(remaining)).IsEquivalentTo([secondId]);
    }

    // The UTF-8 size of each stored saved query entry of a project, by id, read from the catalog file.
    private static List<string> Ids(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)];

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
