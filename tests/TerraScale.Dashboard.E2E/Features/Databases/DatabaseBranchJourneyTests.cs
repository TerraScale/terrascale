using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Shared.Databases;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseBranchJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task OfficialSdkSnapshotAndInterruptedBranchRecoverWithExactProvenanceAndActualReplicas()
    {
        var organizationId = "";
        var projectId = "";
        var mode = "";
        var bearer = "";
        var calls = 0;
        var lists = 0;
        const string sourceName = "snapshot-source";
        const string retryName = "snapshot-recovered";
        TestAuth auth = null!;
        Uri origin = null!;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), async http =>
        {
            if (http.Request.Headers.ContainsKey(DatabaseSdkJourneyTests.Forwarded)) return null;
            var databases = "/v1/projects/" + projectId + "/databases";
            if (http.Request.Method == "POST" && http.Request.Path == databases + "/" + sourceName + "/branches")
            {
                DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organizationId, projectId, "databases:write");
                Interlocked.Increment(ref calls);
                bearer = http.Request.Headers.Authorization.ToString();
                if (Volatile.Read(ref mode) == "unsupported-conflict")
                    return Results.Json(new { error = new { code = "database_busy", message = "Unsupported scoped conflict", fix = "retry" } }, statusCode: 409);
            }
            if (http.Request.Method == "GET" && http.Request.Path == databases)
            {
                DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organizationId, projectId, "databases:read");
                Interlocked.Increment(ref lists);
                if (Volatile.Read(ref mode) is "list-home" or "list-source")
                {
                    // The real listing, with only the interrupted branch's placement or provenance changed.
                    var (status, body) = await DatabaseSdkJourneyTests.ForwardAsync(http, origin);
                    foreach (var row in body!["items"]!.AsArray().Select(item => item!.AsObject()).Where(item => (string?)item["name"] == retryName))
                        if (mode == "list-home") row["homeRegion"] = "sfo"; else row["branchedFrom"] = "01K78000000000000000000000";
                    return Results.Json(body, statusCode: status);
                }
            }
            return null;
        });
        origin = scenario.ProviderOrigin;
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var producer = scenario.CreateProviderClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var identity = await ReadAsync(client, "/api/v1/auth/me");
        organizationId = identity.GetProperty("organizationId").GetString()!;
        projectId = identity.GetProperty("projectId").GetString()!;
        // Resource admission is an explicit test fixture fact. Actual paid
        // checkout is independently exercised by the native billing journeys.
        await ControlAsync(producer, new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId });
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/databases");
        var sourceId = await CreateAsync(client, csrf, sourceName);
        var branchPage = WebUtility.HtmlDecode(await client.GetStringAsync("/databases/" + sourceId + "/branches"));
        await Assert.That(branchPage).Contains("pattern=\"" + DatabaseService.NameInputPattern + "\"");
        await Assert.That(branchPage).Contains(DatabaseService.NameHint);
        foreach (var name in new[] { "1database", "con", "com0", "lpt9" })
        {
            using var rejected = await BranchAsync(client, csrf, sourceId, name);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(await rejected.Content.ReadAsStringAsync()).Contains("must start with a lowercase letter");
        }
        await Assert.That(calls).IsEqualTo(0).Because("Invalid names must be rejected before reaching the branch provider.");
        using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
        { ["_csrf"] = csrf, ["databaseId"] = sourceId, ["name"] = "users" }))
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await WriteAsync(client, csrf, sourceId, "create", "original");
        const string firstName = "snapshot-first";
        string firstId;
        using (var first = await BranchAsync(client, csrf, sourceId, firstName))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await EvidenceAsync(first, producer));
            var row = await BodyAsync(first);
            firstId = row.GetProperty("id").GetString()!;
            await Assert.That(row.GetProperty("branchedFrom").GetString()).IsEqualTo(sourceId);
            await Assert.That(Regions(row)).IsEqualTo("GRU");
        }
        await Assert.That(await ValueAsync(replica, firstId)).IsEqualTo("original");
        await WriteAsync(replica, replicaCsrf, firstId, "replace", "branch-only");
        await Assert.That(await ValueAsync(client, sourceId)).IsEqualTo("original");
        await DeleteAsync(client, csrf, firstId, firstName);

        await ControlAsync(producer, new { command = "fail-database-metadata", organizationId, projectId, name = retryName });
        using (var interrupted = await BranchAsync(client, csrf, sourceId, retryName))
            await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(interrupted, producer));
        var observations = await ReadAsync(producer, "/__test/observations");
        await Assert.That(observations.GetProperty("branchCreates").GetInt32()).IsEqualTo(2);
        await Assert.That(observations.GetProperty("failedDatabaseMetadataWrites").GetInt32()).IsEqualTo(1);
        var recoveredId = observations.GetProperty("databases").EnumerateArray().Single(database => database.GetProperty("name").GetString() == retryName)
            .GetProperty("id").GetString()!;
        // No listing runs before the retries: a listing would record the branch TerraBase has, as the recovery below does.

        // A real provider placement change between the interrupted 201 and the retry must survive recovery. Another
        // client places it with the same exchanged databases:write project bearer, at the canonical route by name.
        using (var request = new HttpRequestMessage(HttpMethod.Patch, "/v1/projects/" + projectId + "/databases/" + retryName))
        {
            request.Headers.Host = "gru.api.terrascale.tech";
            request.Headers.Add("Authorization", bearer);
            request.Content = JsonContent.Create(new { replicaRegions = new[] { "sfo" } });
            using var placed = await producer.SendAsync(request);
            await Assert.That(placed.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await EvidenceAsync(placed, producer));
        }
        var previousCalls = Volatile.Read(ref calls);
        await ControlAsync(producer, new { command = "role", organizationId, value = "member" });
        using (var refused = await BranchAsync(replica, replicaCsrf, sourceId, retryName))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden).Because(await EvidenceAsync(refused, producer));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(previousCalls);
        await ControlAsync(producer, new { command = "role", organizationId, value = "owner" });
        using (var unavailable = await BranchAsync(client, csrf, "01K78000000000000000000000", "foreign-source"))
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(previousCalls);

        foreach (var fault in new[] { "list-home", "list-source" })
        {
            Volatile.Write(ref mode, fault);
            using var refused = await BranchAsync(replica, replicaCsrf, sourceId, retryName);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await EvidenceAsync(refused, producer));
        }
        Volatile.Write(ref mode, "unsupported-conflict");
        var beforeUnsupported = Volatile.Read(ref lists);
        using (var unsupported = await BranchAsync(client, csrf, sourceId, retryName))
            await Assert.That(unsupported.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await EvidenceAsync(unsupported, producer));
        await Assert.That(Volatile.Read(ref lists)).IsEqualTo(beforeUnsupported + 1)
            .Because("Only the source ID resolution lists; an unsupported conflict is never recovered by name.");
        Volatile.Write(ref mode, "");
        await WriteAsync(client, csrf, sourceId, "replace", "source-after-snapshot");
        using (var recovered = await BranchAsync(replica, replicaCsrf, sourceId, retryName))
        {
            await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await EvidenceAsync(recovered, producer));
            var row = await BodyAsync(recovered);
            await Assert.That(row.GetProperty("id").GetString()).IsEqualTo(recoveredId);
            await Assert.That(row.GetProperty("branchedFrom").GetString()).IsEqualTo(sourceId);
            await Assert.That(row.GetProperty("region").GetString()).IsEqualTo("GRU");
            await Assert.That(Regions(row)).IsEqualTo("GRU,SFO");
        }
        await Assert.That(await ValueAsync(client, recoveredId)).IsEqualTo("original");
        await Assert.That(await ValueAsync(replica, sourceId)).IsEqualTo("source-after-snapshot");
        await DeleteAsync(replica, replicaCsrf, recoveredId, retryName);
        await DeleteAsync(client, csrf, sourceId, sourceName);
        observations = await ReadAsync(producer, "/__test/observations");
        await Assert.That(observations.GetProperty("branchCreates").GetInt32()).IsEqualTo(2);
        await Assert.That(observations.GetProperty("branchConflicts").GetInt32()).IsEqualTo(3);
        await Assert.That(observations.GetProperty("databases").GetArrayLength()).IsEqualTo(0);
        await Assert.That(observations.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [Arguments("failed")]
    [Arguments("provisioning")]
    [Arguments("accepted")]
    [Arguments("pending")]
    public async Task FinalCommitRechecksSourceReadinessAfterTheActualProducerSnapshot(string status)
    {
        var organizationId = "";
        var projectId = "";
        var paused = true;
        var entered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpClient? producer = null;
        TestAuth auth = null!;
        Uri origin = null!;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), async http =>
        {
            if (!Volatile.Read(ref paused) || http.Request.Headers.ContainsKey(DatabaseSdkJourneyTests.Forwarded) || http.Request.Method != "POST" ||
                http.Request.Path != "/v1/projects/" + projectId + "/databases/commit-source/branches") return null;
            DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organizationId, projectId, "databases:write");
            // The producer really snapshots; its 201 is held until the source status changes.
            var (code, body) = await DatabaseSdkJourneyTests.ForwardAsync(http, origin);
            entered.TrySetResult(JsonSerializer.SerializeToElement(body));
            await release.Task.WaitAsync(http.RequestAborted);
            return Results.Json(body, statusCode: code);
        });
        origin = scenario.ProviderOrigin;
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using (producer = scenario.CreateProviderClient())
        {
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var identity = await ReadAsync(client, "/api/v1/auth/me");
            organizationId = identity.GetProperty("organizationId").GetString()!;
            projectId = identity.GetProperty("projectId").GetString()!;
            await ControlAsync(producer, new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId });
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/databases");
            var sourceId = await CreateAsync(client, csrf, "commit-source");
            var pending = BranchAsync(client, csrf, sourceId, "commit-branch");
            JsonElement actual = default;
            try
            {
                actual = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Assert.That(actual.GetProperty("branchedFrom").GetString()).IsEqualTo(sourceId);
                await Assert.That((await ReadAsync(producer, "/__test/observations")).GetProperty("branchCreates").GetInt32()).IsEqualTo(1);
                await ControlAsync(producer, new { command = "database-status", organizationId, projectId, databaseId = sourceId, value = status });
                var rows = await ReadAsync(replica, "/api/v1/dashboard/databases");
                await Assert.That(rows.EnumerateArray().Single(row => row.GetProperty("id").GetString() == sourceId).GetProperty("status").GetString()).IsEqualTo(status);
            }
            finally { Volatile.Write(ref paused, false); release.TrySetResult(); }
            using (var refused = await pending)
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await EvidenceAsync(refused, producer));
            // The request is refused, but TerraBase owns databases: the branch it made is listed with its own identity.
            foreach (var instance in new[] { client, replica })
                await Assert.That((await ReadAsync(instance, "/api/v1/dashboard/databases")).EnumerateArray().Single(row => row.GetProperty("name").GetString() == "commit-branch")
                    .GetProperty("id").GetString()).IsEqualTo(actual.GetProperty("id").GetString());
            await DeleteAsync(replica, replicaCsrf, actual.GetProperty("id").GetString()!, "commit-branch");
            await DeleteAsync(client, csrf, sourceId, "commit-source");
            var observed = await ReadAsync(producer, "/__test/observations");
            await Assert.That(observed.GetProperty("branchCreates").GetInt32()).IsEqualTo(1);
            await Assert.That(observed.GetProperty("branchConflicts").GetInt32()).IsEqualTo(0);
            await Assert.That(observed.GetProperty("databases").GetArrayLength()).IsEqualTo(0);
            await Assert.That(observed.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("id")]
    [Arguments("project")]
    [Arguments("name")]
    [Arguments("home")]
    [Arguments("source")]
    [Arguments("missing-source")]
    public async Task BranchNeverPublishesUnconfirmedIdentityPlacementOrProvenance(string mismatch)
    {
        var corrupt = false;
        var organizationId = "";
        var projectId = "";
        var sourceId = "";
        HttpClient? producer = null;
        TestAuth auth = null!;
        Uri origin = null!;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), async http =>
        {
            if (!Volatile.Read(ref corrupt) || http.Request.Headers.ContainsKey(DatabaseSdkJourneyTests.Forwarded) || http.Request.Method != "POST" ||
                http.Request.Path != "/v1/projects/" + projectId + "/databases/confirmation-source/branches") return null;
            DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organizationId, projectId, "databases:write");
            // The producer really creates the branch; only its 201 answer is altered.
            var (status, body) = await DatabaseSdkJourneyTests.ForwardAsync(http, origin);
            var row = body!.AsObject();
            switch (mismatch)
            {
                case "id": row["id"] = sourceId; break;
                case "project": row["projectId"] = "01K78000000000000000000001"; break;
                case "name": row["name"] = "another-branch"; break;
                case "home": row["homeRegion"] = "sfo"; break;
                case "source": row["branchedFrom"] = "01K78000000000000000000002"; break;
                case "missing-source": row.Remove("branchedFrom"); break;
            }
            return Results.Json(row, statusCode: status);
        });
        origin = scenario.ProviderOrigin;
        using var client = scenario.CreateClient();
        using (producer = scenario.CreateProviderClient())
        {
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var identity = await ReadAsync(client, "/api/v1/auth/me");
            organizationId = identity.GetProperty("organizationId").GetString()!;
            projectId = identity.GetProperty("projectId").GetString()!;
            await ControlAsync(producer, new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId });
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            sourceId = await CreateAsync(client, csrf, "confirmation-source");
            Volatile.Write(ref corrupt, true);
            using (var refused = await BranchAsync(client, csrf, sourceId, "confirmation-branch"))
                await Assert.That(refused.StatusCode).IsEqualTo(mismatch == "home" ? HttpStatusCode.Conflict : HttpStatusCode.ServiceUnavailable)
                    .Because(await EvidenceAsync(refused, producer));
            Volatile.Write(ref corrupt, false);
            // The altered answer is never recorded; the listing records the branch as TerraBase lists it.
            var rows = await ReadAsync(client, "/api/v1/dashboard/databases");
            await Assert.That(rows.GetArrayLength()).IsEqualTo(2);
            var branch = rows.EnumerateArray().Single(row => row.GetProperty("id").GetString() != sourceId);
            await Assert.That(branch.GetProperty("name").GetString()).IsEqualTo("confirmation-branch");
            await Assert.That(branch.GetProperty("projectId").GetString()).IsEqualTo(projectId);
            await Assert.That(branch.GetProperty("region").GetString()).IsEqualTo("GRU");
            await Assert.That(branch.GetProperty("branchedFrom").GetString()).IsEqualTo(sourceId);
            var branchId = branch.GetProperty("id").GetString()!;
            await DeleteAsync(client, csrf, branchId, "confirmation-branch");
            await DeleteAsync(client, csrf, sourceId, "confirmation-source");
            var observed = await ReadAsync(producer, "/__test/observations");
            await Assert.That(observed.GetProperty("branchCreates").GetInt32()).IsEqualTo(1);
            await Assert.That(observed.GetProperty("branchConflicts").GetInt32()).IsEqualTo(0);
            await Assert.That(observed.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }
    }

    private static Task<HttpResponseMessage> BranchAsync(HttpClient client, string csrf, string source, string name) =>
        HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/branch", new() { ["_csrf"] = csrf, ["databaseId"] = source, ["name"] = name });
    private static async Task<string> CreateAsync(HttpClient client, string csrf, string name)
    {
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
        return (await BodyAsync(response)).GetProperty("id").GetString()!;
    }
    private static async Task WriteAsync(HttpClient client, string csrf, string id, string operation, string value)
    {
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "users", ["id"] = "01K78D0C000000000000000009", ["operation"] = operation, ["document"] = JsonSerializer.Serialize(new { value }) });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
    }
    private static async Task<string> ValueAsync(HttpClient client, string id) =>
        (await ReadAsync(client, "/api/v1/dashboard/data/documents?databaseId=" + id + "&collection=users")).GetProperty("documents")[0].GetProperty("value").GetProperty("value").GetString()!;
    private static async Task DeleteAsync(HttpClient client, string csrf, string id, string name)
    {
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["confirmName"] = name });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await response.Content.ReadAsStringAsync());
    }
    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync()); return await BodyAsync(response); }
    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
    private static async Task ControlAsync(HttpClient client, object body)
    { using var response = await client.PostAsJsonAsync("/__test/control", body); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await response.Content.ReadAsStringAsync()); }
    private static string Regions(JsonElement row) => string.Join(',', row.GetProperty("regions").EnumerateArray().Select(region => region.GetString()));
    private static async Task<string> EvidenceAsync(HttpResponseMessage response, HttpClient producer)
    { var body = await response.Content.ReadAsStringAsync(); return body[..Math.Min(1000, body.Length)] + "; producer failures: " + (await ReadAsync(producer, "/__test/observations")).GetProperty("failures").GetRawText(); }
}
