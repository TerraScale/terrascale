using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Management;

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

    [Test]
    public async Task Saved_query_pagination_survives_restart_with_the_clock_behind()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-query-restart-").FullName;
        try
        {
            var frozenAt = new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero);
            var created = new List<string>();
            string user, path, cursor;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider(frozenAt)))
            {
                user = api.User;
                path = (await api.CreateManagedDatabaseAsync()).ProjectPath + "/saved-queries";
                for (var i = 0; i < 3; i++)
                {
                    created.Add(await CreateAsync(api.Client, path, $"query-{i}"));
                }

                cursor = (await api.Client.GetFromJsonAsync<JsonElement>(Uri(path + "?limit=1"))).GetProperty("next").GetString()!;
            }

            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider(frozenAt.AddDays(-1))))
            {
                using var owner = api.As(user);
                created.Add(await CreateAsync(owner, path, "after-restart"));
                var page = await owner.GetFromJsonAsync<JsonElement>(Uri($"{path}?after={cursor}"));

                await Assert.That(string.Join(',', Ids(page))).IsEqualTo(string.Join(',', created.Skip(1)));
                await Assert.That(page.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_member_removed_after_the_project_lookup_cannot_delete_a_saved_query()
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        var org = $"/v1/management/organizations/{database.Organization}";
        var memberId = await api.AddMemberAsync(api.Client, database.Organization, "member");
        using var member = api.As(memberId);
        var path = $"{database.ProjectPath}/saved-queries/{await CreateAsync(member, database.ProjectPath + "/saved-queries", "Mine")}";

        // The member's delete has resolved the project but not started its catalog write when the owner removes them.
        using var gate = hook.PauseWrite();
        var delete = member.DeleteAsync(Uri(path));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await api.Client.DeleteAsync(Uri($"{org}/members/{memberId}"))).EnsureSuccessStatusCode();
        gate.Dispose();
        using var deleted = await delete.WaitAsync(TimeSpan.FromSeconds(10));
        using var kept = await api.Client.GetAsync(Uri(path));

        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(kept.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Saved_query_pages_stop_at_one_mebibyte_and_continue_from_the_cursor()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var path = database.ProjectPath + "/saved-queries";
        // Seventy statements of about 16 KB pass 1 MiB together.
        var literal = new string('x', 16_000);
        var created = new List<string>();
        for (var i = 0; i < 70; i++)
        {
            using var response = await api.Client.PostAsJsonAsync(Uri(path), new { name = $"query-{i}", sql = $"SELECT * FROM people WHERE note = '{literal}'" });
            response.EnsureSuccessStatusCode();
            created.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        var first = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{path}?limit=100"));
        var second = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{path}?limit=100&after={first.GetProperty("next").GetString()}"));

        // The budget counts the stored entries: the first page holds every one that fits in 1 MiB, and the next would not.
        var sizes = await StoredSizesAsync(api.Server.DataDirectory, database.Project);
        long total = 0;
        var fit = 0;
        foreach (var id in created)
        {
            if (fit > 0 && total + sizes[id] > 1024 * 1024)
            {
                break;
            }

            total += sizes[id];
            fit++;
        }

        await Assert.That(fit).IsLessThan(70);
        await Assert.That(Ids(first).Count).IsEqualTo(fit);
        await Assert.That(string.Join(',', Ids(first).Concat(Ids(second)))).IsEqualTo(string.Join(',', created));
        await Assert.That(second.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    // The UTF-8 size of each stored saved query entry of a project, by id, read from the catalog file.
    private static async Task<Dictionary<string, long>> StoredSizesAsync(string dataDirectory, string project)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "_system.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, length(CAST(body AS BLOB)) FROM documents WHERE collection = $collection AND body IS NOT NULL;";
        command.Parameters.AddWithValue("$collection", $"projects/{project}/saved_queries");
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sizes[reader.GetString(0)] = reader.GetInt64(1);
        }

        return sizes;
    }

    [Test]
    public async Task Invalid_saved_queries_are_rejected()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var queries = $"{database.ProjectPath}/saved-queries";
        using var created = await api.Client.PostAsJsonAsync(Uri(queries), new { name = "Valid", sql = "SELECT * FROM people" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        object[] invalid =
        [
            new { name = "Bad SQL", sql = "DROP TABLE people" },
            new { name = "No SQL" },
            new { name = "Join", sql = "SELECT * FROM a JOIN b ON a.x = b.y" },
            new { name = "", sql = "SELECT * FROM people" },
            new { name = new string('n', 129), sql = "SELECT * FROM people" },
            new { sql = "SELECT * FROM people" },
        ];

        foreach (var body in invalid)
        {
            using var create = await api.Client.PostAsJsonAsync(Uri(queries), body);
            using var update = await api.Client.PutAsJsonAsync(Uri($"{queries}/{id}"), body);
            await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(JsonSerializer.Serialize(body));
            await Assert.That(update.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(JsonSerializer.Serialize(body));
        }

        var list = await api.Client.GetFromJsonAsync<JsonElement>(Uri(queries));
        await Assert.That(Ids(list)).IsEquivalentTo([id]);
    }

    [Test]
    public async Task Saved_query_routes_return_404_for_unknown_parents()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var created = await api.Client.PostAsJsonAsync(Uri($"{database.ProjectPath}/saved-queries"), new { name = "Kept", sql = "SELECT * FROM people" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var body = new { name = "Query", sql = "SELECT * FROM people" };
        string[] projects =
        [
            $"/v1/management/organizations/{Guid.NewGuid()}/projects/{database.Project}",
            $"/v1/management/organizations/{database.Organization}/projects/{Guid.NewGuid()}",
        ];

        foreach (var project in projects)
        {
            using var list = await api.Client.GetAsync(Uri($"{project}/saved-queries"));
            using var create = await api.Client.PostAsJsonAsync(Uri($"{project}/saved-queries"), body);
            using var read = await api.Client.GetAsync(Uri($"{project}/saved-queries/{id}"));
            using var update = await api.Client.PutAsJsonAsync(Uri($"{project}/saved-queries/{id}"), body);
            using var delete = await api.Client.DeleteAsync(Uri($"{project}/saved-queries/{id}"));
            HttpStatusCode[] statuses = [list.StatusCode, create.StatusCode, read.StatusCode, update.StatusCode, delete.StatusCode];
            await Assert.That(statuses.All(status => status == HttpStatusCode.NotFound)).IsTrue().Because(project);
        }

        using var unknownQuery = await api.Client.GetAsync(Uri($"{database.ProjectPath}/saved-queries/{Guid.NewGuid()}"));
        using var kept = await api.Client.GetAsync(Uri($"{database.ProjectPath}/saved-queries/{id}"));
        await Assert.That(unknownQuery.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(kept.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task<string> CreateAsync(HttpClient client, string path, string name)
    {
        using var created = await client.PostAsJsonAsync(Uri(path), new { name, sql = "SELECT * FROM people" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static List<string> Ids(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)];

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
