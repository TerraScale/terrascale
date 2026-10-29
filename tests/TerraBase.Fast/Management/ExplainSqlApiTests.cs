using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ExplainSqlApiTests
{
    private const string Route = "/v1/projects/{project}/databases/{database}/sql/explain";
    private static readonly string[] EmailFields = ["email"];
    private static readonly string[] NameFields = ["name"];

    [Test]
    public async Task Explain_reports_the_real_plan_before_and_after_index_creation_and_only_indexes_used()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var email = await SeedPeopleAsync(api, database);
        var sql = $"SELECT name FROM people WHERE email = '{Literal(email)}' ORDER BY age";

        var before = await ExplainAsync(api, database, sql);
        using var created = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/indexes"),
            new { collection = "people", fields = EmailFields });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var unrelated = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/indexes"),
            new { collection = "people", fields = NameFields });
        await Assert.That(unrelated.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var after = await ExplainAsync(api, database, sql);
        var sorted = await ExplainAsync(api, database, "SELECT email FROM people ORDER BY email LIMIT 2");

        await Assert.That(before.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(before.GetProperty("error").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(before.GetProperty("collection").GetString()).IsEqualTo("people");
        await Assert.That(before.GetProperty("hasLimit").GetBoolean()).IsFalse();
        await Assert.That(Strings(before, "plan")).IsNotEmpty();
        await Assert.That(Strings(before, "indexes")).IsEmpty();
        await Assert.That(Suggestions(before)).IsEquivalentTo(["email: filtered without an index", "age: sorted without an index"]);
        await Assert.That(after.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(Strings(after, "indexes")).IsEquivalentTo(["people_email"]);
        await Assert.That(string.Join('\n', Strings(after, "plan"))).Contains("tb_index_people_email");
        await Assert.That(Suggestions(after)).IsEquivalentTo(["age: sorted without an index"]);
        await Assert.That(sorted.GetProperty("hasLimit").GetBoolean()).IsTrue();
        await Assert.That(Strings(sorted, "indexes")).IsEquivalentTo(["people_email"]);
        await Assert.That(Suggestions(sorted)).IsEmpty();
        await Assert.That(sorted.TryGetProperty("rows", out _)).IsFalse();
    }

    [Test]
    public async Task Explain_neither_meters_reads_nor_creates_or_increments_index_recommendations()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var email = await SeedPeopleAsync(api, database);
        var sql = $"SELECT name FROM people WHERE email = '{Literal(email)}' ORDER BY age LIMIT 1";
        var usagePath = ManagementApi.Uri(database.Path + "/usage");
        var recommendationsPath = ManagementApi.Uri(database.Path + "/index-recommendations");
        var before = await api.Client.GetFromJsonAsync<JsonElement>(usagePath);

        await ExplainAsync(api, database, sql);
        await ExplainAsync(api, database, sql);
        var explained = await api.Client.GetFromJsonAsync<JsonElement>(usagePath);
        var empty = await api.Client.GetFromJsonAsync<JsonElement>(recommendationsPath);
        await Assert.That(explained.GetProperty("reads").GetInt64()).IsEqualTo(before.GetProperty("reads").GetInt64());
        await Assert.That(empty.GetProperty("items").GetArrayLength()).IsEqualTo(0);

        // A real run proves that these same fields and this database do produce usage.
        using var run = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/sql"), new { sql });
        run.EnsureSuccessStatusCode();
        var result = await run.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(result.GetProperty("matched").GetInt32()).IsEqualTo(1);
        var recorded = await api.Client.GetFromJsonAsync<JsonElement>(recommendationsPath);
        await ExplainAsync(api, database, sql);
        var after = await api.Client.GetFromJsonAsync<JsonElement>(usagePath);
        var unchanged = await api.Client.GetFromJsonAsync<JsonElement>(recommendationsPath);

        await Assert.That(after.GetProperty("reads").GetInt64()).IsEqualTo(before.GetProperty("reads").GetInt64() + 1);
        await Assert.That(RecommendationCounts(recorded)).IsEquivalentTo(["people:email=1", "people:age=1"]);
        await Assert.That(RecommendationCounts(unchanged)).IsEquivalentTo(RecommendationCounts(recorded));
    }

    [Test]
    public async Task Explain_accepts_a_scoped_viewer_like_subject_without_membership_only_on_the_client_listener()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await SeedPeopleAsync(api, database);
        using var viewer = ProjectClient(api, database.Project, scope: "documents:read  databases:read", database: database.Id, subject: EntityIds.New().ToString());
        using var anonymous = api.WithToken(null);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read");
        const string Sql = "SELECT name FROM people LIMIT 1";
        var allowed = await ExplainAsync(viewer, database, Sql);
        await Assert.That(allowed.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(allowed.GetProperty("collection").GetString()).IsEqualTo("people");
        using var former = await viewer.PostAsJsonAsync(
            ManagementApi.Uri($"/projects/{database.Project}/databases/{database.Name}/sql/explain"), new { sql = Sql });
        await Assert.That(former.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var noToken = await anonymous.PostAsJsonAsync(ExplainPath(database), new { sql = Sql });
        using var apiKey = await data.PostAsJsonAsync(ExplainPath(database), new { sql = Sql });
        using var adminRequest = new HttpRequestMessage(HttpMethod.Post, ExplainPath(database)) { Content = JsonContent.Create(new { sql = Sql }) };
        adminRequest.Headers.Authorization = viewer.DefaultRequestHeaders.Authorization;
        using var onAdmin = await api.Server.Client.SendAsync(adminRequest);
        await Assert.That(noToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(apiKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // The internal listener serves the route but never takes a ts-auth token.
        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // A sign-in token names no project, so it is not a data credential.
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(ExplainPath(database), new { sql = Sql }), HttpStatusCode.Unauthorized, "invalid_key", "this token does not authorize project routes; use an API key or its project token");
    }

    [Test]
    public async Task Explain_rejects_missing_malformed_or_mismatched_project_database_and_scope_claims_before_sql()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        (string? Project, string? Scope, string? Database, string RouteProject, HttpStatusCode Status)[] cases =
        [
            (null, "databases:read", null, database.Project, HttpStatusCode.Unauthorized),
            ("not-a-project", "databases:read", null, database.Project, HttpStatusCode.Unauthorized),
            (EntityIds.New().ToString(), "databases:read", null, database.Project, HttpStatusCode.NotFound),
            ("not-a-project", "databases:read", null, "not-a-project", HttpStatusCode.Unauthorized),
            ("AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA", "databases:read", null, "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA", HttpStatusCode.Unauthorized),
            (database.Project, "databases:read", EntityIds.New().ToString(), database.Project, HttpStatusCode.NotFound),
            (database.Project, null, null, database.Project, HttpStatusCode.Forbidden),
            (database.Project, "documents:read", null, database.Project, HttpStatusCode.Forbidden),
            (database.Project, "databases:reader", null, database.Project, HttpStatusCode.Forbidden),
        ];
        foreach (var item in cases)
        {
            using var client = ProjectClient(api, item.Project, item.Scope, item.Database);
            var sending = client.PostAsJsonAsync(ExplainPath(database with { Project = item.RouteProject }), new { sql = "DELETE FROM people" });
            if (item.Status == HttpStatusCode.Unauthorized)
            {
                using var response = await sending;
                await Assert.That(response.StatusCode).IsEqualTo(item.Status);
            }
            else
            {
                await ErrorAssert.ErrorAsync(sending, item.Status,
                    item.Status == HttpStatusCode.Forbidden ? "forbidden_scope" : "unknown_database",
                    item.Status == HttpStatusCode.Forbidden ? "the token does not grant databases:read" : "the database does not exist");
            }
        }
    }

    [Test]
    public async Task Explain_rejects_non_string_project_access_claims_without_dropping_database_restrictions()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        object?[] invalid = [null, true, 42, new[] { database.Name }, new Dictionary<string, string> { ["name"] = database.Name }];
        foreach (var name in new[] { "project_id", "database", "scope" })
        {
            foreach (var value in invalid)
            {
                using var client = api.WithToken(api.Issuer.Mint(api.User, descriptor =>
                {
                    descriptor.Claims["project_id"] = database.Project;
                    descriptor.Claims["scope"] = "databases:read";
                    descriptor.Claims[name] = value!;
                }));
                using var response = await client.PostAsJsonAsync(ExplainPath(database), new { sql = "SELECT * FROM people" });
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized).Because($"{name}: {value ?? "null"}");
            }
        }
    }

    [Test]
    public async Task Explain_and_check_return_not_found_when_registered_storage_is_unavailable()
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, database.Project);
        hook.Hidden = tenant => tenant.DatabaseId == database.Id;
        await ErrorAssert.ErrorAsync(client.PostAsJsonAsync(ExplainPath(database), new { sql = "SELECT * FROM people" }),
            HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/sql/check"), new { sql = "SELECT * FROM people" }),
            HttpStatusCode.NotFound, "not_found");
    }

    [Test]
    public async Task Explain_isolates_projects_and_hides_unknown_or_deleted_database_names_before_sql()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var other = await api.CreateManagedDatabaseAsync(database.Name);
        await SeedPeopleAsync(api, database);
        await SeedPeopleAsync(api, other);
        using var created = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/indexes"), new { collection = "people", fields = EmailFields });
        created.EnsureSuccessStatusCode();
        const string Sql = "SELECT email FROM people ORDER BY email LIMIT 1";
        await Assert.That(Strings(await ExplainAsync(api, database, Sql), "indexes")).IsEquivalentTo(["people_email"]);
        await Assert.That(Strings(await ExplainAsync(api, database with { Name = database.Id }, Sql), "indexes")).IsEquivalentTo(["people_email"]);
        await Assert.That(Strings(await ExplainAsync(api, other, Sql), "indexes")).IsEmpty();
        using var client = ProjectClient(api, database.Project);
        foreach (var hidden in new[] { other, database with { Name = "unknown" }, database with { Name = EntityIds.New().ToString() } })
        {
            await ErrorAssert.ErrorAsync(client.PostAsJsonAsync(ExplainPath(hidden), new { sql = "DELETE FROM people" }), HttpStatusCode.NotFound,
                hidden.Project != database.Project ? "unknown_database" : "not_found",
                hidden.Project != database.Project ? "the database does not exist" : null);
        }

        using var deleted = await api.Client.DeleteAsync(ManagementApi.Uri(database.Path));
        deleted.EnsureSuccessStatusCode();
        await ErrorAssert.ErrorAsync(client.PostAsJsonAsync(ExplainPath(database), new { sql = Sql }), HttpStatusCode.NotFound, "not_found");
    }

    [Test]
    public async Task Invalid_sql_returns_invalid_request_while_check_still_returns_ok_false()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, database.Project);
        const string Secret = "zzsecretzz";
        string?[] invalid =
        [
            null,
            "",
            $"SELEC * FROM {Secret}",
            $"DELETE FROM people WHERE email = '{Secret}'",
            $"SELECT * FROM people; SELECT * FROM {Secret}",
            $"SELECT * FROM people WHERE email = '{Secret}' LIMIT 0",
        ];

        foreach (var sql in invalid)
        {
            using var response = await client.PostAsJsonAsync(ExplainPath(database), new { sql });
            var text = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(text);
            using var check = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/sql/check"), new { sql });
            check.EnsureSuccessStatusCode();
            var checkBody = await check.Content.ReadFromJsonAsync<JsonElement>();

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(sql ?? "null sql");
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_request");
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(text).DoesNotContain(Secret);
            await Assert.That(checkBody.GetProperty("ok").GetBoolean()).IsFalse();
        }
    }

    [Test]
    public async Task Explain_maps_an_engine_deadline_to_invalid_query_and_recovers_after_rejection()
    {
        var clock = new SteppingClock();
        await using var api = await ManagementApi.StartAsync(
            TerraBaseTestServer.UseProductionQueryBudget, services: services => services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock));
        var database = await api.CreateManagedDatabaseAsync();
        await SeedPeopleAsync(api, database);
        const string Sql = "SELECT * FROM people LIMIT 1";

        using var client = ProjectClient(api, database.Project);
        clock.Armed = true;
        try
        {
            await ErrorAssert.ErrorAsync(client.PostAsJsonAsync(ExplainPath(database), new { sql = Sql }),
                HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        }
        finally
        {
            clock.Armed = false;
        }

        var recovered = await ExplainAsync(api, database, Sql);
        await Assert.That(recovered.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(Strings(recovered, "plan")).IsNotEmpty();
    }

    [Test]
    public async Task Openapi_documents_the_v1_explain_path_with_JWT_security_and_errors()
    {
        await using var api = await ManagementApi.StartAsync();
        using var anonymous = api.WithToken(null);
        var document = await anonymous.GetFromJsonAsync<JsonElement>(ManagementApi.Uri("/openapi/v1.json"));
        var paths = document.GetProperty("paths");
        var operation = paths.GetProperty(Route).GetProperty("post");

        await Assert.That(operation.GetProperty("operationId").GetString()).IsEqualTo("explainSql");
        await Assert.That(operation.GetProperty("security").EnumerateArray().Any(item => item.TryGetProperty("TsAuth", out _))).IsTrue();
        await Assert.That(operation.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _)).IsTrue();
        await Assert.That(operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example").GetProperty("sql").GetString()).Contains("SELECT");
        await Assert.That(operation.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("example").GetProperty("indexes")[0].GetString()).IsEqualTo("people_email");
        foreach (var status in new[] { "200", "400", "401", "403", "404", "408", "413", "415", "421", "503" })
        {
            await Assert.That(operation.GetProperty("responses").TryGetProperty(status, out _)).IsTrue().Because(status);
        }

        await Assert.That(paths.TryGetProperty("/management/organizations/{organization}" + Route, out _)).IsFalse();
        await Assert.That(paths.TryGetProperty("/v1" + Route, out _)).IsFalse();
        await Assert.That(paths.TryGetProperty("/v2" + Route, out _)).IsFalse();
    }

    private static async Task<string> SeedPeopleAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        var faker = new Faker();
        var email = faker.Internet.Email();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        for (var i = 0; i < 3; i++)
        {
            using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", $"p{i}")),
                new { name = faker.Name.FullName(), email = i == 0 ? email : $"{i}.{email}", age = faker.Random.Int(18, 80) });
            response.EnsureSuccessStatusCode();
        }

        return email;
    }

    private static async Task<JsonElement> ExplainAsync(ManagementApi api, ManagedDatabaseRef database, string sql)
    {
        using var client = ProjectClient(api, database.Project);
        return await ExplainAsync(client, database, sql);
    }

    private static HttpClient ProjectClient(ManagementApi api, string? project, string? scope = "databases:read", string? database = null, string? subject = null) =>
        api.WithToken(api.Issuer.Mint(subject ?? api.User, descriptor =>
        {
            if (project is not null)
            {
                descriptor.Claims["project_id"] = project;
            }

            if (scope is not null)
            {
                descriptor.Claims["scope"] = scope;
            }

            if (database is not null)
            {
                descriptor.Claims["database"] = database;
            }
        }));

    private static async Task<JsonElement> ExplainAsync(HttpClient client, ManagedDatabaseRef database, string sql)
    {
        using var response = await client.PostAsJsonAsync(ExplainPath(database), new { sql });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Uri ExplainPath(ManagedDatabaseRef database) =>
        ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{database.Name}/sql/explain");

    private static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static IEnumerable<string> Strings(JsonElement body, string property) =>
        body.GetProperty(property).EnumerateArray().Select(item => item.GetString()!);

    private static IEnumerable<string> Suggestions(JsonElement body) =>
        body.GetProperty("suggestions").EnumerateArray().Select(item => $"{item.GetProperty("field").GetString()}: {item.GetProperty("reason").GetString()}");

    private static IEnumerable<string> RecommendationCounts(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(item => $"{item.GetProperty("id").GetString()}={item.GetProperty("queries").GetInt64()}");
}
