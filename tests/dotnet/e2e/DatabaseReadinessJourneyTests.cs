using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
public sealed class DatabaseReadinessJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task InterruptedCreationRecoversOnlyItsActualHomeAndPreservesProducerReplicas()
    {
        var rejectCreate = false;
        var organizationId = "";
        var projectId = "";
        var refusedCreates = 0;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (!Volatile.Read(ref rejectCreate) || http.Request.Method != "POST" ||
                http.Request.Path != "/v1/management/organizations/" + organizationId + "/projects/" + projectId + "/databases")
                return Task.FromResult<IResult?>(null);
            DemandScope(http, organizationId);
            Interlocked.Increment(ref refusedCreates);
            return Task.FromResult<IResult?>(Results.Json(new { error = new { code = "unavailable", message = "Scoped test-only producer outage", fix = "retry" } }, statusCode: 503));
        });
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var producer = scenario.CreateProviderClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var identity = await JsonAsync(client, "/api/v1/auth/me");
        organizationId = identity.GetProperty("organizationId").GetString()!;
        projectId = identity.GetProperty("projectId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/databases");
        const string name = "interrupted-home";
        Volatile.Write(ref rejectCreate, true);
        using (var unavailable = await PostAsync(client, "/api/v1/dashboard/databases", csrf, new() { ["name"] = name }, ["GRU"]))
        {
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(unavailable, producer));
            await Assert.That(unavailable.Headers.Location is null).IsTrue();
        }
        await Assert.That(refusedCreates).IsEqualTo(1);
        await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("creates").GetInt32()).IsEqualTo(0);
        await Assert.That((await JsonAsync(replica, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
        Volatile.Write(ref rejectCreate, false);
        await ControlAsync(producer, new { command = "fail-database-metadata", organizationId, projectId, name });
        await ControlAsync(producer, new { command = "pause-database-creates" });
        var pending = PostAsync(client, "/api/v1/dashboard/databases", csrf, new() { ["name"] = name }, ["GRU", "SFO"]);
        try
        {
            await WaitAsync(producer, "pausedCreates");
            await Assert.That((await JsonAsync(replica, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
        }
        finally { await ControlAsync(producer, new { command = "release-database-creates" }); }
        using (var interrupted = await pending)
            await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(interrupted, producer));
        var observed = await JsonAsync(producer, "/__test/observations");
        await Assert.That(observed.GetProperty("creates").GetInt32()).IsEqualTo(1);
        await Assert.That(observed.GetProperty("failedDatabaseMetadataWrites").GetInt32()).IsEqualTo(1);
        var actualId = observed.GetProperty("databases")[0].GetProperty("id").GetString()!;
        await Assert.That((await JsonAsync(replica, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
        using (var wrongHome = await PostAsync(replica, "/api/v1/dashboard/databases", replicaCsrf, new() { ["name"] = name }, ["SFO"]))
            await Assert.That(wrongHome.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await EvidenceAsync(wrongHome, producer));
        await Assert.That((await JsonAsync(replica, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
        // The retry omits SFO. Recovery must retain the producer's actual SFO
        // replica rather than claim this new request changed placement.
        using (var recovered = await PostAsync(replica, "/api/v1/dashboard/databases", replicaCsrf, new() { ["name"] = name }, ["GRU"]))
        {
            await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await EvidenceAsync(recovered, producer));
            await Assert.That(recovered.Headers.Location?.OriginalString).IsEqualTo("/databases/" + actualId + "?created=1");
        }
        var rows = await JsonAsync(client, "/api/v1/dashboard/databases");
        await Assert.That(rows.GetArrayLength()).IsEqualTo(1);
        await Assert.That(rows[0].GetProperty("id").GetString()).IsEqualTo(actualId);
        await Assert.That(rows[0].GetProperty("status").GetString()).IsEqualTo("ready");
        await Assert.That(rows[0].GetProperty("region").GetString()).IsEqualTo("GRU");
        await Assert.That(Regions(rows[0])).IsEqualTo("GRU,SFO");
        var page = await PageAsync(client, "/databases/" + actualId + "?created=1");
        await Assert.That(page).Contains(name + " is ready</p>");
        await Assert.That(page).Contains("Its home region is ready. Replica progress is shown below.");
        await Assert.That(page).Contains("Not measured");
        using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
        { ["_csrf"] = csrf, ["databaseId"] = actualId, ["name"] = "users" }))
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using (var document = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
        { ["_csrf"] = csrf, ["databaseId"] = actualId, ["collection"] = "users", ["id"] = "home-ready", ["operation"] = "create", ["document"] = "{\"ready\":true}" }))
            await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await EvidenceAsync(document, producer));
        var documents = await JsonAsync(replica, "/api/v1/dashboard/data/documents?databaseId=" + actualId + "&collection=users");
        await Assert.That(documents[0].GetProperty("value").GetProperty("ready").GetBoolean()).IsTrue();
        using (var removed = await PostAsync(client, "/api/v1/dashboard/databases/regions", csrf, new() { ["databaseId"] = actualId }, ["GRU"]))
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await EvidenceAsync(removed, producer));
        await DeleteAsync(client, csrf, actualId, name);
        observed = await JsonAsync(producer, "/__test/observations");
        await Assert.That(observed.GetProperty("creates").GetInt32()).IsEqualTo(1);
        await Assert.That(observed.GetProperty("databases").GetArrayLength()).IsEqualTo(0);
        await Assert.That(observed.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [Arguments("id")]
    [Arguments("project")]
    [Arguments("name")]
    [Arguments("home")]
    [Arguments("replicas")]
    [Arguments("missing-replicas")]
    public async Task NativePlacementRefusesAnUnconfirmedProducerResponse(string mismatch)
    {
        var invalid = false;
        var organizationId = "";
        var projectId = "";
        var id = "";
        var name = "confirmed-placement";
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (!Volatile.Read(ref invalid) || http.Request.Method != "PATCH" ||
                http.Request.Path != "/v1/management/organizations/" + organizationId + "/projects/" + projectId + "/databases/" + id)
                return Task.FromResult<IResult?>(null);
            DemandScope(http, organizationId);
            var value = new Dictionary<string, object?>
            {
                ["id"] = mismatch == "id" ? "01K78000000000000000000000" : id,
                ["projectId"] = mismatch == "project" ? "01K78000000000000000000001" : projectId,
                ["name"] = mismatch == "name" ? "another-database" : name,
                ["createdAt"] = DateTimeOffset.UtcNow,
                ["homeRegion"] = mismatch == "home" ? "sfo-1" : "gru-1",
                ["replicaRegions"] = mismatch == "replicas" ? Array.Empty<string>() : new[] { "sfo-1" }
            };
            if (mismatch == "missing-replicas") value.Remove("replicaRegions");
            return Task.FromResult<IResult?>(Results.Json(value));
        });
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var identity = await JsonAsync(client, "/api/v1/auth/me");
        organizationId = identity.GetProperty("organizationId").GetString()!;
        projectId = identity.GetProperty("projectId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        id = await CreateAsync(client, csrf, name);
        Volatile.Write(ref invalid, true);
        using (var response = await PostAsync(client, "/api/v1/dashboard/databases/regions", csrf, new() { ["databaseId"] = id }, ["GRU", "SFO"]))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(response, producer));
        var database = (await JsonAsync(client, "/api/v1/dashboard/databases"))[0];
        await Assert.That(database.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(database.GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(database.GetProperty("region").GetString()).IsEqualTo("GRU");
        await Assert.That(Regions(database)).IsEqualTo("GRU");
        Volatile.Write(ref invalid, false);
        await DeleteAsync(client, csrf, id, name);
        await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [Arguments("duplicate-id")]
    [Arguments("code-collision")]
    [Arguments("invalid-origin")]
    [Arguments("invalid-health")]
    [Arguments("missing-name")]
    public async Task InvalidDiscoveryNeverSilentlyDropsAnEntryAndNeverProvisions(string defect)
    {
        var invalid = false;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (!Volatile.Read(ref invalid) || http.Request.Host.Host != "atlas.terrascale.tech" || http.Request.Path != "/v1/discovery")
                return Task.FromResult<IResult?>(null);
            var bad = new Dictionary<string, object?>
            {
                ["id"] = defect switch { "duplicate-id" => "gru-1", "code-collision" => "gru-2", _ => "fra-1" },
                ["name"] = "Frankfurt",
                ["country"] = "DE",
                ["api"] = defect == "invalid-origin" ? "https://attacker.example" : "https://fra.api.terrascale.tech",
                ["health"] = defect == "invalid-health" ? "up" : "ok"
            };
            if (defect == "missing-name") bad.Remove("name");
            return Task.FromResult<IResult?>(Results.Json(new
            {
                regions = new object[] {
                new { id = "gru-1", name = "São Paulo", country = "BR", api = "https://gru.api.terrascale.tech", health = "ok" },
                new { id = "sfo-1", name = "San Francisco", country = "US", api = "https://sfo.api.terrascale.tech", health = "ok" }, bad }
            }));
        });
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        Volatile.Write(ref invalid, true);
        using (var response = await PostAsync(client, "/api/v1/dashboard/databases", csrf, new() { ["name"] = "catalog-refused" }, ["GRU"]))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(response, producer));
        Volatile.Write(ref invalid, false);
        await Assert.That((await JsonAsync(client, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
        await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("creates").GetInt32()).IsEqualTo(0);
        var id = await CreateAsync(client, csrf, "catalog-refused");
        await DeleteAsync(client, csrf, id, "catalog-refused");
        await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [Arguments("provisioning")]
    [Arguments("accepted")]
    [Arguments("pending")]
    [Arguments("failed")]
    public async Task CreatedNoticeUsesTheRecordedStatusInsteadOfAlwaysClaimingReady(string status)
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var identity = await JsonAsync(client, "/api/v1/auth/me");
        var organizationId = identity.GetProperty("organizationId").GetString()!;
        var projectId = identity.GetProperty("projectId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        const string name = "status-notice";
        var id = await CreateAsync(client, csrf, name);
        var ready = await PageAsync(client, "/databases/" + id + "?created=1");
        await Assert.That(ready).Contains("ts-db-created ts-alert-success\" data-status=\"ready\"");
        await Assert.That(ready).Contains(name + " is ready</p>");
        await ControlAsync(producer, new { command = "database-status", organizationId, projectId, databaseId = id, value = status });
        var page = await PageAsync(client, "/databases/" + id + "?created=1");
        await Assert.That(page).Contains("ts-db-created ts-alert-" + (status == "failed" ? "danger" : "info") + "\" data-status=\"" + status + "\"");
        await Assert.That(page).Contains(name + (status == "failed" ? " could not be created</p>" : " is being created</p>"));
        await Assert.That(page.Contains(name + " is ready</p>", StringComparison.Ordinal)).IsFalse();
        var list = await PageAsync(client, "/databases");
        var total = System.Text.RegularExpressions.Regex.Match(list,
            "<div class=\"ts-stat\" data-tone=\"amber\">.*?<dd class=\"ts-stat-value\">([0-9]+)</dd>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        await Assert.That(total.Success).IsTrue();
        await Assert.That(total.Groups[1].Value).IsEqualTo(status == "failed" ? "0" : "1")
            .Because("The original native summary counts provisioning, accepted and pending as in progress.");
        await Assert.That(list).Contains(status);
        await DeleteAsync(client, csrf, id, name);
        await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
    }

    private static void DemandScope(HttpContext http, string organization)
    {
        if (http.Request.Host.Host != "gru.api.terrascale.tech" || http.Request.Headers["X-TerraBase-Organization-Id"] != organization ||
            !http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal))
            throw new InvalidOperationException("A producer response fault must remain scoped to the actual home, organization and authenticated request.");
    }
    private static async Task<string> CreateAsync(HttpClient client, string csrf, string name)
    {
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("id").GetString()!;
    }
    private static async Task DeleteAsync(HttpClient client, string csrf, string id, string name)
    {
        using var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["confirmName"] = name });
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync());
        await Assert.That((await JsonAsync(client, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
    }
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string csrf, Dictionary<string, string> fields, string[] regions)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Accept.ParseAdd("text/html");
        request.Content = new FormUrlEncodedContent(fields.Append(new("_csrf", csrf)).Concat(regions.Select(region => new KeyValuePair<string, string>("region", region))));
        return await client.SendAsync(request);
    }
    private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone();
    }
    private static async Task<string> PageAsync(HttpClient client, string path)
    { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK); return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()); }
    private static async Task ControlAsync(HttpClient producer, object body)
    { using var response = await producer.PostAsJsonAsync("/__test/control", body); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await response.Content.ReadAsStringAsync()); }
    private static string Regions(JsonElement database) => string.Join(',', database.GetProperty("regions").EnumerateArray().Select(region => region.GetString()));
    private static async Task<string> EvidenceAsync(HttpResponseMessage response, HttpClient producer)
    { var body = await response.Content.ReadAsStringAsync(); return body[..Math.Min(body.Length, 1000)] + "; producer failures: " + (await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetRawText(); }
    private static async Task WaitAsync(HttpClient producer, string counter)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await JsonAsync(producer, "/__test/observations")).GetProperty(counter).GetInt32() == 0) await Task.Delay(20, deadline.Token);
    }
}
