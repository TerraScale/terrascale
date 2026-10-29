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
    private static readonly string[] Name = ["name"];
    private static readonly string[] Sku = ["sku"];
    private static readonly string[] CustomerEmail = ["customer.email"];
    private static readonly string[] StatusTotal = ["status", "total"];
    private static readonly string[] AbThenC = ["a_b", "c"];
    private static readonly string[] AThenBc = ["a", "b_c"];
    private static readonly string[] PrimaryKey = ["_id"];
    private static readonly string[] EmailTwice = ["email", "email"];

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
    public async Task Invalid_index_definitions_are_rejected()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        object[] invalid =
        [
            new { fields = new[] { "email" } },
            new { collection = "Bad Name", fields = new[] { "email" } },
            new { collection = "people" },
            new { collection = "people", fields = Array.Empty<string>() },
            new { collection = "people", fields = new[] { "a", "b", "c", "d", "e" } },
            new { collection = "people", fields = new[] { "_id" } },
            new { collection = "people", fields = new[] { "_rev" } },
            new { collection = "people", fields = new[] { "a..b" } },
            new { collection = "people", fields = new[] { "email", "email" } },
            new { collection = "people", fields = new[] { "email" }, name = "Bad-Name" },
            new { collection = "people", fields = new[] { "email" }, name = "" },
        ];

        foreach (var body in invalid)
        {
            using var response = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), body);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(JsonSerializer.Serialize(body));
        }

        var list = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/indexes"));
        await Assert.That(list.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Index_errors_answer_their_code_and_message()
    {
        const string BadCollection = "collection must be a lowercase name of at most 63 characters";
        const string BadCount = "fields must hold 1 to 4 distinct fields";
        const string BadField = "fields must be dotted member paths of letters, digits and _, not _id or _rev";
        const string BadName = "name must be a lowercase letter, then lowercase letters, digits or _, at most 63 characters";
        const string RoleTooLow = "your role in the organization does not allow this";
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var indexes = $"{database.Path}/indexes";
        var unknown = $"{database.ProjectPath}/databases/{EntityIds.New()}";
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "p1")), new { email = Faker.Internet.Email(), name = Faker.Name.FullName() }))
            .EnsureSuccessStatusCode();
        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = "SELECT * FROM people WHERE email = 'a@example.com'" })).EnsureSuccessStatusCode();

        (await api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "people", fields = Name, name = "people_email" })).EnsureSuccessStatusCode();
        using var viewer = api.As(await api.AddMemberAsync(api.Client, database.Organization, "viewer"));

        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "Bad Name", fields = Email }), HttpStatusCode.BadRequest, "invalid_request", BadCollection);
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "people" }), HttpStatusCode.BadRequest, "invalid_request", BadCount);
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "people", fields = PrimaryKey }), HttpStatusCode.BadRequest, "invalid_request", BadField);
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "people", fields = EmailTwice }), HttpStatusCode.BadRequest, "invalid_request", BadCount);
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "people", fields = Email, name = "Bad-Name" }), HttpStatusCode.BadRequest, "invalid_request", BadName);
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri(indexes), new { collection = "people", fields = Email, name = "people_email" }),
            HttpStatusCode.Conflict, "index_exists", "an index with this name, or on these fields of the collection, already exists");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri($"{indexes}?collection=Bad!")), HttpStatusCode.BadRequest, "invalid_request", BadCollection);
        await ErrorAssert.ErrorAsync(api.Client.DeleteAsync(Uri($"{indexes}/people_missing")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsync(Uri($"{database.Path}/index-recommendations/people:missing/apply"), null), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsync(Uri($"{database.Path}/index-recommendations/people:email/apply"), null),
            HttpStatusCode.Conflict, "name_taken", "another index already has the default name");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri($"{unknown}/indexes")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri($"{unknown}/indexes"), new { collection = "people", fields = Email }), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.DeleteAsync(Uri($"{unknown}/indexes/people_email")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri($"{unknown}/index-recommendations")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.PostAsync(Uri($"{unknown}/index-recommendations/people:email/apply"), null), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(viewer.PostAsJsonAsync(Uri(indexes), new { collection = "people", fields = Email }), HttpStatusCode.Forbidden, "forbidden_role", RoleTooLow);
        await ErrorAssert.ErrorAsync(viewer.DeleteAsync(Uri($"{indexes}/people_email")), HttpStatusCode.Forbidden, "forbidden_role", RoleTooLow);
        await ErrorAssert.ErrorAsync(
            viewer.PostAsync(Uri($"{database.Path}/index-recommendations/people:email/apply"), null), HttpStatusCode.Forbidden, "forbidden_role", RoleTooLow);
    }

    [Test]
    public async Task Index_changes_on_a_database_whose_storage_is_missing_are_not_found()
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "p1")), new { email = Faker.Internet.Email() })).EnsureSuccessStatusCode();
        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = "SELECT * FROM people WHERE email = 'a@example.com'" })).EnsureSuccessStatusCode();
        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "people", fields = Name })).EnsureSuccessStatusCode();

        hook.Hidden = tenant => tenant.DatabaseId == database.Id;

        await ErrorAssert.ErrorAsync(
            api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "people", fields = Email }), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.DeleteAsync(Uri($"{database.Path}/indexes/people_name")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(
            api.Client.PostAsync(Uri($"{database.Path}/index-recommendations/people:email/apply"), null), HttpStatusCode.NotFound, "not_found");
    }

    [Test]
    public async Task Locations_are_canonical_for_mixed_case_and_trailing_slash_routes()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "p1")), new { email = Faker.Internet.Email() }))
            .EnsureSuccessStatusCode();
        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = "SELECT * FROM people WHERE email = 'a@example.com'" })).EnsureSuccessStatusCode();

        using var applied = await api.Client.PostAsync(Uri($"{database.Path}/INDEX-RECOMMENDATIONS/people:email/apply"), null);

        await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(applied.Headers.Location!.ToString()).IsEqualTo($"{database.Path}/indexes/people_email");

        using var created = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes/"), new { collection = "people", fields = Name });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"{database.Path}/indexes/people_name");
    }

    [Test]
    public async Task Recommendations_for_two_fields_of_a_maximum_length_collection_both_apply()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var collection = "c" + new string('x', 62);
        await ManagementApi.CreateCollectionAsync(data, database, collection);
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, collection, "d1")), new { email = Faker.Internet.Email(), name = Faker.Name.FullName() }))
            .EnsureSuccessStatusCode();
        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = $"SELECT * FROM {collection} WHERE email = 'a' AND name = 'b'" })).EnsureSuccessStatusCode();

        using var email = await api.Client.PostAsync(Uri($"{database.Path}/index-recommendations/{collection}:email/apply"), null);
        using var name = await api.Client.PostAsync(Uri($"{database.Path}/index-recommendations/{collection}:name/apply"), null);
        var names = Names(await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/indexes")));

        await Assert.That(email.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(name.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(names.Distinct().Count()).IsEqualTo(2);
        await Assert.That(names.Max(index => index.Length)).IsLessThanOrEqualTo(63);
        await Assert.That(names.Any(index => System.Text.RegularExpressions.Regex.IsMatch(index, "_email_[0-9a-f]{8}$"))).IsTrue();
        await Assert.That(names.Any(index => System.Text.RegularExpressions.Regex.IsMatch(index, "_name_[0-9a-f]{8}$"))).IsTrue();
    }

    [Test]
    public async Task Definitions_that_join_to_the_same_text_get_distinct_default_names()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();

        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = "SELECT * FROM people_a WHERE email = 'a'" })).EnsureSuccessStatusCode();
        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = "SELECT * FROM people WHERE a_email = 'a'" })).EnsureSuccessStatusCode();

        using var first = await api.Client.PostAsync(Uri($"{database.Path}/index-recommendations/people_a:email/apply"), null);
        using var second = await api.Client.PostAsync(Uri($"{database.Path}/index-recommendations/people:a_email/apply"), null);
        using var left = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "orders", fields = AbThenC });
        using var right = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "orders", fields = AThenBc });
        var names = Names(await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/indexes")));

        HttpStatusCode[] statuses = [first.StatusCode, second.StatusCode, left.StatusCode, right.StatusCode];
        await Assert.That(statuses.All(status => status == HttpStatusCode.Created)).IsTrue();
        await Assert.That(names.Distinct().Count()).IsEqualTo(4);

        await Assert.That(names.Select(index => index[..^9]).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["orders__a__b_uc", "orders__a_ub__c", "people__a_uemail", "people_ua__email"]);
    }

    [Test]
    public async Task Recommendations_keep_counting_recorded_fields_but_stop_recording_new_ones_at_the_cap()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();

        for (var batch = 0; batch < 4; batch++)
        {
            var conditions = Enumerable.Range(batch * 256, 256).Select(i => $"f{i:D4} = 1");
            (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = $"SELECT * FROM people WHERE {string.Join(" OR ", conditions)}" }))
                .EnsureSuccessStatusCode();
        }

        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = "SELECT * FROM people WHERE f0000 = 1 OR f1023 = 1" })).EnsureSuccessStatusCode();
        var pending = (await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/index-recommendations"))).GetProperty("items").GetArrayLength();

        await api.Server.Services.GetRequiredService<QueryFieldRecorder>().FlushAsync(CancellationToken.None);
        var items = (await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/index-recommendations"))).GetProperty("items");
        var ids = items.EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToList();

        await Assert.That(pending).IsEqualTo(1000);
        await Assert.That(ids.Count).IsEqualTo(1000);
        await Assert.That(ids[0]).IsEqualTo("people:f0000");
        await Assert.That(items[0].GetProperty("queries").GetInt64()).IsEqualTo(2);
        await Assert.That(ids.Any(id => string.CompareOrdinal(id, "people:f1000") >= 0)).IsFalse();
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

    [Test]
    public async Task Index_routes_return_404_for_unknown_parents()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        (await api.Client.PostAsJsonAsync(Uri($"{database.Path}/indexes"), new { collection = "people", fields = Email })).EnsureSuccessStatusCode();

        foreach (var path in SqlApiTests.UnknownDatabasePaths(database))
        {
            using var list = await api.Client.GetAsync(Uri($"{path}/indexes"));
            using var create = await api.Client.PostAsJsonAsync(Uri($"{path}/indexes"), new { collection = "people", fields = Name });
            using var drop = await api.Client.DeleteAsync(Uri($"{path}/indexes/people_email"));
            using var recommendations = await api.Client.GetAsync(Uri($"{path}/index-recommendations"));
            using var apply = await api.Client.PostAsync(Uri($"{path}/index-recommendations/people:email/apply"), null);
            HttpStatusCode[] statuses = [list.StatusCode, create.StatusCode, drop.StatusCode, recommendations.StatusCode, apply.StatusCode];
            await Assert.That(statuses.All(status => status == HttpStatusCode.NotFound)).IsTrue().Because(path);
        }

        var indexes = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/indexes"));
        await Assert.That(Names(indexes)).IsEquivalentTo(["people_email"]);
    }

    private static List<string> Names(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
