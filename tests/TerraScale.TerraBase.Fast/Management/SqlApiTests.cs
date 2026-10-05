using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.Documents;
using TerraScale.TerraBase.Tenancy;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Management;

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
    public async Task Wide_document_projections_resolve_many_fields_without_repeated_member_scans()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "wide");
        var members = string.Join(',', Enumerable.Range(0, 65_000).Select(index => $"\"f{index:D5}\":{index}"));
        // Legacy bodies may be wider than the data API's current 4096-node limit. Queries must still bound
        // their projection work rather than scanning all 65000 members once for each selected field.
        await api.PutStoredAsync(data, database, "wide", "d0", "{" + members + "}");
        var selected = Enumerable.Range(63_200, 1800).Select(index => $"f{index:D5}").ToArray();

        var result = await RunAsync(api, database, "SELECT " + string.Join(',', selected) + " FROM wide")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var projected = result.GetProperty("rows")[0].GetProperty("value");

        await Assert.That(result.GetProperty("truncated").GetBoolean()).IsFalse();
        await Assert.That(projected.EnumerateObject().Select(member => member.Name)).IsEquivalentTo(selected);
        await Assert.That(projected.GetProperty("f63200").GetInt32()).IsEqualTo(63_200);
        await Assert.That(projected.GetProperty("f64999").GetInt32()).IsEqualTo(64_999);
    }

    [Test]
    public async Task Nested_wide_projections_enumerate_each_object_once()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "nested");
        // 20000 sibling objects; selecting a.x from 1500 of them reaches 1500 distinct objects in one row.
        var members = string.Join(',', Enumerable.Range(0, 20_000).Select(index => $"\"a{index:D5}\":{{\"x\":{index}}}"));
        await api.PutStoredAsync(data, database, "nested", "d0", "{" + members + "}");
        var selected = Enumerable.Range(18_500, 1500).Select(index => $"a{index:D5}.x").ToArray();

        var result = await RunAsync(api, database, "SELECT " + string.Join(',', selected) + " FROM nested")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var projected = result.GetProperty("rows")[0].GetProperty("value");

        await Assert.That(projected.EnumerateObject().Select(member => member.Name)).IsEquivalentTo(selected);
        await Assert.That(projected.GetProperty("a18500.x").GetInt32()).IsEqualTo(18_500);
        await Assert.That(projected.GetProperty("a19999.x").GetInt32()).IsEqualTo(19_999);
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
    public async Task Invalid_sql_is_rejected_on_run_and_reported_on_check()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        const string secret = "zzsecretzz";
        string[] invalid =
        [
            "",
            $"SELEC * FROM {secret}",
            $"DELETE FROM people WHERE {secret} = 1",
            $"SELECT * FROM people; SELECT * FROM {secret}",
            $"SELECT * FROM people p JOIN {secret} s ON p.id = s.id",
            $"SELECT count({secret}) FROM people",
            $"SELECT * FROM people WHERE {secret} = $1",
            $"SELECT * FROM people WHERE {secret} LIKE 'a%'",
            // Versions are opaque: _rev can be selected, not compared or sorted.
            $"SELECT {secret} FROM people WHERE _rev = 1",
            $"SELECT {secret} FROM people ORDER BY _rev",
            $"SELECT * FROM people WHERE {secret} = NULL",
            $"SELECT * FROM people WHERE 1 = {secret}",
            $"SELECT * FROM people WHERE {secret} IN (SELECT 1)",
            $"WITH x AS (SELECT * FROM people) SELECT * FROM {secret}",
            $"SELECT * FROM people GROUP BY {secret}",
            $"SELECT DISTINCT {secret} FROM people",
            $"SELECT {secret} AS alias FROM people",
            $"SELECT * FROM people ORDER BY a, b, c, d, {secret}",
            "SELECT * FROM people LIMIT 0",
            "SELECT * FROM people LIMIT 1001",
            "SELECT * FROM people LIMIT 10 OFFSET 5",
            "SELECT * FROM Bad_Collection",
            $"SELECT * FROM people WHERE {secret} = 'unterminated",
            "SELECT * FROM people WHERE " + new string('(', 100) + "a = 1" + new string(')', 100),
            "SELECT * FROM people WHERE a = '" + new string('x', 17 * 1024) + "'",
        ];

        foreach (var sql in invalid)
        {
            using var run = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql });
            var error = await run.Content.ReadAsStringAsync();
            var check = await PostAsync(api, $"{database.Path}/sql/check", new { sql });

            await Assert.That(run.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(sql);
            await Assert.That(error).DoesNotContain(secret).Because(sql);
            await Assert.That(check.GetProperty("ok").GetBoolean()).IsFalse().Because(sql);
            await Assert.That(check.GetProperty("error").GetString()).IsNotNullOrEmpty().Because(sql);
            await Assert.That(check.GetProperty("error").GetString()!).DoesNotContain(secret).Because(sql);
        }
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

    [Test]
    public async Task Projections_read_the_first_stored_duplicate_member_like_filters()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "dup")), new { age = 0 })).EnsureSuccessStatusCode();
        // The data API rejects duplicate members now; documents stored before it did can still hold them.
        await ManagementApi.ReplaceStoredBodyAsync(api.Server.DataDirectory, database, "people", "dup", """{"age":20,"age":30,"n":{"x":1,"x":2}}""");

        var result = await RunAsync(api, database, "SELECT age, n.x FROM people WHERE age = 20");

        var value = result.GetProperty("rows")[0].GetProperty("value");
        await Assert.That(value.GetProperty("age").GetInt32()).IsEqualTo(20);
        await Assert.That(value.GetProperty("n.x").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Time_a_consumer_spends_between_rows_is_not_counted_against_the_query_deadline()
    {
        var time = new FakeTimeProvider();
        await using var api = await ManagementApi.StartAsync(services: services => services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, time));
        var database = await api.CreateManagedDatabaseAsync();
        await SeedPeopleAsync(api, database);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await using var transaction = (await engine.BeginTransactionAsync(
            new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id), write: false, CancellationToken.None))!;

        var read = 0;
        await foreach (var _ in transaction.QuerySqlAsync(new SqlQuery("people", null, [], 100), CancellationToken.None))
        {
            // Alone, the consumer's work between two rows takes longer than the whole 5-second deadline.
            time.Advance(TimeSpan.FromSeconds(6));
            read++;
        }

        await Assert.That(read).IsEqualTo(5);
        // The deadline ran on the faked clock, so the 30 seconds the consumer took were seen and not counted.
        await Assert.That(api.Server.Services.GetRequiredService<TerraScale.TerraBase.Engine.Sqlite.SqliteStorageEngine>().Time).IsSameReferenceAs(time);
    }

    [Test]
    public async Task A_query_deadline_clock_that_starts_at_zero_still_bounds_a_query()
    {
        var clock = new SteppingClock();
        await using var api = await ManagementApi.StartAsync(services: services => services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock));
        var database = await api.CreateManagedDatabaseAsync();
        await SeedPeopleAsync(api, database);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await using var transaction = (await engine.BeginTransactionAsync(
            new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id), write: false, CancellationToken.None))!;

        // The count starts at timestamp 0 and its next reading is 6 seconds later, past the 5-second deadline.
        clock.Armed = true;

        await Assert.ThrowsAsync<QueryCapacityException>(() => transaction.CountCollectionsAsync(CancellationToken.None).AsTask());
    }

    // Reads 0 until armed; then each reading returns the current time and moves it 6 seconds on.
    private sealed class SteppingClock : TimeProvider
    {
        private static readonly long Step = 6 * TimeSpan.TicksPerSecond;
        private long _now;

        public bool Armed { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Armed ? Interlocked.Add(ref _now, Step) - Step : Interlocked.Read(ref _now);
    }

    [Test]
    public async Task A_quoted_name_with_a_dot_is_refused_and_quoted_parts_still_name_nested_members()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        // A member literally named a.b beside the nested a.b.
        await api.PutStoredAsync(data, database, "items", "d0", """{"a.b":1,"a":{"b":2}}""");

        var refused = new List<HttpStatusCode>();
        foreach (var sql in new[] { "SELECT \"a.b\" FROM items", "SELECT * FROM items WHERE \"a.b\" = 1", "SELECT * FROM items ORDER BY \"a.b\"" })
        {
            using var response = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql });
            refused.Add(response.StatusCode);
        }

        var nested = await RunAsync(api, database, "SELECT \"a\".\"b\" FROM items");

        await Assert.That(refused).IsEquivalentTo(Enumerable.Repeat(HttpStatusCode.BadRequest, 3));
        await Assert.That(nested.GetProperty("rows")[0].GetProperty("value").GetProperty("a.b").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Overlapping_projections_stop_at_the_output_budget()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        // Eight bodies of about 0.95 MiB fit 8 MiB; selecting a, a.b and a.b.c emits each body three times.
        var body = "{\"a\":{\"b\":{\"c\":\"" + new string('x', 1_000_000) + "\"}}}";
        await ManagementApi.CreateCollectionAsync(data, database, "big");
        for (var i = 0; i < 8; i++)
        {
            await api.PutStoredAsync(data, database, "big", $"d{i}", body);
        }

        using var response = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = "SELECT a, a.b, a.b.c FROM big" });
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var json = JsonDocument.Parse(bytes);
        var result = json.RootElement;

        await Assert.That(result.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("matched").GetInt32()).IsLessThan(8);
        await Assert.That(bytes.Length).IsLessThanOrEqualTo(8 * 1024 * 1024);
    }

    [Test]
    public async Task Small_projections_of_documents_larger_in_total_than_the_budget_return_every_row()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        // Ten bodies of about 0.95 MiB pass 8 MiB together; their ids alone are a few hundred bytes.
        var body = $$"""{"padding":"{{new string('x', 1_000_000)}}"}""";
        await ManagementApi.CreateCollectionAsync(data, database, "big");
        for (var i = 0; i < 10; i++)
        {
            await api.PutStoredAsync(data, database, "big", $"d{i}", body);
        }

        var result = await RunAsync(api, database, "SELECT _id FROM big LIMIT 100");

        await Assert.That(result.GetProperty("truncated").GetBoolean()).IsFalse();
        await Assert.That(Ids(result)).IsEquivalentTo(Enumerable.Range(0, 10).Select(i => $"d{i}"));
    }

    [Test]
    public async Task A_run_does_not_wait_for_the_catalog_to_count_its_unindexed_fields()
    {
        var hook = new UsageCommitHook((collection, _, _) => collection.EndsWith("/projects", StringComparison.Ordinal));
        await using var api = await ManagementApi.StartAsync(services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        await SeedPeopleAsync(api, database);

        // A project creation holds the catalog's write lock while the run counts email as unindexed.
        using var gate = hook.PauseCommit();
        var creating = api.Client.PostAsJsonAsync(Uri($"/v1/management/organizations/{database.Organization}/projects"), new { name = "Held" });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await RunAsync(api, database, "SELECT _id FROM people WHERE email = 'nobody'").WaitAsync(TimeSpan.FromSeconds(10));
        gate.Dispose();
        (await creating).EnsureSuccessStatusCode();
        var recommendations = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"{database.Path}/index-recommendations"));

        await Assert.That(result.GetProperty("matched").GetInt32()).IsEqualTo(0);
        await Assert.That(recommendations.GetProperty("items").EnumerateArray()
            .Select(item => $"{item.GetProperty("id").GetString()}={item.GetProperty("queries").GetInt64()}")).IsEquivalentTo(["people:email=1"]);
    }

    [Test]
    public async Task Documents_at_the_accepted_json_depth_are_queried()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "deep");
        // The deepest document the data API accepts (RequestJson), and one level deeper.
        using var deepest = new StringContent(Nested(RequestJson.MaxDepth), null, "application/json");
        using var tooDeep = new StringContent(Nested(RequestJson.MaxDepth + 1), null, "application/json");
        using var accepted = await data.PutAsync(Uri(ManagementApi.DocumentPath(database, "deep", "d0")), deepest);
        using var rejected = await data.PutAsync(Uri(ManagementApi.DocumentPath(database, "deep", "d1")), tooDeep);
        using var read = await data.GetAsync(Uri(ManagementApi.DocumentPath(database, "deep", "d0")));
        var document = await read.Content.ReadFromJsonAsync<JsonElement>();

        var all = await RunAsync(api, database, "SELECT * FROM deep");
        var projected = await RunAsync(api, database, "SELECT a FROM deep");

        await Assert.That(accepted.IsSuccessStatusCode).IsTrue();
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(document.GetProperty("document").GetRawText()).IsEqualTo(Nested(RequestJson.MaxDepth));
        await Assert.That(all.GetProperty("rows")[0].GetProperty("value").GetRawText()).IsEqualTo(Nested(RequestJson.MaxDepth));
        await Assert.That(projected.GetProperty("rows")[0].GetProperty("value").GetRawText()).IsEqualTo(Nested(RequestJson.MaxDepth));
    }

    [Test]
    [Arguments("*")]
    [Arguments("_id, padding")]
    public async Task Output_budget_includes_escaped_ids_row_wrappers_and_envelope(string projection)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        // Measure the actual empty-value response, then add enough ASCII padding to exceed 8 MiB by exactly one byte.
        // Every stored body fits 1 MiB; omitting even commas or the envelope from accounting would accept all eight rows.
        // Ids that escape to six times their length: legacy ids an earlier build stored, which the data API still serves.
        await ManagementApi.CreateCollectionAsync(data, database, "big");
        for (var i = 0; i < 8; i++)
        {
            (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "big", $"r{i}")), new { padding = "" })).EnsureSuccessStatusCode();
            await ManagementApi.ExecuteStoredAsync(
                api.Server.DataDirectory, database, "UPDATE documents SET id = $legacy WHERE collection = 'big' AND id = $id;",
                ("$legacy", i + new string('<', 255)), ("$id", Id($"r{i}")));
        }

        using var baseline = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = $"SELECT {projection} FROM big" });
        baseline.EnsureSuccessStatusCode();
        var paddingBytes = 8 * 1024 * 1024 + 1 - (await baseline.Content.ReadAsByteArrayAsync()).Length;
        for (var i = 0; i < 8; i++)
        {
            var padding = new string('x', paddingBytes / 8 + (i < paddingBytes % 8 ? 1 : 0));
            await ManagementApi.ExecuteStoredAsync(
                api.Server.DataDirectory, database, "UPDATE documents SET body = $body WHERE collection = 'big' AND id = $legacy;",
                ("$body", JsonSerializer.Serialize(new { padding })), ("$legacy", i + new string('<', 255)));
        }

        using var response = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = $"SELECT {projection} FROM big" });
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var json = JsonDocument.Parse(bytes);

        await Assert.That(bytes.Length).IsLessThanOrEqualTo(8 * 1024 * 1024);
        await Assert.That(json.RootElement.GetProperty("matched").GetInt32()).IsEqualTo(7);
        await Assert.That(json.RootElement.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(json.RootElement.GetProperty("rows")[0].GetProperty("id").GetString()).IsEqualTo("0" + new string('<', 255));
    }

    [Test]
    public async Task A_single_row_larger_than_the_output_budget_once_escaped_is_not_returned()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        // 900 000 '<' are under 1 MiB stored but 5.4 MB each once escaped as \u003C; a and a.b emit them twice.
        await ManagementApi.CreateCollectionAsync(data, database, "big");
        await api.PutStoredAsync(data, database, "big", "d0", $$$"""{"a":{"b":"{{{new string('<', 900_000)}}}"}}""");

        var result = await RunAsync(api, database, "SELECT a, a.b FROM big");

        await Assert.That(result.GetProperty("matched").GetInt32()).IsEqualTo(0);
        await Assert.That(result.GetProperty("truncated").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Sql_routes_return_404_for_unknown_parents()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var body = new { sql = "SELECT * FROM people" };

        foreach (var path in UnknownDatabasePaths(database))
        {
            using var run = await api.Client.PostAsJsonAsync(Uri($"{path}/sql"), body);
            using var check = await api.Client.PostAsJsonAsync(Uri($"{path}/sql/check"), body);
            await Assert.That(run.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because(path);
            await Assert.That(check.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because(path);
        }
    }

    /// <summary>A database path with, in turn, an unknown organization, project and database.</summary>
    internal static string[] UnknownDatabasePaths(ManagedDatabaseRef database) =>
    [
        $"/v1/management/organizations/{Guid.NewGuid()}/projects/{database.Project}/databases/{database.Id}",
        $"/v1/management/organizations/{database.Organization}/projects/{Guid.NewGuid()}/databases/{database.Id}",
        $"{database.ProjectPath}/databases/{Guid.NewGuid()}",
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

    private static async Task<JsonElement> RunAsync(ManagementApi api, ManagedDatabaseRef database, string sql) =>
        await PostAsync(api, $"{database.Path}/sql", new { sql });

    private static async Task<JsonElement> PostAsync(ManagementApi api, string path, object body)
    {
        using var response = await api.Client.PostAsJsonAsync(Uri(path), body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // The test names of the rows' ids (see ManagementApi.DocumentId).
    private static List<string> Ids(JsonElement result) =>
        [.. result.GetProperty("rows").EnumerateArray().Select(row => ManagementApi.DocumentName(row.GetProperty("id").GetString()!))];

    private static string Id(string name) => ManagementApi.DocumentId(name);

    private static List<string> Suggestions(JsonElement check) =>
        [.. check.GetProperty("suggestions").EnumerateArray().Select(item => $"{item.GetProperty("field").GetString()}: {item.GetProperty("reason").GetString()}")];

    // A JSON object nested to the given depth: {"a":{"a":…{}…}}.
    private static string Nested(int depth) => string.Concat(Enumerable.Repeat("{\"a\":", depth - 1)) + "{}" + new string('}', depth - 1);

    private static Uri Uri(string path) => ManagementApi.Uri(path);

    private sealed record Person(string Name, string Email, string City);
}
