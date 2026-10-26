using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseRenameJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments(400, "invalid_name")]
    [Arguments(409, "name_taken")]
    [Arguments(503, "catalog_anchor_unavailable")]
    public async Task SettingsRetainsTheDraftAndShowsProducerErrors(int status, string code)
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http => Task.FromResult<IResult?>(
            http.Request.Method == "PATCH" ? Results.Json(new { error = new { code, message = "Rename refused by the catalog", fix = "Check the database name" } }, statusCode: status) : null));
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "rename-source", ["region"] = "GRU" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var id = (await BodyAsync(created)).GetProperty("id").GetString()!;
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var refused = await RenameAsync(client, csrf, id, "rename-draft", "rename-source");
        await Assert.That((int)refused.StatusCode).IsEqualTo(status);
        var page = await refused.Content.ReadAsStringAsync();
        await Assert.That(page).Contains("role=\"alert\"");
        await Assert.That(page).Contains("value=\"rename-draft\"");
        await Assert.That(page).Contains(status == 503 ? "Reload Settings to check the current name" : "Rename refused by the catalog");
    }

    [Test]
    public async Task ConflictRenamedDatabaseShowsItsPreservedIdentityAndClearsTheBannerWhenResolved()
    {
        var mode = 0;
        var id = "";
        var projectId = "";
        var currentName = "";
        var createdAt = DateTimeOffset.UnixEpoch;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            var observed = Volatile.Read(ref mode);
            if (observed == 0 || http.Request.Method != "GET" || http.Request.Path != "/v1/projects/" + projectId + "/databases")
                return Task.FromResult<IResult?>(null);
            http.Response.Headers["X-TerraBase-Region"] = "gru";
            return Task.FromResult<IResult?>(Results.Json(new
            {
                items = new[] { new
            {
                id, projectId, name = currentName, createdAt, branchedFrom = (string?)null,
                region = "gru", homeRegion = "gru", replicaRegions = new[] { "sfo" }, allRegions = false,
                previousName = observed == 1 ? "shared" : null,
                conflictOf = observed == 1 ? "01JA0000000000000000000003" : null
            } },
                next = (string?)null
            }));
        });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        projectId = (await ReadAsync(client, "/api/v1/auth/me")).GetProperty("projectId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases",
            new() { ["_csrf"] = csrf, ["name"] = "conflict-source", ["region"] = "GRU" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var body = await BodyAsync(created);
            id = body.GetProperty("id").GetString()!;
            createdAt = body.GetProperty("createdAt").GetDateTimeOffset();
        }
        var path = "/databases/" + id + "/settings";
        await Assert.That(await client.GetStringAsync(path)).DoesNotContain("id=\"database-name-conflict\"");
        currentName = "shared-conflict-" + id[^6..].ToLowerInvariant();
        Volatile.Write(ref mode, 1);
        var page = await client.GetStringAsync(path);
        await Assert.That(page).Contains("id=\"database-name-conflict\"");
        await Assert.That(page).Contains("Renamed after a name conflict");
        await Assert.That(page).Contains("<strong>shared</strong>");
        await Assert.That(page).Contains(currentName);
        await Assert.That(page).Contains("Its ID, data, files, backups and keys are preserved");
        await Assert.That(page).Contains("name=\"databaseId\" value=\"" + id + "\"");
        // Clearing conflict metadata must refresh even if the current label is unchanged.
        Volatile.Write(ref mode, 2);
        await Assert.That(await client.GetStringAsync(path)).DoesNotContain("id=\"database-name-conflict\"");
    }

    [Test]
    public async Task SettingsRenameUsesImmutableIdentityAndKeepsDataPlacementAndKeyRestriction()
    {
        var patches = 0;
        string bearer = "", readBearer = "", projectId = "", organizationId = "", databaseId = "";
        TestAuth auth = null!;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), async http =>
        {
            if (http.Request.Method == "GET" && http.Request.Path == "/v1/projects/" + projectId + "/databases") readBearer = http.Request.Headers.Authorization.ToString();
            if (http.Request.Method != "PATCH") return null;
            DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organizationId, projectId, "databases:write");
            await Assert.That(http.Request.Path.Value).IsEqualTo("/v1/projects/" + projectId + "/databases/" + databaseId);
            http.Request.EnableBuffering();
            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            await Assert.That(body.RootElement.EnumerateObject().Select(field => field.Name)).IsEquivalentTo(new[] { "name" });
            http.Request.Body.Position = 0;
            bearer = http.Request.Headers.Authorization.ToString();
            Interlocked.Increment(ref patches);
            return null;
        });
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var identity = await ReadAsync(client, "/api/v1/auth/me");
        projectId = identity.GetProperty("projectId").GetString()!;
        organizationId = identity.GetProperty("organizationId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "rename-source", ["region"] = "GRU" }))
        { await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created); databaseId = (await BodyAsync(created)).GetProperty("id").GetString()!; }
        var before = (await ReadAsync(client, "/api/v1/dashboard/databases"))[0];
        using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["name"] = "users" }))
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using (var document = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
        { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["collection"] = "users", ["id"] = "01K78D0C000000000000000009", ["operation"] = "create", ["document"] = "{\"value\":\"preserved\"}" }))
            await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string keyId;
        using (var key = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/key", new()
        { ["_csrf"] = csrf, ["name"] = "Scoped rename key", ["databaseId"] = databaseId, ["scopes"] = "database:read,database:write" }))
        { await Assert.That(key.StatusCode).IsEqualTo(HttpStatusCode.Created); keyId = (await BodyAsync(key)).GetProperty("apiKey").GetProperty("keyId").GetString()!; }
        var path = "/databases/" + databaseId + "/settings";
        var page = await client.GetStringAsync(path);
        await Assert.That(page).Contains("Update name-based connection strings");
        await Assert.That(page).Contains("Conflicting requests accepted in different regions can change the final name");
        await Assert.That(page).Contains("There is no alias or redirect");
        foreach (var name in new[] { "1name", "con", "prn", "aux", "nul", "valid\n", " valid", "valid ", "", new string('a', 64) }
            .Concat(Enumerable.Range(0, 10).SelectMany(number => new[] { "com" + number, "lpt" + number })))
        {
            using var refused = await RenameAsync(client, csrf, databaseId, name, "rename-source");
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        }
        foreach (var (name, confirmation) in new[] { ("INVALID", "rename-source"), ("rename_new-", "wrong"), ("rename-source", "rename-source"), ("rename_new-", "rename-source\n"), ("rename_new-", " rename-source"), ("rename_new-", "RENAME-SOURCE") })
        {
            using var refused = await RenameAsync(client, csrf, databaseId, name, confirmation);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        }
        using (var noCsrf = await RenameAsync(client, "", databaseId, "rename_new-", "rename-source"))
            await Assert.That(noCsrf.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using (var foreign = await RenameAsync(client, csrf, "01K78000000000000000000000", "rename_new-", "rename-source"))
            await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(patches).IsEqualTo(0);
        using (var duplicate = await producer.PostAsJsonAsync("/__test/control", new { command = "external-create", projectId, value = "taken" }))
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await ReadAsync(client, "/api/v1/dashboard/databases");
        using (var duplicate = await RenameAsync(client, csrf, databaseId, "taken", "rename-source"))
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        using (var role = await producer.PostAsJsonAsync("/__test/control", new { command = "role", organizationId, value = "member" }))
            await Assert.That(role.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using (var forbidden = await RenameAsync(client, csrf, databaseId, "rename_new-", "rename-source"))
            await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using (var role = await producer.PostAsJsonAsync("/__test/control", new { command = "role", organizationId, value = "owner" }))
            await Assert.That(role.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(patches).IsEqualTo(0);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using (var invalid = await RenameAsync(client, csrf, databaseId, "INVALID", "rename-source"))
        { await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity); await Assert.That(await invalid.Content.ReadAsStringAsync()).Contains("value=\"INVALID\""); }
        using (var renamed = await RenameAsync(client, csrf, databaseId, "rename_new-", "rename-source"))
        { await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await renamed.Content.ReadAsStringAsync()); await Assert.That(renamed.Headers.Location?.ToString()).IsEqualTo(path + "?renamed=1"); }
        client.DefaultRequestHeaders.Accept.Clear();
        await Assert.That(patches).IsEqualTo(1);
        var after = (await ReadAsync(client, "/api/v1/dashboard/databases")).EnumerateArray().Single(row => row.GetProperty("id").GetString() == databaseId);
        foreach (var property in new[] { "id", "createdAt", "regions", "region", "allRegions", "branchedFrom" })
            await Assert.That(after.GetProperty(property).GetRawText()).IsEqualTo(before.GetProperty(property).GetRawText());
        await Assert.That(after.GetProperty("name").GetString()).IsEqualTo("rename_new-");
        await Assert.That(await client.GetStringAsync("/databases/" + databaseId)).Contains("/databases/rename_new-/");
        var preserved = (await ReadAsync(client, "/api/v1/dashboard/data/documents?databaseId=" + databaseId + "&collection=users")).GetProperty("documents").EnumerateArray().Single();
        await Assert.That(preserved.GetProperty("id").GetString()).IsEqualTo("01K78D0C000000000000000009");
        await Assert.That(preserved.GetProperty("value").GetProperty("value").GetString()).IsEqualTo("preserved");
        using (var rotated = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/rotate", new() { ["_csrf"] = csrf, ["keyId"] = keyId }))
        {
            await Assert.That(rotated.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var key = (await BodyAsync(rotated)).GetProperty("apiKey");
            await Assert.That(key.GetProperty("databaseId").GetString()).IsEqualTo(databaseId);
            await Assert.That(key.GetProperty("databaseName").GetString()).IsEqualTo("rename_new-");
        }
        producer.DefaultRequestHeaders.Add("Authorization", readBearer);
        producer.DefaultRequestHeaders.Host = "gru.api.terrascale.tech";
        using (var old = await producer.GetAsync("/v1/projects/" + projectId + "/databases/rename-source"))
            await Assert.That(old.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        // A rename by another client refreshes the existing dashboard record, without changing its URL.
        producer.DefaultRequestHeaders.Remove("Authorization");
        producer.DefaultRequestHeaders.Add("Authorization", bearer);
        using (var external = await producer.PatchAsJsonAsync("/v1/projects/" + projectId + "/databases/" + databaseId, new { name = "external-name" }))
            await Assert.That(external.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await client.GetStringAsync(path)).Contains("external-name");
        var observed = await ReadAsync(producer, "/__test/observations");
        await Assert.That(observed.GetProperty("credentials").EnumerateArray().Single().GetProperty("database").GetString()).IsEqualTo(databaseId);
        await Assert.That(observed.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
    }

    private static Task<HttpResponseMessage> RenameAsync(HttpClient client, string csrf, string id, string name, string confirmation) =>
        HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/rename", new() { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = name, ["confirmName"] = confirmation });
    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK); return await BodyAsync(response); }
    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    { using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return body.RootElement.Clone(); }
}
