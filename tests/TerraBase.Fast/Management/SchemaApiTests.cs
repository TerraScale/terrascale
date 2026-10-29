using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Engine.Lattices;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class SchemaApiTests
{
    private const string BadCollection = "collection must be a lowercase name of at most 63 characters";

    private static readonly Faker Faker = new();

    [Test]
    public async Task Unique_schema_declaration_propagates_catalog_refusal_after_route_resolution()
    {
        var refuse = false;
        var lookups = 0;
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider => UsageApiTests.SlowManagement.Wrap(
                (IManagementService)registration.ImplementationFactory!(provider), _ => { },
                operation =>
                {
                    if (Volatile.Read(ref refuse) && operation == nameof(IManagementService.GetWritableDatabaseTenantAsync))
                    {
                        Interlocked.Increment(ref lookups);
                        return new ValueTask<WritableDatabaseTenantResult>(ManagementErrors.CatalogUnsupported);
                    }

                    return null;
                }));
        });
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var signed = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims!["project_id"] = database.Project;
            token.Claims!["scope"] = "databases:write";
        }));
        var path = Uri(database.Data + "/schema/people");
        var declaration = new { fields = new[] { new { name = "email", unique = true } } };
        Volatile.Write(ref refuse, true);
        await ErrorAssert.ErrorAsync(signed.PutAsJsonAsync(path, declaration), HttpStatusCode.ServiceUnavailable,
            "unavailable", "the management catalog has an old or unsupported format; discard the affected data");
        await Assert.That(Volatile.Read(ref lookups)).IsEqualTo(1);
        Volatile.Write(ref refuse, false);
        using var recovered = await signed.PutAsJsonAsync(path, declaration);
        recovered.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Collections_are_listed_with_document_counts_and_bytes()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        using var empty = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/collections")));
        var people = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            people.Add(await PutAsync(data, database, "people", $"p{i}", new { name = Faker.Name.FullName(), email = Faker.Internet.Email() }));
        }

        var orders = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            orders.Add(await PutAsync(data, database, "orders", $"o{i}", new { total = Faker.Random.Decimal(1, 500), item = Faker.Commerce.ProductName() }));
        }

        using var listed = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/collections")));

        await Assert.That(empty.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        var items = listed.RootElement.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(items.Select(item => item.GetProperty("name").GetString()!)).IsEquivalentTo(["orders", "people"]);
        await Assert.That(items[0].GetProperty("name").GetString()).IsEqualTo("orders");
        await Assert.That(items[0].GetProperty("documents").GetInt64()).IsEqualTo(2);
        await Assert.That(items[0].GetProperty("bytes").GetInt64()).IsEqualTo(orders.Sum(StoredBytes));
        await Assert.That(items[1].GetProperty("documents").GetInt64()).IsEqualTo(3);
        await Assert.That(items[1].GetProperty("bytes").GetInt64()).IsEqualTo(people.Sum(StoredBytes));
    }

    // Storage keeps the materialized document: canonical JSON with relaxed escaping, fields in name order.
    private static long StoredBytes(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return DocumentState.Bottom.Put(default, parsed.RootElement).Materialize()!.Length;
    }

    [Test]
    public async Task The_schema_is_inferred_from_nested_and_mixed_documents()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await PutAsync(data, database, "people", "a", new
        {
            name = Faker.Name.FullName(),
            age = Faker.Random.Int(18, 90),
            address = new { city = Faker.Address.City(), zip = Faker.Address.ZipCode() },
            tags = new[] { Faker.Lorem.Word() },
        });
        await PutAsync(data, database, "people", "b", new
        {
            name = Faker.Name.FullName(),
            age = Faker.Lorem.Word(),
            address = new { city = Faker.Address.City() },
            score = 1.5,
        });
        await PutAsync(data, database, "people", "c", new { name = (string?)null, age = Faker.Random.Int(18, 90), active = Faker.Random.Bool() });

        using var schema = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/collections/people/schema")));

        var root = schema.RootElement;
        await Assert.That(root.GetProperty("collection").GetString()).IsEqualTo("people");
        await Assert.That(root.GetProperty("documents").GetInt64()).IsEqualTo(3);
        await Assert.That(root.GetProperty("sampledDocuments").GetInt32()).IsEqualTo(3);
        var fields = root.GetProperty("fields").EnumerateArray().ToDictionary(
            field => field.GetProperty("path").GetString()!,
            field => (Present: field.GetProperty("present").GetInt32(), Types: field.GetProperty("types").EnumerateObject().ToDictionary(type => type.Name, type => type.Value.GetInt32())));
        await Assert.That(root.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("path").GetString()!).ToList())
            .IsEquivalentTo(["active", "address", "address.city", "address.zip", "age", "name", "score", "tags"]);
        await Assert.That(fields["active"].Types).IsEquivalentTo(new Dictionary<string, int> { ["boolean"] = 1 });
        await Assert.That(fields["address"].Present).IsEqualTo(2);
        await Assert.That(fields["address"].Types).IsEquivalentTo(new Dictionary<string, int> { ["object"] = 2 });
        await Assert.That(fields["address.city"].Present).IsEqualTo(2);
        await Assert.That(fields["address.zip"].Present).IsEqualTo(1);
        await Assert.That(fields["age"].Present).IsEqualTo(3);
        await Assert.That(fields["age"].Types).IsEquivalentTo(new Dictionary<string, int> { ["integer"] = 2, ["string"] = 1 });
        await Assert.That(fields["name"].Types).IsEquivalentTo(new Dictionary<string, int> { ["string"] = 2, ["null"] = 1 });
        await Assert.That(fields["score"].Types).IsEquivalentTo(new Dictionary<string, int> { ["number"] = 1 });
        await Assert.That(fields["tags"].Types).IsEquivalentTo(new Dictionary<string, int> { ["array"] = 1 });
    }

    [Test]
    public async Task Literal_dotted_member_names_never_merge_with_nested_paths()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "things");
        using var put = await data.PutAsync(
            Uri(ManagementApi.DocumentPath(database, "things", "t1")),
            new StringContent("""{"a.b":0,"a":{"b":{"c":1}},"say \"hi\"":true}""", null, "application/json"));
        put.EnsureSuccessStatusCode();

        using var schema = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{database.Path}/collections/things/schema")));

        var paths = schema.RootElement.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("path").GetString()!).ToList();
        await Assert.That(paths).IsEquivalentTo(["\"a.b\"", "a", "a.b", "a.b.c", "\"say \"\"hi\"\"\""]);
    }

    [Test]
    public async Task Stored_duplicate_members_use_the_first_value_and_its_entire_subtree()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        // The data API rejects duplicate members now; documents stored before it did can still hold them.
        string[] documents =
        [
            """{"a":{},"a":{"ignored":1},"mixed":1,"mixed":"later","n":{"x":{"b":1},"x":{"ignored":true}},"scalar":null,"scalar":{"ignored":true}}""",
            """{"a":{"b":1,"b":"later"},"\u0061":{"ignored":true},"mixed":"first","mixed":false,"n":{"x":{"b":"first"}},"n":{"ignored":true},"scalar":{"b":true},"scalar":null}""",
        ];
        for (var i = 0; i < documents.Length; i++)
        {
            await PutAsync(data, database, "things", $"d{i}", new { placeholder = true });
            await ManagementApi.ReplaceStoredBodyAsync(api.Server.DataDirectory, database, "things", $"d{i}", documents[i]);
        }

        using var schema = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{database.Path}/collections/things/schema")));
        var fields = schema.RootElement.GetProperty("fields").EnumerateArray().ToDictionary(field => field.GetProperty("path").GetString()!);
        await Assert.That(fields.Keys).IsEquivalentTo(["a", "a.b", "mixed", "n", "n.x", "n.x.b", "scalar", "scalar.b"]);
        await Assert.That(fields["a"].GetProperty("present").GetInt32()).IsEqualTo(2);
        await Assert.That(fields["a.b"].GetProperty("present").GetInt32()).IsEqualTo(1);
        await Assert.That(fields["a.b"].GetProperty("types").GetProperty("integer").GetInt32()).IsEqualTo(1);
        await Assert.That(fields["mixed"].GetProperty("types").EnumerateObject().Select(type => (type.Name, type.Value.GetInt32())))
            .IsEquivalentTo([("integer", 1), ("string", 1)]);
        await Assert.That(fields["n.x.b"].GetProperty("present").GetInt32()).IsEqualTo(2);
        await Assert.That(fields["scalar.b"].GetProperty("present").GetInt32()).IsEqualTo(1);

        using var sql = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = "SELECT a.b, mixed, n.x.b, scalar.b FROM things WHERE a.b = 1" });
        sql.EnsureSuccessStatusCode();
        var result = await sql.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(result.GetProperty("matched").GetInt32()).IsEqualTo(1);
        var row = result.GetProperty("rows")[0];
        await Assert.That(row.GetProperty("id").GetString()).IsEqualTo(ManagementApi.DocumentId("d1"));
        await Assert.That(row.GetProperty("value").GetProperty("a.b").GetInt32()).IsEqualTo(1);
        await Assert.That(row.GetProperty("value").GetProperty("mixed").GetString()).IsEqualTo("first");
        await Assert.That(row.GetProperty("value").GetProperty("n.x.b").GetString()).IsEqualTo("first");
    }

    [Test]
    public async Task Sampling_stops_before_a_document_that_exceeds_the_byte_budget()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        // Nine documents of about 0.95 MiB, stored without uploads: the 8 MiB budget fits eight, and the ninth starts a new page.
        var body = $$"""{"padding":"{{new string('x', 1_000_000)}}"}""";
        await ManagementApi.CreateCollectionAsync(data, database, "big");
        for (var i = 0; i < 9; i++)
        {
            await api.PutStoredAsync(data, database, "big", $"d{i}", body);
        }

        using var response = await api.Client.GetAsync(Uri($"{database.Path}/collections/big/schema"));
        if (!response.IsSuccessStatusCode)
        {
            var memory = GC.GetGCMemoryInfo();
            TUnit.Core.TestContext.Current!.OutputWriter.WriteLine(
                $"schema sampling: status={(int)response.StatusCode}, heap={memory.HeapSizeBytes}, fragmented={memory.FragmentedBytes}, committed={memory.TotalCommittedBytes}, available={memory.TotalAvailableMemoryBytes}, load={memory.MemoryLoadBytes}");
        }

        response.EnsureSuccessStatusCode();
        using var schema = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        await Assert.That(schema.RootElement.GetProperty("documents").GetInt64()).IsEqualTo(9);
        await Assert.That(schema.RootElement.GetProperty("sampledDocuments").GetInt64()).IsEqualTo(8);
    }

    [Test]
    public async Task Members_are_walked_to_depth_eight_and_no_deeper()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        // l1 → l2 → … → l9: the eighth member is recorded as an object, the ninth below it is not.
        await PutAsync(data, database, "deep", "d1", new { l1 = new { l2 = new { l3 = new { l4 = new { l5 = new { l6 = new { l7 = new { l8 = new { l9 = Faker.Random.Int() } } } } } } } } });

        using var schema = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{database.Path}/collections/deep/schema")));

        var fields = schema.RootElement.GetProperty("fields").EnumerateArray().ToDictionary(field => field.GetProperty("path").GetString()!);
        await Assert.That(fields.Keys).IsEquivalentTo(Enumerable.Range(1, 8).Select(depth => string.Join('.', Enumerable.Range(1, depth).Select(level => $"l{level}"))));
        await Assert.That(fields["l1.l2.l3.l4.l5.l6.l7.l8"].GetProperty("types").GetProperty("object").GetInt32()).IsEqualTo(1);
    }

    [Test]
    [Arguments("long-parent")]
    [Arguments("field-count")]
    [Arguments("path-total")]
    [Arguments("utf8-path")]
    public async Task Schema_expansion_limits_reject_oversized_field_tables(string shape)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "wide");
        var fields = string.Join(',', Enumerable.Range(0, shape == "field-count" ? 10_001 : 3000)
            .Select(index => $"\"f{index:D5}\":0"));
        var document = shape switch
        {
            // A valid body smaller than 1 MiB whose unbounded inferred paths would occupy gigabytes.
            "long-parent" => "{\"" + new string('a', 900_000) + "\":{" + fields + "}}",
            "path-total" => "{\"" + new string('a', 512) + "\":{" + fields + "}}",
            "utf8-path" => "{\"" + new string('é', 512) + "\":0}",
            _ => "{" + fields + "}",
        };
        // A legacy stored document can exceed the data API's current 4096-node limit.
        await api.PutStoredAsync(data, database, "wide", "d0", document);

        await ErrorAssert.ErrorAsync(
            api.Client.GetAsync(Uri($"{database.Path}/collections/wide/schema")), HttpStatusCode.BadRequest, "invalid_query", "schema too expensive");
    }

    [Test]
    public async Task An_exhausted_engine_read_budget_is_too_expensive()
    {
        var clock = new SteppingClock();
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(TerraBaseTestServer.UseProductionQueryBudget, services: services =>
        {
            hook.Install(services);
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock);
        });
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await PutAsync(data, database, "people", "p1", new { name = Faker.Name.FullName() });
        // From the first read of the database on, every engine read outlasts the 5-second deadline.
        hook.BeforeBegin = (tenant, _) =>
        {
            clock.Armed |= tenant.DatabaseId == database.Id;
            return ValueTask.CompletedTask;
        };

        await ErrorAssert.ErrorAsync(
            api.Client.GetAsync(Uri(database.Path + "/collections")), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
        await ErrorAssert.ErrorAsync(
            api.Client.GetAsync(Uri(database.Path + "/collections/people/schema")), HttpStatusCode.BadRequest, "invalid_query", "schema too expensive");
        await ErrorAssert.ErrorAsync(
            api.Client.GetAsync(Uri(database.Path + "/usage")), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
    }

    [Test]
    public async Task Schema_response_bounds_include_json_escaping_of_paths()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "escaped");
        var parent = new string('<', 100);
        for (var page = 0; page < 3; page++)
        {
            var fields = string.Join(',', Enumerable.Range(page * 3000, 3000).Select(index => $"\"f{index:D5}\":0"));
            using var content = new StringContent("{\"" + parent + "\":{" + fields + "}}", Encoding.UTF8, "application/json");
            using var put = await data.PutAsync(Uri(ManagementApi.DocumentPath(database, "escaped", $"d{page}")), content);
            put.EnsureSuccessStatusCode();
        }

        using var response = await api.Client.GetAsync(Uri($"{database.Path}/collections/escaped/schema"));
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var schema = JsonDocument.Parse(bytes);

        await Assert.That(schema.RootElement.GetProperty("fields").GetArrayLength()).IsEqualTo(9001);
        await Assert.That(bytes.Length).IsLessThanOrEqualTo(8 * 1024 * 1024);
    }

    [Test]
    public async Task Unknown_resources_are_not_found_and_invalid_collections_are_rejected()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await PutAsync(data, database, "emptied", "x", new { name = Faker.Name.FullName() });
        (await data.DeleteAsync(Uri(ManagementApi.DocumentPath(database, "emptied", "x")))).EnsureSuccessStatusCode();
        var unknownDatabase = $"{database.ProjectPath}/databases/{EntityIds.New()}";
        var unknownProject = $"/v1/management/organizations/{database.Organization}/projects/{EntityIds.New()}/databases/{database.Id}";

        using var emptiedCollection = await api.Client.GetAsync(Uri(database.Path + "/collections/emptied/schema"));

        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri(database.Path + "/collections/nothing/schema")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(
            api.Client.GetAsync(Uri(database.Path + "/collections/Not-Valid/schema")), HttpStatusCode.BadRequest, "invalid_request", BadCollection);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri(unknownDatabase + "/collections")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri(unknownDatabase + "/collections/people/schema")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri(unknownProject + "/collections")), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri(unknownProject + "/collections/people/schema")), HttpStatusCode.NotFound, "not_found");
        // Collections are explicit: one whose documents were all deleted (tombstones) still exists, with nothing to infer.
        await Assert.That(emptiedCollection.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var emptied = await emptiedCollection.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(emptied.GetProperty("documents").GetInt64()).IsEqualTo(0);
        await Assert.That(emptied.GetProperty("sampledDocuments").GetInt32()).IsEqualTo(0);
        await Assert.That(emptied.GetProperty("fields").GetArrayLength()).IsEqualTo(0);
    }

    // Sends the exact JSON text, so stored bytes are known; returns it. Creates the collection first.
    private static async Task<string> PutAsync(HttpClient data, ManagedDatabaseRef database, string collection, string name, object document)
    {
        await ManagementApi.CreateCollectionAsync(data, database, collection);
        var json = JsonSerializer.Serialize(document, JsonSerializerOptions.Web);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        (await data.PutAsync(Uri(ManagementApi.DocumentPath(database, collection, name)), content)).EnsureSuccessStatusCode();
        return json;
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
