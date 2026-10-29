using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class SqlApiTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] Email = ["email"];

    [Test]
    public async Task Select_returns_whole_documents_and_projections()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var people = await SeedPeopleAsync(api, database);

        var all = await RunAsync(api, database, "select * from people;");
        var projected = await RunAsync(api, database, $"SELECT _id, _rev, name, address.city, \"address\".\"zip\", missing FROM people WHERE _id = '{Id("p1")}'");
        var value = projected.GetProperty("rows")[0].GetProperty("value");
        // _rev is the document's version exactly as the data API reports it.
        using var data = await api.CreateClientAsync(database.Organization, "documents:read");
        var version = (await data.GetFromJsonAsync<JsonElement>(Uri(ManagementApi.DocumentPath(database, "people", "p1")))).GetProperty("version").GetString();

        await Assert.That(Ids(all)).IsEquivalentTo(people.Keys.Order().ToList());
        await Assert.That(all.GetProperty("matched").GetInt32()).IsEqualTo(people.Count);
        await Assert.That(all.GetProperty("truncated").GetBoolean()).IsFalse();
        await Assert.That(all.GetProperty("rows")[0].GetProperty("value").GetProperty("email").GetString()).IsEqualTo(people["p1"].Email);
        await Assert.That(value.EnumerateObject().Select(member => member.Name)).IsEquivalentTo(["_id", "_rev", "name", "address.city", "address.zip", "missing"]);
        await Assert.That(value.GetProperty("_id").GetString()).IsEqualTo(Id("p1"));
        await Assert.That(value.GetProperty("_rev").GetString()).IsEqualTo(version);
        await Assert.That(value.GetProperty("name").GetString()).IsEqualTo(people["p1"].Name);
        await Assert.That(value.GetProperty("address.city").GetString()).IsEqualTo(people["p1"].City);
        await Assert.That(value.GetProperty("address.zip").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(value.GetProperty("missing").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Where_supports_every_operator_and_order_by_with_limit()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var people = await SeedPeopleAsync(api, database);
        (string Sql, string[] Ids)[] cases =
        [
            ("SELECT * FROM people WHERE age = 30", ["p2"]),
            ("SELECT * FROM people WHERE age != 30", ["p1", "p3", "p4", "p5"]),
            ("SELECT * FROM people WHERE age <> 30", ["p1", "p3", "p4", "p5"]),
            ("SELECT * FROM people WHERE age < 30", ["p1"]),
            ("SELECT * FROM people WHERE age <= 30", ["p1", "p2"]),
            ("SELECT * FROM people WHERE age > 40", ["p4", "p5"]),
            ("SELECT * FROM people WHERE age >= 40", ["p3", "p4", "p5"]),
            ("SELECT * FROM people WHERE score >= 2.5 AND score < 4.5", ["p3", "p4"]),
            ("SELECT * FROM people WHERE age > -1 AND active = TRUE", ["p1", "p3", "p5"]),
            ("SELECT * FROM people WHERE active = false", ["p2", "p4"]),
            ("SELECT * FROM people WHERE age IN (20, 50, 99)", ["p1", "p4"]),
            ("SELECT * FROM people WHERE age NOT IN (20, 50)", ["p2", "p3", "p5"]),
            ("SELECT * FROM people WHERE nickname IS NULL", ["p2", "p3", "p4", "p5"]),
            ("SELECT * FROM people WHERE nickname IS NOT NULL", ["p1"]),
            ("SELECT * FROM people WHERE age = 20 OR age = 60", ["p1", "p5"]),
            ("SELECT * FROM people WHERE NOT (age = 20 OR age = 60) AND (active = TRUE OR age = 30)", ["p2", "p3"]),
            ($"SELECT * FROM people WHERE email = '{people["p4"].Email.Replace("'", "''", StringComparison.Ordinal)}'", ["p4"]),
            ($"SELECT * FROM people WHERE address.city = '{people["p3"].City.Replace("'", "''", StringComparison.Ordinal)}'", ["p3"]),
            ($"SELECT * FROM people WHERE _id IN ('{Id("p2")}', '{Id("p5")}', 'nobody')", ["p2", "p5"]),
            ($"SELECT * FROM people WHERE _id > '{Id("p3")}'", ["p4", "p5"]),
            ("SELECT * FROM people -- comment\nWHERE /* block */ age = 40", ["p3"]),
            ("SELECT * FROM nobody", []),
        ];

        foreach (var (sql, ids) in cases)
        {
            await Assert.That(Ids(await RunAsync(api, database, sql))).IsEquivalentTo(ids).Because(sql);
        }

        var sorted = await RunAsync(api, database, "SELECT name FROM people ORDER BY age DESC LIMIT 2");
        var byTwoKeys = await RunAsync(api, database, "SELECT _id FROM people ORDER BY active ASC, age DESC");
        await Assert.That(Ids(sorted)).IsEquivalentTo(["p5", "p4"]);
        await Assert.That(Ids(sorted)[0]).IsEqualTo("p5");
        await Assert.That(sorted.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(sorted.GetProperty("matched").GetInt32()).IsEqualTo(2);
        await Assert.That(string.Join(',', Ids(byTwoKeys))).IsEqualTo("p4,p2,p5,p3,p1");
    }

    [Test]
    public async Task Sql_keys_take_any_value_while_document_ids_stay_ulids()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "things");
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "things", "text")), new { key = "abc" })).EnsureSuccessStatusCode();
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "things", "number")), new { key = 42 })).EnsureSuccessStatusCode();

        await Assert.That(Ids(await RunAsync(api, database, "SELECT * FROM things WHERE key = 'abc'"))).IsEquivalentTo(["text"]);
        await Assert.That(Ids(await RunAsync(api, database, "SELECT * FROM things WHERE key = 42"))).IsEquivalentTo(["number"]);
        await Assert.That(Ids(await RunAsync(api, database, "SELECT * FROM things WHERE _id = 'abc'"))).IsEmpty();

        using var rejected = await data.PutAsJsonAsync(Uri($"{database.Data}/collections/things/documents/by-key?id=abc"), new { key = "abc" });
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_document_id");
    }

    [Test]
    public async Task Deleted_documents_are_not_selected_counted_or_indexed()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var people = await SeedPeopleAsync(api, database);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var before = (await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/collections"))).GetProperty("items")[0];
        // A delete leaves a tombstone row (NULL body) in the documents table.
        (await data.DeleteAsync(Uri(ManagementApi.DocumentPath(database, "people", "p2")))).EnsureSuccessStatusCode();
        var byEmail = $"SELECT _id FROM people WHERE email = '{people["p2"].Email.Replace("'", "''", StringComparison.Ordinal)}'";

        var all = await RunAsync(api, database, "SELECT _id FROM people");
        var unindexed = await RunAsync(api, database, byEmail);
        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "people", fields = Email })).EnsureSuccessStatusCode();
        var indexed = await RunAsync(api, database, byEmail);
        var plan = await PostAsync(api, $"{database.Path}/sql/check", new { sql = byEmail });
        var sortedByIndex = await RunAsync(api, database, "SELECT _id FROM people ORDER BY email");
        var after = (await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/collections"))).GetProperty("items")[0];
        var schema = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/collections/people/schema"));

        await Assert.That(Ids(all)).IsEquivalentTo(["p1", "p3", "p4", "p5"]);
        await Assert.That(Ids(unindexed)).IsEmpty();
        await Assert.That(Ids(indexed)).IsEmpty();
        await Assert.That(plan.GetProperty("indexes").EnumerateArray().Select(index => index.GetString()!)).IsEquivalentTo(["people_email"]);
        await Assert.That(Ids(sortedByIndex).Order(StringComparer.Ordinal)).IsEquivalentTo(["p1", "p3", "p4", "p5"]);
        await Assert.That(before.GetProperty("documents").GetInt64()).IsEqualTo(5);
        await Assert.That(after.GetProperty("documents").GetInt64()).IsEqualTo(4);
        await Assert.That(after.GetProperty("bytes").GetInt64()).IsLessThan(before.GetProperty("bytes").GetInt64());
        await Assert.That(schema.GetProperty("documents").GetInt64()).IsEqualTo(4);
        await Assert.That(schema.GetProperty("sampledDocuments").GetInt32()).IsEqualTo(4);
    }

    [Test]
    public async Task Check_suggests_unindexed_fields_until_an_index_is_used()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var people = await SeedPeopleAsync(api, database);
        var sql = $"SELECT * FROM people WHERE email = '{people["p2"].Email.Replace("'", "''", StringComparison.Ordinal)}' AND _id != 'x' ORDER BY age";

        var before = await PostAsync(api, $"{database.Path}/sql/check", new { sql });
        using var created = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "people", fields = Email });
        var after = await PostAsync(api, $"{database.Path}/sql/check", new { sql });
        var limited = await PostAsync(api, $"{database.Path}/sql/check", new { sql = "SELECT * FROM people LIMIT 5" });
        var sortedByIndex = await PostAsync(api, $"{database.Path}/sql/check", new { sql = "SELECT * FROM people ORDER BY email LIMIT 3" });

        await Assert.That(before.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(before.GetProperty("error").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(before.GetProperty("collection").GetString()).IsEqualTo("people");
        await Assert.That(before.GetProperty("hasLimit").GetBoolean()).IsFalse();
        await Assert.That(before.GetProperty("plan").GetArrayLength()).IsGreaterThan(0);
        await Assert.That(before.GetProperty("indexes").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Suggestions(before)).IsEquivalentTo(["email: filtered without an index", "age: sorted without an index"]);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(after.GetProperty("indexes").EnumerateArray().Select(index => index.GetString()!)).IsEquivalentTo(["people_email"]);
        await Assert.That(Suggestions(after)).IsEquivalentTo(["age: sorted without an index"]);
        await Assert.That(Ids(await RunAsync(api, database, sql))).IsEquivalentTo(["p2"]);
        await Assert.That(limited.GetProperty("hasLimit").GetBoolean()).IsTrue();
        await Assert.That(limited.GetProperty("suggestions").GetArrayLength()).IsEqualTo(0);
        // The collection leads every index key, so a sort on the indexed field reads the index instead of sorting.
        await Assert.That(sortedByIndex.GetProperty("indexes").EnumerateArray().Select(index => index.GetString()!)).IsEquivalentTo(["people_email"]);
    }

    [Test]
    public async Task Runs_record_recommendations_that_apply_as_indexes()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await SeedPeopleAsync(api, database);
        var recommendations = $"{database.Path}/index-recommendations";

        var empty = await api.Client.GetFromJsonAsync<JsonElement>(Uri(recommendations));
        await RunAsync(api, database, "SELECT * FROM people WHERE email = 'a@example.com'");
        await RunAsync(api, database, "SELECT * FROM people WHERE email = 'b@example.com' ORDER BY address.city DESC");
        await RunAsync(api, database, $"SELECT * FROM people WHERE _id = '{Id("p1")}'");
        var listed = await api.Client.GetFromJsonAsync<JsonElement>(Uri(recommendations));
        using var applied = await api.Client.PostAsync(Uri($"{recommendations}/people:email/apply"), null);
        var appliedBody = await applied.Content.ReadFromJsonAsync<JsonElement>();
        using var again = await api.Client.PostAsync(Uri($"{recommendations}/people:email/apply"), null);
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>();
        using var unknown = await api.Client.PostAsync(Uri($"{recommendations}/people:nothing/apply"), null);
        var remaining = await api.Client.GetFromJsonAsync<JsonElement>(Uri(recommendations));
        var check = await PostAsync(api, $"{database.Path}/sql/check", new { sql = "SELECT * FROM people WHERE email = 'a@example.com'" });

        await Assert.That(empty.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        var items = listed.GetProperty("items");
        await Assert.That(items.EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToList()).IsEquivalentTo(["people:email", "people:address.city"]);
        await Assert.That(items[0].GetProperty("id").GetString()).IsEqualTo("people:email");
        await Assert.That(items[0].GetProperty("collection").GetString()).IsEqualTo("people");
        await Assert.That(items[0].GetProperty("fields")[0].GetString()).IsEqualTo("email");
        await Assert.That(items[0].GetProperty("queries").GetInt64()).IsEqualTo(2);
        await Assert.That(items[0].GetProperty("reason").GetString()).Contains("2 queries");
        await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(applied.Headers.Location!.ToString()).IsEqualTo($"{database.Path}/indexes/people_email");
        await Assert.That(appliedBody.GetProperty("status").GetString()).IsEqualTo("applied");
        await Assert.That(appliedBody.GetProperty("index").GetProperty("name").GetString()).IsEqualTo("people_email");
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(againBody.GetProperty("status").GetString()).IsEqualTo("already-applied");
        await Assert.That(againBody.GetProperty("index").GetProperty("name").GetString()).IsEqualTo("people_email");
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(remaining.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo(["people:address.city"]);
        await Assert.That(check.GetProperty("indexes").EnumerateArray().Select(index => index.GetString()!)).IsEquivalentTo(["people_email"]);
    }

    /// <summary>A database path with, in turn, an unknown organization, project and database.</summary>
    internal static string[] UnknownDatabasePaths(ManagedDatabaseRef database) =>
    [
        $"/v1/management/organizations/{EntityIds.New()}/projects/{database.Project}/databases/{database.Id}",
        $"/v1/management/organizations/{database.Organization}/projects/{EntityIds.New()}/databases/{database.Id}",
        $"{database.ProjectPath}/databases/{EntityIds.New()}",
    ];

    private static async Task<Dictionary<string, Person>> SeedPeopleAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var people = new Dictionary<string, Person>();
        for (var i = 1; i <= 5; i++)
        {
            var person = new Person(Faker.Name.FullName(), $"{i}.{Faker.Internet.Email()}", Faker.Address.City());
            people[$"p{i}"] = person;
            var document = new
            {
                name = person.Name,
                email = person.Email,
                age = i * 10 + 10,
                score = i - 0.5,
                active = i % 2 == 1,
                address = new { city = person.City },
                nickname = i == 1 ? Faker.Internet.UserName() : null,
            };
            (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", $"p{i}")), document)).EnsureSuccessStatusCode();
        }

        return people;
    }

    private static async Task<JsonElement> RunAsync(ManagementApi api, ManagedDatabaseRef database, string sql, CancellationToken cancellationToken = default) =>
        await PostAsync(api, $"{database.Path}/sql", new { sql }, cancellationToken);

    private static async Task<JsonElement> PostAsync(ManagementApi api, string path, object body, CancellationToken cancellationToken = default)
    {
        using var response = await api.Client.PostAsJsonAsync(Uri(path), body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    // The test names of the rows' ids (see ManagementApi.DocumentId).
    private static List<string> Ids(JsonElement result) =>
        [.. result.GetProperty("rows").EnumerateArray().Select(row => ManagementApi.DocumentName(row.GetProperty("id").GetString()!))];

    private static string Id(string name) => ManagementApi.DocumentId(name);

    private static List<string> Suggestions(JsonElement check) =>
        [.. check.GetProperty("suggestions").EnumerateArray().Select(item => $"{item.GetProperty("field").GetString()}: {item.GetProperty("reason").GetString()}")];

    // A JSON object nested to the given depth: {"a":{"a":…{}…}}.
    private static Uri Uri(string path) => ManagementApi.Uri(path);

    private sealed record Person(string Name, string Email, string City);
}
