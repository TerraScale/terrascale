using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Accounts;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
public sealed class DatabaseBranchJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task OfficialSdkSnapshotAndInterruptedBranchRecoverWithExactProvenanceAndActualReplicas()
    {
        var organizationId = "";
        var projectId = "";
        var sourceId = "";
        var mode = "";
        var bearer = "";
        var actingUser = "";
        var calls = 0;
        var lists = 0;
        JsonElement pendingBranch = default;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            var projectPath = "/v1/management/organizations/" + organizationId + "/projects/" + projectId;
            if (http.Request.Method == "POST" && http.Request.Path == projectPath + "/databases/" + sourceId + "/branches")
            {
                DemandScope(http, organizationId);
                Interlocked.Increment(ref calls);
                bearer = http.Request.Headers.Authorization.ToString();
                actingUser = http.Request.Headers["X-TerraBase-User-Id"].ToString();
                if (Volatile.Read(ref mode) == "unsupported-conflict")
                    return Task.FromResult<IResult?>(Results.Json(new { error = new { code = "database_busy", message = "Unsupported scoped conflict", fix = "retry" } }, statusCode: 409));
            }
            if (http.Request.Method == "GET" && http.Request.Path == projectPath + "/databases")
            {
                DemandScope(http, organizationId);
                Interlocked.Increment(ref lists);
                if (Volatile.Read(ref mode) is "list-home" or "list-source")
                {
                    var row = JsonNode.Parse(pendingBranch.GetRawText())!.AsObject();
                    if (mode == "list-home") row["homeRegion"] = "sfo-1";
                    else row["branchedFrom"] = "01K78000000000000000000000";
                    return Task.FromResult<IResult?>(Results.Json(new { items = new[] { row }, next = (string?)null }));
                }
            }
            return Task.FromResult<IResult?>(null);
        });
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
        const string sourceName = "snapshot-source";
        sourceId = await CreateAsync(client, csrf, sourceName);
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

        const string retryName = "snapshot-recovered";
        await ControlAsync(producer, new { command = "fail-database-metadata", organizationId, projectId, name = retryName });
        using (var interrupted = await BranchAsync(client, csrf, sourceId, retryName))
            await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(interrupted, producer));
        var observations = await ReadAsync(producer, "/__test/observations");
        await Assert.That(observations.GetProperty("branchCreates").GetInt32()).IsEqualTo(2);
        await Assert.That(observations.GetProperty("failedDatabaseMetadataWrites").GetInt32()).IsEqualTo(1);
        pendingBranch = observations.GetProperty("databases").EnumerateArray().Single(database => database.GetProperty("name").GetString() == retryName).Clone();
        var recoveredId = pendingBranch.GetProperty("id").GetString()!;
        await Assert.That((await ReadAsync(replica, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(1);

        // A real provider placement change between the interrupted201 and
        // retry must survive recovery. The admitted SDK bearer stays in memory.
        using (var request = new HttpRequestMessage(HttpMethod.Patch, "/v1/management/organizations/" + organizationId + "/projects/" + projectId + "/databases/" + recoveredId))
        {
            request.Headers.Host = "gru.api.terrascale.tech";
            request.Headers.Add("Authorization", bearer);
            request.Headers.Add("X-TerraBase-User-Id", actingUser);
            request.Headers.Add("X-TerraBase-Organization-Id", organizationId);
            request.Content = JsonContent.Create(new { replicaRegions = new[] { "sfo-1" } });
            using var placed = await producer.SendAsync(request);
            await Assert.That(placed.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await EvidenceAsync(placed, producer));
            pendingBranch = await BodyAsync(placed);
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
            await Assert.That((await ReadAsync(client, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(1);
        }
        Volatile.Write(ref mode, "unsupported-conflict");
        var beforeUnsupported = Volatile.Read(ref lists);
        using (var unsupported = await BranchAsync(client, csrf, sourceId, retryName))
            await Assert.That(unsupported.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await EvidenceAsync(unsupported, producer));
        await Assert.That(Volatile.Read(ref lists)).IsEqualTo(beforeUnsupported);
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
        using (var removed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/regions", new()
        { ["_csrf"] = csrf, ["databaseId"] = recoveredId, ["region"] = "GRU" }))
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
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
        var sourceId = "";
        var paused = true;
        var entered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpClient? producer = null;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            var prefix = "/v1/management/organizations/" + organizationId + "/projects/" + projectId + "/databases/";
            var path = http.Request.Path.Value ?? "";
            if (!Volatile.Read(ref paused) || http.Request.Method != "GET" || !path.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var requested = path[prefix.Length..];
            if (requested.Length == 0 || requested == sourceId || requested.Contains('/')) return null;
            DemandScope(http, organizationId);
            using var response = await producer!.GetAsync("/__test/observations", http.RequestAborted);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(http.RequestAborted));
            var actual = json.RootElement.GetProperty("databases").EnumerateArray().Single(row => row.GetProperty("id").GetString() == requested).Clone();
            entered.TrySetResult(actual);
            await release.Task.WaitAsync(http.RequestAborted);
            return null;
        });
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
            sourceId = await CreateAsync(client, csrf, "commit-source");
            var pending = BranchAsync(client, csrf, sourceId, "commit-branch");
            JsonElement actual = default;
            try
            {
                actual = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Assert.That(actual.GetProperty("branchedFrom").GetString()).IsEqualTo(sourceId);
                await Assert.That((await ReadAsync(producer, "/__test/observations")).GetProperty("branchCreates").GetInt32()).IsEqualTo(1);
                await ControlAsync(producer, new { command = "database-status", organizationId, projectId, databaseId = sourceId, value = status });
                var rows = await ReadAsync(replica, "/api/v1/dashboard/databases");
                await Assert.That(rows.GetArrayLength()).IsEqualTo(1);
                await Assert.That(rows[0].GetProperty("status").GetString()).IsEqualTo(status);
            }
            finally { Volatile.Write(ref paused, false); release.TrySetResult(); }
            using (var refused = await pending)
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await EvidenceAsync(refused, producer));
            foreach (var instance in new[] { client, replica })
                await Assert.That((await ReadAsync(instance, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(1)
                    .Because("A real producer snapshot does not authorize publishing a branch after the exact canonical source stopped being ready.");
            await ControlAsync(producer, new { command = "database-status", organizationId, projectId, databaseId = sourceId, value = "ready" });
            using (var retry = await BranchAsync(replica, replicaCsrf, sourceId, "commit-branch"))
            {
                await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await EvidenceAsync(retry, producer));
                await Assert.That((await BodyAsync(retry)).GetProperty("id").GetString()).IsEqualTo(actual.GetProperty("id").GetString());
            }
            await DeleteAsync(replica, replicaCsrf, actual.GetProperty("id").GetString()!, "commit-branch");
            await DeleteAsync(client, csrf, sourceId, "commit-source");
            var observed = await ReadAsync(producer, "/__test/observations");
            await Assert.That(observed.GetProperty("branchCreates").GetInt32()).IsEqualTo(1);
            await Assert.That(observed.GetProperty("branchConflicts").GetInt32()).IsEqualTo(1);
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
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            var prefix = "/v1/management/organizations/" + organizationId + "/projects/" + projectId + "/databases/";
            var path = http.Request.Path.Value ?? "";
            if (!Volatile.Read(ref corrupt) || http.Request.Method != "GET" || !path.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var requested = path[prefix.Length..];
            if (requested == sourceId || requested.Contains('/')) return null;
            DemandScope(http, organizationId);
            using var observed = await producer!.GetAsync("/__test/observations");
            observed.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await observed.Content.ReadAsStringAsync());
            var real = json.RootElement.GetProperty("databases").EnumerateArray().Single(database => database.GetProperty("id").GetString() == requested);
            var row = JsonNode.Parse(real.GetRawText())!.AsObject();
            switch (mismatch)
            {
                case "id": row["id"] = "01K78000000000000000000000"; break;
                case "project": row["projectId"] = "01K78000000000000000000001"; break;
                case "name": row["name"] = "another-branch"; break;
                case "home": row["homeRegion"] = "sfo-1"; break;
                case "source": row["branchedFrom"] = "01K78000000000000000000002"; break;
                case "missing-source": row.Remove("branchedFrom"); break;
            }
            return Results.Json(row);
        });
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
            var rows = await ReadAsync(client, "/api/v1/dashboard/databases");
            await Assert.That(rows.GetArrayLength()).IsEqualTo(1);
            await Assert.That(rows[0].GetProperty("id").GetString()).IsEqualTo(sourceId);
            Volatile.Write(ref corrupt, false);
            string branchId;
            using (var recovered = await BranchAsync(client, csrf, sourceId, "confirmation-branch"))
            {
                await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await EvidenceAsync(recovered, producer));
                var row = await BodyAsync(recovered);
                branchId = row.GetProperty("id").GetString()!;
                await Assert.That(row.GetProperty("branchedFrom").GetString()).IsEqualTo(sourceId);
            }
            await DeleteAsync(client, csrf, branchId, "confirmation-branch");
            await DeleteAsync(client, csrf, sourceId, "confirmation-source");
            var observed = await ReadAsync(producer, "/__test/observations");
            await Assert.That(observed.GetProperty("branchCreates").GetInt32()).IsEqualTo(1);
            await Assert.That(observed.GetProperty("branchConflicts").GetInt32()).IsEqualTo(1);
            await Assert.That(observed.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }
    }

    private static void DemandScope(HttpContext http, string organization)
    {
        if (http.Request.Host.Host != "gru.api.terrascale.tech" || http.Request.Headers["X-TerraBase-Organization-Id"] != organization ||
            !http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(http.Request.Headers["X-TerraBase-User-Id"]))
            throw new InvalidOperationException("Branch boundary evidence must use the genuine scoped home and official admitted SDK identity headers.");
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
        { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "users", ["id"] = "customer", ["operation"] = operation, ["document"] = JsonSerializer.Serialize(new { value }) });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
    }
    private static async Task<string> ValueAsync(HttpClient client, string id) =>
        (await ReadAsync(client, "/api/v1/dashboard/data/documents?databaseId=" + id + "&collection=users"))[0].GetProperty("value").GetProperty("value").GetString()!;
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
