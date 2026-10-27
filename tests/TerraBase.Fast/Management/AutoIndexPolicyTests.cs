using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Indexes;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class AutoIndexPolicyTests
{
    private static readonly string[] Email = ["email"];
    private static readonly string[] Name = ["name"];

    [Test]
    public async Task Collections_default_to_enabled_and_can_toggle_before_they_exist()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var collection = "c_" + new Faker().Random.String2(8, "abcdefghijklmnopqrstuvwxyz");

        using var client = PolicyClient(api, database);
        var former = Uri($"/projects/{database.Project}/databases/{database.Name}/auto-index/{collection}");
        using var formerGet = await client.GetAsync(former);
        using var formerPut = await client.PutAsJsonAsync(former, new { enabled = false });
        await Assert.That(formerGet.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(formerPut.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        await GetPolicyAsync(api, database, collection, true);
        await SetPolicyAsync(api, database, collection, false);
        await GetPolicyAsync(api, database, collection, false);
        await SetPolicyAsync(api, database, collection, false);
        await SetPolicyAsync(api, database, collection, true);
        await GetPolicyAsync(api, database, collection, true);
        await SetPolicyAsync(api, database, collection, true);
    }

    [Test]
    public async Task An_unreadable_catalog_refuses_policy_reads_and_writes_without_losing_saved_opt_outs()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        await SetPolicyAsync(api, database, "people", false);
        using var client = PolicyClient(api, database);
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var indexes = api.Server.Services.GetRequiredService<IIndexService>();
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var records = $"databases/{database.Id}/auto_index_policies";
        byte[] version;
        byte[] policy;
        await using (var transaction = await store.BeginTransactionAsync(write: false, token))
        {
            version = (await transaction.GetAsync("catalog", "version", token))!.Json.ToArray();
            policy = (await transaction.GetAsync(records, "people", token))!.Json.ToArray();
        }

        await WriteVersionAsync("\"99\""u8.ToArray());
        await Assert.That(await indexes.GetAutoIndexPolicyAsync(api.User, tenant, "people", token) is Unavailable).IsTrue();
        await ErrorAssert.ErrorAsync(client.GetAsync(PolicyUri(database, "people"), token), HttpStatusCode.ServiceUnavailable,
            "unavailable", "the management catalog has an old or unsupported format; discard the affected data");
        foreach (var enabled in new[] { false, true })
        {
            await Assert.That(await indexes.SetAutoIndexPolicyAsync(api.User, tenant, "people", enabled, token) is Unavailable).IsTrue();
            await ErrorAssert.ErrorAsync(client.PutAsJsonAsync(PolicyUri(database, "people"), new { enabled }, token),
                HttpStatusCode.ServiceUnavailable, "unavailable", "the management catalog has an old or unsupported format; discard the affected data");
        }

        await using (var transaction = await store.BeginTransactionAsync(write: false, token))
        {
            var saved = await transaction.GetAsync(records, "people", token);
            await Assert.That(saved!.Json.Span.SequenceEqual(policy)).IsTrue();
            await Assert.That(await transaction.CountDocumentsAsync(records, token)).IsEqualTo(1);
        }

        await WriteVersionAsync(version);
        await GetPolicyAsync(api, database, "people", false);
        await SetPolicyAsync(api, database, "people", true);
        await GetPolicyAsync(api, database, "people", true);
        await Assert.That(await CountOptOutsAsync(api, database)).IsEqualTo(0);

        async Task WriteVersionAsync(byte[] value)
        {
            await using var transaction = await store.BeginTransactionAsync(write: true, token);
            await transaction.PutAsync("catalog", "version", value, token);
            await transaction.CommitAsync(token);
        }
    }

    [Test]
    public async Task Policies_are_isolated_by_collection_database_and_organization()
    {
        await using var api = await ManagementApi.StartAsync();
        var first = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(first.Organization, first.Project);
        var neighbor = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "neighbor");
        var otherOrganization = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(otherOrganization.Organization, otherOrganization.Project);

        await SetPolicyAsync(api, first, "people", false);
        await GetPolicyAsync(api, first, "orders", true);
        await GetPolicyAsync(api, neighbor, "people", true);
        await GetPolicyAsync(api, otherOrganization, "people", true);
        await SetPolicyAsync(api, neighbor, "people", false);
        await SetPolicyAsync(api, first, "people", true);
        await GetPolicyAsync(api, first, "people", true);
        await GetPolicyAsync(api, neighbor, "people", false);
        await GetPolicyAsync(api, otherOrganization, "people", true);
    }

    [Test]
    public async Task Opt_outs_and_reenabling_survive_restarts()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-auto-index-restart-").FullName;
        try
        {
            string user;
            ManagedDatabaseRef database;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                user = api.User;
                database = await api.CreateManagedDatabaseAsync();
                await api.VerifyProjectBindingAsync(database.Organization, database.Project);
                await SetPolicyAsync(api, database, "people", false);
                await SetPolicyAsync(api, database, "orders", false);
                await SetPolicyAsync(api, database, "orders", true);
            }

            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                await GetPolicyAsync(api, database, "people", false, user);
                await GetPolicyAsync(api, database, "orders", true, user);
                await GetPolicyAsync(api, database, "unconfigured", true, user);
                await SetPolicyAsync(api, database, "people", true, user);
            }

            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                await GetPolicyAsync(api, database, "people", true, user);
                await GetPolicyAsync(api, database, "orders", true, user);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Project_tokens_require_policy_scopes_and_respect_project_and_database_restrictions()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        await SetPolicyAsync(api, database, "people", false);
        var otherProject = await api.CreateManagedDatabaseAsync();
        using var readOnly = PolicyClient(api, database, scope: "databases:read");
        using var restricted = PolicyClient(api, database, restrictedDatabase: database.Id);
        using var mismatched = PolicyClient(api, otherProject);
        using var anonymous = api.WithToken(null);
        var neighbor = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "neighbor");
        var path = PolicyUri(database, "people");
        var neighborPath = PolicyUri(neighbor, "people");

        await AssertPolicyAsync(readOnly.GetAsync(path), "people", false);
        await AssertPolicyAsync(readOnly.GetAsync(PolicyUri(database, "unconfigured")), "unconfigured", true);
        await AssertPolicyAsync(restricted.GetAsync(path), "people", false);
        await AssertPolicyAsync(restricted.PutAsJsonAsync(path, new { enabled = false }), "people", false);
        foreach (var enabled in new[] { false, true })
        {
            await ErrorAssert.ErrorAsync(readOnly.PutAsJsonAsync(path, new { enabled }), HttpStatusCode.Forbidden, "forbidden_scope");
            await ErrorAssert.ErrorAsync(
                restricted.PutAsJsonAsync(neighborPath, new { enabled }), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
            await ErrorAssert.ErrorAsync(
                mismatched.PutAsJsonAsync(path, new { enabled }), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
            await ErrorAssert.ErrorAsync(anonymous.PutAsJsonAsync(path, new { enabled }), HttpStatusCode.Unauthorized, "invalid_key");
        }

        await ErrorAssert.ErrorAsync(anonymous.GetAsync(path), HttpStatusCode.Unauthorized, "invalid_key");
        await ErrorAssert.ErrorAsync(
            restricted.GetAsync(neighborPath), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        await ErrorAssert.ErrorAsync(
            mismatched.GetAsync(path), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        await GetPolicyAsync(api, neighbor, "people", true);
        await GetPolicyAsync(api, database, "people", false);
    }

    [Test]
    public async Task Unknown_database_parents_return_not_found_without_changing_existing_policies()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        await SetPolicyAsync(api, database, "people", false);
        using var client = PolicyClient(api, database);

        ManagedDatabaseRef[] unknown =
        [
            database with { Project = Ulid.NewUlid().ToString() },
            database with { Name = "missing" },
        ];
        foreach (var missing in unknown)
        {
            var policy = PolicyUri(missing, "people");
            await ErrorAssert.ErrorAsync(client.GetAsync(policy), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
            await ErrorAssert.ErrorAsync(
                client.PutAsJsonAsync(policy, new { enabled = false }), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
            await ErrorAssert.ErrorAsync(
                client.PutAsJsonAsync(policy, new { enabled = true }), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        }

        await GetPolicyAsync(api, database, "people", false);
    }

    [Test]
    public async Task Collection_names_are_validated_and_enabled_is_a_required_boolean()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = PolicyClient(api, database);
        await SetPolicyAsync(api, database, "people", false);
        string[] invalidCollections = ["People", "bad.name", "1people", "con", new string('c', 64)];
        foreach (var collection in invalidCollections)
        {
            var path = PolicyUri(database, collection);
            await ErrorAssert.ErrorAsync(
                client.GetAsync(path), HttpStatusCode.BadRequest, "invalid_request",
                "collection must be a lowercase name of at most 63 characters");
            await ErrorAssert.ErrorAsync(
                client.PutAsJsonAsync(path, new { enabled = false }), HttpStatusCode.BadRequest, "invalid_request",
                "collection must be a lowercase name of at most 63 characters");
        }

        var validPath = PolicyUri(database, "people");
        foreach (var body in new[] { "{}", "{\"enabled\":null}" })
        {
            using var content = new StringContent(body, null, "application/json");
            await ErrorAssert.ErrorAsync(
                client.PutAsync(validPath, content), HttpStatusCode.BadRequest, "invalid_request", "enabled must be a boolean");
        }

        string[] invalidBodies = ["{\"enabled\":\"false\"}", "{\"enabled\":0}", "{\"enabled\":[]}", "{\"enabled\":{}}", "{bad"];
        foreach (var body in invalidBodies)
        {
            using var content = new StringContent(body, null, "application/json");
            using var response = await client.PutAsync(validPath, content);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(body);
        }

        await GetPolicyAsync(api, database, "people", false);
        await SetPolicyAsync(api, database, "c" + new string('x', 62), false);
        await SetPolicyAsync(api, database, "valid-name", false);
    }

    [Test]
    public async Task The_opt_out_limit_is_per_database_and_allows_repeats_and_reenabling()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = PolicyClient(api, database);
        var neighbor = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "neighbor");
        // Default-enabled policies must not consume the disabled-record budget.
        await SetPolicyAsync(api, database, "unconfigured", true);
        // Seed below the cap in one transaction; exercise the boundary through the real HTTP endpoint.
        var token = TestContext.Current!.Execution.CancellationToken;
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using (var transaction = await store.BeginTransactionAsync(write: true, token))
        {
            for (var i = 0; i < 999; i++)
            {
                var collection = $"c{i:D4}";
                await transaction.PutAsync($"databases/{database.Id}/auto_index_policies", collection,
                    JsonSerializer.SerializeToUtf8Bytes(new AutoIndexPolicy(collection, false), TerraBaseJsonContext.Default.AutoIndexPolicy), token);
            }

            await transaction.CommitAsync(token);
        }

        await AssertPolicyAsync(client.PutAsJsonAsync(PolicyUri(database, "c0999"), new { enabled = false }), "c0999", false);
        await GetPolicyAsync(api, database, "c0999", false);
        var extra = PolicyUri(database, "extra");
        await ErrorAssert.ErrorAsync(
            client.PutAsJsonAsync(extra, new { enabled = false }), HttpStatusCode.BadRequest, "auto_index_policy_limit",
            "a database may opt out at most 1000 collections");
        await GetPolicyAsync(api, database, "extra", true);
        await SetPolicyAsync(api, database, "c0000", false);
        await GetPolicyAsync(api, database, "c0000", false);
        await SetPolicyAsync(api, database, "extra", true);
        await ErrorAssert.ErrorAsync(
            client.PutAsJsonAsync(extra, new { enabled = false }), HttpStatusCode.BadRequest, "auto_index_policy_limit",
            "a database may opt out at most 1000 collections");
        await SetPolicyAsync(api, neighbor, "extra", false);
        await SetPolicyAsync(api, database, "c0000", true);
        await SetPolicyAsync(api, database, "extra", false);
        await GetPolicyAsync(api, database, "c0000", true);
        await GetPolicyAsync(api, database, "extra", false);
        await ErrorAssert.ErrorAsync(
            client.PutAsJsonAsync(PolicyUri(database, "c0000"), new { enabled = false }),
            HttpStatusCode.BadRequest, "auto_index_policy_limit", "a database may opt out at most 1000 collections");
    }

    [Test]
    public async Task Removing_a_database_removes_its_policy_without_affecting_its_neighbor()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = PolicyClient(api, database);
        var neighbor = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "neighbor");
        await SetPolicyAsync(api, database, "people", false);
        await SetPolicyAsync(api, neighbor, "people", false);
        await Assert.That(await CountOptOutsAsync(api, database)).IsEqualTo(1);

        using var removed = await api.Client.DeleteAsync(Uri(database.Path));
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await CountOptOutsAsync(api, database)).IsEqualTo(0);
        await Assert.That(await CountOptOutsAsync(api, neighbor)).IsEqualTo(1);
        await ErrorAssert.ErrorAsync(client.GetAsync(PolicyUri(database, "people")), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        await ErrorAssert.ErrorAsync(
            client.PutAsJsonAsync(PolicyUri(database, "people"), new { enabled = false }), HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        var replacement = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, database.Name);
        await Assert.That(replacement.Id).IsNotEqualTo(database.Id);
        await GetPolicyAsync(api, replacement, "people", true);
        await GetPolicyAsync(api, neighbor, "people", false);
    }

    [Test]
    public async Task Disabled_policies_preserve_existing_indexes_and_allow_manual_index_operations()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var indexes = Uri($"{database.Path}/indexes");
        using var existing = await api.Client.PostAsJsonAsync(indexes, new { collection = "people", fields = Email });
        await Assert.That(existing.StatusCode).IsEqualTo(HttpStatusCode.Created);

        await SetPolicyAsync(api, database, "people", false);
        var preserved = await api.Client.GetFromJsonAsync<JsonElement>(indexes);
        await Assert.That(preserved.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!))
            .IsEquivalentTo(["people_email"]);
        using var created = await api.Client.PostAsJsonAsync(indexes, new { collection = "people", fields = Name });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var query = await api.Client.PostAsJsonAsync(Uri($"{database.Path}/sql"), new { sql = "SELECT * FROM people WHERE city = 'sample'" });
        query.EnsureSuccessStatusCode();
        using var applied = await api.Client.PostAsync(Uri($"{database.Path}/index-recommendations/people:city/apply"), null);
        await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var dropped = await api.Client.DeleteAsync(Uri($"{database.Path}/indexes/people_email"));
        await Assert.That(dropped.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var remaining = await api.Client.GetFromJsonAsync<JsonElement>(indexes);
        await Assert.That(remaining.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!))
            .IsEquivalentTo(["people_city", "people_name"]);
        await GetPolicyAsync(api, database, "people", false);

        using var removedCollection = await data.DeleteAsync(Uri($"{database.Data}/collections/people"));
        await Assert.That(removedCollection.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await GetPolicyAsync(api, database, "people", false);
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        await GetPolicyAsync(api, database, "people", false);
    }

    private static async Task GetPolicyAsync(ManagementApi api, ManagedDatabaseRef database, string collection, bool enabled, string? user = null)
    {
        using var client = PolicyClient(api, database, user, scope: "databases:read");
        await AssertPolicyAsync(client.GetAsync(PolicyUri(database, collection)), collection, enabled);
    }

    private static async Task SetPolicyAsync(ManagementApi api, ManagedDatabaseRef database, string collection, bool enabled, string? user = null)
    {
        using var client = PolicyClient(api, database, user, scope: "databases:write");
        await AssertPolicyAsync(client.PutAsJsonAsync(PolicyUri(database, collection), new { enabled }), collection, enabled);
    }

    private static HttpClient PolicyClient(
        ManagementApi api, ManagedDatabaseRef database, string? user = null,
        string scope = "databases:read databases:write", string? restrictedDatabase = null) =>
        api.WithToken(api.Issuer.Mint(user ?? api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = scope;
            if (restrictedDatabase is not null)
            {
                token.Claims["database"] = restrictedDatabase;
            }
        }));

    private static async Task AssertPolicyAsync(Task<HttpResponseMessage> sending, string collection, bool enabled)
    {
        using var response = await sending;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var policy = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(policy.GetProperty("collection").GetString()).IsEqualTo(collection);
        await Assert.That(policy.GetProperty("enabled").GetBoolean()).IsEqualTo(enabled);
    }

    private static Uri PolicyUri(ManagedDatabaseRef database, string collection) =>
        Uri($"/v1/projects/{database.Project}/databases/{database.Name}/auto-index/{collection}");

    private static async Task<long> CountOptOutsAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var transaction = await store.BeginTransactionAsync(write: false, token);
        return await transaction.CountDocumentsAsync($"databases/{database.Id}/auto_index_policies", token);
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
