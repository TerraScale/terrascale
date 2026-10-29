using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Tenancy;
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
    [Arguments("lowercase")]
    [Arguments("trim_lowercase")]
    public async Task Lowercase_normalization_handles_non_ASCII_without_promising_full_case_folding(string normalization)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await DeclareAsync(api, data, database, new { fields = new[] { new { name = "email", unique = true, normalization } } });
        using (var first = await data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), new { email = "ÅNGSTRÖM" }))
        {
            first.EnsureSuccessStatusCode();
        }

        await DuplicateAsync(data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()),
            new { email = normalization == "trim_lowercase" ? "\u2003ångström\u2003" : "ångström" }));
        foreach (var email in new[] { "Straße", "STRASSE" })
        {
            using var distinct = await data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), new { email });
            distinct.EnsureSuccessStatusCode();
        }

        using var management = await SchemaClientAsync(api, database);
        using var misnamed = await management.PutAsJsonAsync(Uri(database.Data + "/schema/people"),
            new { fields = new[] { new { name = "email", unique = true, normalization = "case_fold" } } });
        await Assert.That(misnamed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
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
    public async Task Parallel_writers_cannot_claim_the_same_value_in_a_group_commit()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await DeclareAsync(api, data, database, new { fields = new[] { new { name = "email", unique = true } } });
        var email = new Faker().Internet.Email();
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), new { email })));
        try
        {
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Created)).IsEqualTo(1);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Conflict)).IsEqualTo(15);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Test]
    public async Task Failed_unique_admission_does_not_break_later_writes_in_the_same_group_commit()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await DeclareAsync(api, data, database, new { fields = new[] { new { name = "email", unique = true } } });
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var store = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
        var first = Ulid.NewUlid().ToString();
        var last = Ulid.NewUlid().ToString();
        var invalid = Ulid.NewUlid().ToString();
        var email = new Faker().Internet.Email();
        var held = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        Task<HttpResponseMessage> primer;
        Task<HttpResponseMessage>[] sending;
        try
        {
            // The primer occupies the waiting batch; the next batch starts with rolled-back table creation.
            primer = data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), new { });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.BatchedWrites == 1));
            var failing = data.PutAsJsonAsync(Document(database, invalid), new { email = Array.Empty<string>() });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites == 1));
            var accepted = data.PutAsJsonAsync(Document(database, first), new { email });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites == 2));
            var duplicate = data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), new { email });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites == 3));
            var later = data.PutAsJsonAsync(Document(database, last), new { email = "other" });
            sending = [failing, accepted, duplicate, later];
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites == sending.Length));
        }
        finally
        {
            await held.DisposeAsync();
        }

        using var primed = await primer;
        primed.EnsureSuccessStatusCode();
        var answers = await Task.WhenAll(sending);
        try
        {
            await Assert.That(answers.Select(answer => answer.StatusCode)).IsEquivalentTo(
                [HttpStatusCode.BadRequest, HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.Created]);
            var firstBody = await answers[1].Content.ReadFromJsonAsync<JsonElement>();
            var lastBody = await answers[3].Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(firstBody.GetProperty("offset").GetString()).IsEqualTo(lastBody.GetProperty("offset").GetString());
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }

        foreach (var id in new[] { first, last, invalid })
        {
            using var read = await data.GetAsync(Document(database, id));
            await Assert.That(read.StatusCode).IsEqualTo(id == invalid ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task Engine_refusal_is_rollback_only_and_does_not_leave_keys_behind()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await DeclareAsync(api, data, database, new { fields = new[] { new { name = "number", unique = true } } });
        var first = Ulid.NewUlid().ToString();
        var second = Ulid.NewUlid().ToString();
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await transaction.PutAsync("people", first, Encoding.UTF8.GetBytes("{\"number\":1.00}"), CancellationToken.None);
            await Assert.That(async () => await transaction.PutAsync("people", second, Encoding.UTF8.GetBytes("{\"number\":1e0}"), CancellationToken.None))
                .Throws<UniqueConstraintException>();
            await Assert.That(async () => await transaction.CommitAsync(CancellationToken.None)).Throws<InvalidOperationException>();
        }

        using var absent = await data.GetAsync(Document(database, first));
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var accepted = await data.PutAsJsonAsync(Document(database, second), new { number = 1 });
        accepted.EnsureSuccessStatusCode();
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Sparse_compound_keys_skip_invalid_components_regardless_of_field_order(bool reverse)
    {
        var fields = new[] { "a", "b" };
        if (reverse)
        {
            Array.Reverse(fields);
        }

        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await DeclareAsync(api, data, database, new
        {
            indexes = new[] { new { name = "compound", fields, unique = true } },
        });

        object[] invalidValues = [Array.Empty<string>(), new { nested = "object" }, new string('x', 17 * 1024)];
        foreach (var invalid in invalidValues)
        {
            foreach (var invalidField in new[] { "a", "b" })
            {
                var sparseField = invalidField == "a" ? "b" : "a";
                foreach (var nullComponent in new[] { false, true })
                {
                    var document = new Dictionary<string, object?> { [invalidField] = invalid };
                    if (nullComponent)
                    {
                        document[sparseField] = null;
                    }

                    for (var copy = 0; copy < 2; copy++)
                    {
                        using var accepted = await data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), document);
                        accepted.EnsureSuccessStatusCode();
                    }
                }

                var complete = new Dictionary<string, object?> { [invalidField] = invalid, [sparseField] = "present" };
                using var rejected = await data.PutAsJsonAsync(Document(database, Ulid.NewUlid().ToString()), complete);
                await Assert.That(rejected.StatusCode).IsEqualTo(invalid is string ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest);
            }
        }
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
