using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class UniqueConstraintTests
{
    [Test]
    public async Task Declared_JSON_schema_and_native_uniqueness_coexist_without_changing_collection_identity()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await DeclareAsync(api, data, database, new { fields = new[] { new { name = "email", unique = true } } });
        var collection = Uri(database.Data + "/collections/people");
        using var before = await data.GetAsync(collection);
        before.EnsureSuccessStatusCode();
        var identity = (await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var schema = Uri($"/v1/projects/{database.Project}/databases/{database.Name}/collections/people/schema");
        using var schemaBody = new StringContent("""{"type":"object","required":["email"],"properties":{"email":{"type":"string"}}}""", Encoding.UTF8, "application/json");
        using (var attached = await data.PutAsync(schema, schemaBody))
        {
            attached.EnsureSuccessStatusCode();
        }

        using (var invalid = await data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), new { email = 123 }))
        {
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That((await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo("schema_validation_failed");
        }

        var email = new Faker().Internet.Email();
        using (var created = await data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), new { email }))
        {
            created.EnsureSuccessStatusCode();
        }

        await DuplicateAsync(data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), new { email }));
        using (var detached = await data.DeleteAsync(schema))
        {
            detached.EnsureSuccessStatusCode();
        }

        await DuplicateAsync(data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), new { email }));
        using var after = await data.GetAsync(collection);
        after.EnsureSuccessStatusCode();
        await Assert.That((await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()).IsEqualTo(identity);
    }

    [Test]
    public async Task Single_and_compound_keys_normalize_and_release_atomically()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var management = await SchemaClientAsync(api, database);
        var schema = new
        {
            fields = new[] { new { name = "email", unique = true, normalization = "trim_lowercase" } },
            indexes = new[] { new { name = "tenant_handle", fields = new[] { "tenant", "profile.handle" }, unique = true, normalization = "lowercase" } },
        };
        using (var declared = await management.PutAsJsonAsync(Uri(database.Data + "/schema/people"), schema))
        {
            await Assert.That(declared.IsSuccessStatusCode).IsTrue().Because(await declared.Content.ReadAsStringAsync());
        }

        var email = new Faker().Internet.Email().ToLowerInvariant();
        var first = Ulid.NewUlid().ToString();
        var second = Ulid.NewUlid().ToString();
        using (var put = await data.PutAsJsonAsync(Document(database, first), new { email, tenant = "a", profile = new { handle = "name" } }))
        {
            put.EnsureSuccessStatusCode();
        }

        await DuplicateAsync(data.PutAsJsonAsync(Document(database, second), new { email = " " + email.ToUpperInvariant() + " ", tenant = "b" }));
        await DuplicateAsync(data.PutAsJsonAsync(Document(database, second), new { email = "other", tenant = "a", profile = new { handle = "NAME" } }));
        using (var updated = await data.PutAsJsonAsync(Document(database, first), new { email = "changed", tenant = "a", profile = new { handle = "next" } }))
        {
            updated.EnsureSuccessStatusCode();
        }

        using (var reused = await data.PutAsJsonAsync(Document(database, second), new { email, tenant = "a", profile = new { handle = "name" } }))
        {
            reused.EnsureSuccessStatusCode();
        }

        using var patch = new StringContent(JsonSerializer.Serialize(new { email }), Encoding.UTF8, "application/merge-patch+json");
        await DuplicateAsync(data.PatchAsync(Document(database, first), patch));
        using (var unchanged = await data.GetAsync(Document(database, first)))
        {
            unchanged.EnsureSuccessStatusCode();
            var document = await unchanged.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(document.GetProperty("document").GetProperty("email").GetString()).IsEqualTo("changed");
        }

        using (var transferred = await data.PostAsJsonAsync(Uri(database.Data + "/transactions"), new
        {
            ops = new object[]
            {
                new { op = "delete", collection = "people", id = second },
                new { op = "put", collection = "people", id = Ulid.NewUlid().ToString(), document = new { email, tenant = "a", profile = new { handle = "name" } } },
            },
        }))
        {
            transferred.EnsureSuccessStatusCode();
        }

        using var inferred = await management.GetAsync(Uri(database.Data + "/schema/people"));
        inferred.EnsureSuccessStatusCode();
        var body = await inferred.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("uniqueConstraints").GetArrayLength()).IsEqualTo(2);
        using var again = await management.PutAsJsonAsync(Uri(database.Data + "/schema/people"), schema);
        again.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task A_duplicate_rolls_back_every_operation_and_reports_its_index()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await DeclareAsync(api, data, database, new { fields = new[] { new { name = "email", unique = true } } });
        var first = Ulid.NewUlid().ToString();
        var second = Ulid.NewUlid().ToString();
        var email = new Faker().Internet.Email();
        using var transaction = await data.PostAsJsonAsync(Uri(database.Data + "/transactions"), new
        {
            ops = new[]
            {
                new { op = "put", collection = "people", id = first, document = new { email } },
                new { op = "put", collection = "people", id = second, document = new { email } },
            },
        });
        await Assert.That(transaction.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var error = await transaction.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unique_violation");
        await Assert.That(error.GetProperty("error").GetProperty("op").GetInt32()).IsEqualTo(1);
        using var missing = await data.GetAsync(Document(database, first));
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var later = await data.PutAsJsonAsync(Document(database, second), new { email });
        later.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Missing_and_null_values_are_sparse_and_typed_scalars_remain_distinct()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var schema = new { fields = new[] { new { name = "value", unique = true } } };
        await DeclareAsync(api, data, database, schema);
        foreach (var document in new[] { "{}", "{}", "{\"value\":null}", "{\"value\":null}", "{\"value\":true}", "{\"value\":1}", "{\"value\":\"1\"}" })
        {
            using var content = new StringContent(document, Encoding.UTF8, "application/json");
            using var put = await data.PutAsync(Document(database, Ulid.NewUlid().ToString()), content);
            put.EnsureSuccessStatusCode();
        }

        using var invalidContent = new StringContent("{\"value\":[1]}", Encoding.UTF8, "application/json");
        using var invalid = await data.PutAsync(Document(database, Ulid.NewUlid().ToString()), invalidContent);
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var management = await SchemaClientAsync(api, database);
        using var changed = await management.PutAsJsonAsync(Uri(database.Data + "/schema/people"), new { fields = Array.Empty<object>() });
        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    internal static async Task DeclareAsync(ManagementApi api, HttpClient data, ManagedDatabaseRef database, object schema)
    {
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var management = await SchemaClientAsync(api, database);
        using var declared = await management.PutAsJsonAsync(Uri(database.Data + "/schema/people"), schema);
        await Assert.That(declared.IsSuccessStatusCode).IsTrue().Because(await declared.Content.ReadAsStringAsync());
    }

    private static async Task<HttpClient> SchemaClientAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        return api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read databases:write";
        }));
    }

    private static Task DuplicateAsync(Task<HttpResponseMessage> response) =>
        ErrorAssert.ErrorAsync(response, HttpStatusCode.Conflict, "unique_violation", "a unique field or compound key is already taken");

    internal static Uri Document(ManagedDatabaseRef database, string id) => Uri(database.Data + "/collections/people/documents/by-key?id=" + System.Uri.EscapeDataString(id));

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
