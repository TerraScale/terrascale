using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E.Features.Usage;

[ClassDataSource<DashboardRootFixture>]
public sealed class UsageStorageJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task AbsentOperationMetersKeepReadsUnknownWhileNativeSdkStorageRemainsIndependentlyKnown()
    {
        Func<string, (string UserId, string OrganizationId, string ProjectId, IReadOnlyList<string> Scopes)?> resolveToken = null!;
        var organizationId = "";
        var projectId = "";
        var userId = "";
        var storagePath = "";
        var storageFault = 0;
        var observedStorageFaults = 0;
        var meters = new PartialMeasurements();
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root,
            builder =>
            {
                resolveToken = ((TestAuth)builder.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!).ResolveProjectToken;
                builder.Services.Replace(ServiceDescriptor.Singleton<IUsageMeasurements>(meters));
                builder.Services.Replace(ServiceDescriptor.Singleton<IUsageReplication>(new ReferenceUsageReplication()));
            },
            http =>
            {
                if (!HttpMethods.IsGet(http.Request.Method) || http.Request.Path.Value != storagePath || Volatile.Read(ref storageFault) == 0)
                    return Task.FromResult<IResult?>(null);
                var bearer = http.Request.Headers.Authorization.ToString();
                var actor = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? resolveToken(bearer[7..]) : null;
                if (http.Request.Host.Host is not ("gru.api.terrascale.tech" or "sfo.api.terrascale.tech") || actor is not { } grant || grant.UserId != userId ||
                    grant.OrganizationId != organizationId || grant.ProjectId != projectId || !grant.Scopes.Contains("databases:read") ||
                    http.Request.Headers.ContainsKey("X-TerraBase-Organization-Id"))
                    throw new InvalidOperationException("The independent storage fault must target the exact admitted SDK scope.");
                Interlocked.Increment(ref observedStorageFaults);
                IResult result = Volatile.Read(ref storageFault) switch
                {
                    1 => Results.Json(new { error = new { code = "unavailable", message = "Test-only storage outage." } }, statusCode: 503),
                    2 => Results.Json(new { items = new[] { new { name = "events", documents = 1, bytes = -1 } } }),
                    3 => Results.Text("{", "application/json"),
                    4 => Results.Json(new { error = new { code = "not_found", message = "Test-only missing storage response." } }, statusCode: 404),
                    5 => Results.Json(new { error = new { code = "forbidden", message = "Test-only storage rejection." } }, statusCode: 403),
                    6 => Results.Json(new { error = new { code = "conflict", message = "Test-only storage conflict." } }, statusCode: 409),
                    7 => Results.Json(new { error = new { code = "invalid_request", message = "Test-only storage validation rejection." } }, statusCode: 400),
                    8 => Results.Json(new { items = new[] { new { name = "events", documents = 1L, bytes = long.MaxValue }, new { name = "other", documents = 1L, bytes = 1L } } }),
                    _ => throw new InvalidOperationException("Unknown independent storage fault.")
                };
                return Task.FromResult<IResult?>(result);
            });
        using var first = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(first);
        using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var identity = JsonDocument.Parse(await first.GetStringAsync("/api/v1/auth/me"));
        organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
        projectId = identity.RootElement.GetProperty("projectId").GetString()!;
        userId = identity.RootElement.GetProperty("userId").GetString()!;
        var secret = await HttpJourney.CreatePatAsync(first, "Independent storage E2E", account.Password);
        var empty = await UsageAsync(first);
        await AssertUnknownOperationsAsync(empty);
        await Assert.That(empty.GetProperty("totals").GetProperty("logicalBytes").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(empty.GetProperty("totals").GetProperty("storageMeasured").GetBoolean()).IsFalse();
        await Assert.That(empty.GetProperty("allowances").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "Storage across regions").GetProperty("used").ValueKind).IsEqualTo(JsonValueKind.Null);
        var egress = empty.GetProperty("allowances").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "Client egress this month");
        await Assert.That(egress.GetProperty("used").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(egress.GetProperty("limit").GetInt64()).IsEqualTo(1_000_000_000);
        var emptyBody = WebUtility.HtmlDecode(await replica.GetStringAsync("/usage"));
        await Assert.That(emptyBody).Contains("Published allowances count 4 KiB operation units and storage across selected regions, and client egress. These document counters do not measure allowance utilization.");
        await Assert.That(emptyBody).Contains("Storage isn't measured here.");
        await Assert.That(emptyBody).Contains("Replication measurements are unavailable.");
        await Assert.That(emptyBody.Contains("Reads and writes could not be loaded", StringComparison.Ordinal)).IsFalse();

        var csrf = await HttpJourney.CsrfAsync(first, "/databases");
        using var created = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "independent-storage", ["region"] = "GRU" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var databaseId = database.RootElement.GetProperty("id").GetString()!;
        using (var collection = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/data/collections", new()
        { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["name"] = "events" }))
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var payload = "{\"message\":\"" + new string('a', 1010) + "\"}"; // 1024 document bytes, excluding files.
        using (var document = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/data/documents", new()
        { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["collection"] = "events", ["operation"] = "create", ["id"] = "01K78D0C000000000000000014", ["document"] = payload }))
            await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var known = await UsageAsync(replica, databaseId);
        await AssertUnknownOperationsAsync(known);
        var knownBytes = Encoding.UTF8.GetByteCount(payload);
        await Assert.That(known.GetProperty("totals").GetProperty("logicalBytes").GetInt64()).IsEqualTo(knownBytes);
        var row = known.GetProperty("databases")[0];
        await Assert.That(row.GetProperty("documents").GetInt64()).IsEqualTo(1);
        await Assert.That(row.GetProperty("logicalBytes").GetInt64()).IsEqualTo(knownBytes);
        await Assert.That(row.GetProperty("metered").GetBoolean()).IsFalse();
        await Assert.That(row.GetProperty("unavailable").GetBoolean()).IsFalse();
        foreach (var counter in new[] { "reads", "writes" }) await Assert.That(row.GetProperty(counter).ValueKind).IsEqualTo(JsonValueKind.Null);
        var knownBody = WebUtility.HtmlDecode(await first.GetStringAsync("/usage?project=" + projectId + "&database=" + databaseId));
        await Assert.That(knownBody).Contains("<dd class=\"ts-stat-value\">1.0 kB</dd>");
        await Assert.That(knownBody).Contains("<span class=\"ts-meta\" title=\"1\">1 document</span>");
        await Assert.That(knownBody.Contains("Storage isn't measured here.", StringComparison.Ordinal)).IsFalse();

        storagePath = "/v1/projects/" + projectId + "/databases/independent-storage/schema";
        foreach (var fault in Enumerable.Range(1, 8))
        {
            Volatile.Write(ref storageFault, fault);
            var missing = await UsageAsync(replica, databaseId);
            await AssertUnknownOperationsAsync(missing);
            await Assert.That(missing.GetProperty("totals").GetProperty("storageMeasured").GetBoolean()).IsFalse();
            await Assert.That(missing.GetProperty("totals").GetProperty("logicalBytes").ValueKind).IsEqualTo(JsonValueKind.Null);
            var missingRow = missing.GetProperty("databases")[0];
            await Assert.That(missingRow.GetProperty("documents").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(missingRow.GetProperty("logicalBytes").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(missingRow.GetProperty("unavailable").GetBoolean()).IsTrue();
            foreach (var allowance in missing.GetProperty("allowances").EnumerateArray())
            {
                await Assert.That(allowance.GetProperty("measured").GetBoolean()).IsFalse();
                await Assert.That(allowance.GetProperty("used").ValueKind).IsEqualTo(JsonValueKind.Null);
            }
            var missingBody = WebUtility.HtmlDecode(await first.GetStringAsync("/usage?project=" + projectId + "&database=" + databaseId));
            await Assert.That(missingBody).Contains("Storage isn't measured here.");
            await Assert.That(missingBody).Contains("Not measured");
        }
        await Assert.That(observedStorageFaults).IsGreaterThanOrEqualTo(16);
        Volatile.Write(ref storageFault, 0);
        await Assert.That((await UsageAsync(first, databaseId)).GetProperty("totals").GetProperty("logicalBytes").GetInt64()).IsEqualTo(knownBytes);

        var beforeForeign = observedStorageFaults;
        Volatile.Write(ref storageFault, 1);
        var foreign = await HttpJourney.McpAsync(first, secret, "tools/call", new { name = "get_usage", arguments = new { organizationId, projectId, databaseId = "foreign-database" } });
        await Assert.That(foreign.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(observedStorageFaults).IsEqualTo(beforeForeign);
        Volatile.Write(ref storageFault, 0);
        using var producer = scenario.CreateProviderClient();
        using (var admission = await producer.PostAsJsonAsync("/__test/control", new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId }))
            await Assert.That(admission.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using (var project = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/organization/projects", new()
        { ["_csrf"] = csrf, ["name"] = "Usage partial storage", ["region"] = "GRU" }))
            await Assert.That(project.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        meters.ProjectId = projectId;
        var partial = await UsageAsync(first);
        var measuredProject = partial.GetProperty("projects").EnumerateArray().Single(row => row.GetProperty("id").GetString() == projectId);
        await Assert.That(measuredProject.GetProperty("logicalBytes").GetInt64()).IsEqualTo(1536);
        foreach (var project in partial.GetProperty("projects").EnumerateArray())
            await Assert.That(project.GetProperty("sharePercent").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(partial.GetProperty("projects").EnumerateArray().Single(row => row.GetProperty("id").GetString() != projectId).GetProperty("logicalBytes").ValueKind).IsEqualTo(JsonValueKind.Null);
        var partialBody = WebUtility.HtmlDecode(await first.GetStringAsync("/usage"));
        await Assert.That(partialBody).Contains("Storage isn't measured here.");
        await Assert.That(partialBody.Contains("of logical storage in this organization", StringComparison.Ordinal)).IsFalse();
        meters.Complete = true;
        var complete = await UsageAsync(first);
        await Assert.That(complete.GetProperty("allowances")[0].GetProperty("used").GetInt64()).IsEqualTo(29);
        var emptyProjectId = partial.GetProperty("projects").EnumerateArray().Single(row => row.GetProperty("id").GetString() != projectId).GetProperty("id").GetString();
        var completeBody = WebUtility.HtmlDecode(await first.GetStringAsync("/usage?project=" + emptyProjectId));
        await Assert.That(completeBody).Contains("ts-usage-gauge-value\" title=\"29\">29</span>");
        await Assert.That(completeBody).Contains("No activity yet");
        await Assert.That(completeBody.Contains("ts-usage-chart-shell", StringComparison.Ordinal)).IsFalse();

        async Task<JsonElement> UsageAsync(HttpClient client, string selectedDatabase = "")
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "get_usage", arguments = new { organizationId, projectId, databaseId = selectedDatabase } });
            var result = response.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(response.ToString());
            return result.GetProperty("structuredContent");
        }
    }

    private sealed class PartialMeasurements : IUsageMeasurements
    {
        public string ProjectId { get; set; } = "";
        public bool Complete { get; set; }
        public Task<UsageMeasurements> ReadAsync(UsageSelection selection, CancellationToken cancellationToken)
        {
            if (ProjectId.Length == 0) return new ReferenceUsageMeasurements().ReadAsync(selection, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var month = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            var monthly = selection.Context.Organization!.Projects.Keys.ToDictionary(id => id, id => id == ProjectId || Complete
                ? new UsageObservation(UsageSourceState.Measured, new(id, "", id == ProjectId ? 1536 : 0, id == ProjectId ? 1 : 0,
                    new(now, now, "day", 0, 0), id == ProjectId ? [new(now, 0, 0)] : [], [], new(month, 6, 3, 9, 29))) : new UsageObservation(UsageSourceState.Unavailable));
            return Task.FromResult(new UsageMeasurements(Complete ? monthly[selection.Context.Project!.Id] : new(UsageSourceState.Unavailable), monthly));
        }
    }

    private static async Task AssertUnknownOperationsAsync(JsonElement report)
    {
        var totals = report.GetProperty("totals");
        await Assert.That(totals.GetProperty("available").GetBoolean()).IsFalse();
        await Assert.That(totals.GetProperty("source").GetString()).IsEqualTo("This deployment has no TerraBase, which meters reads and writes.");
        foreach (var counter in new[] { "reads", "writes" }) await Assert.That(totals.GetProperty(counter).ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(totals.GetProperty("series").GetArrayLength()).IsEqualTo(0);
        await Assert.That(report.GetProperty("allowanceError").GetString()).IsEqualTo("");
        foreach (var allowance in report.GetProperty("allowances").EnumerateArray())
        {
            await Assert.That(allowance.GetProperty("measured").GetBoolean()).IsFalse();
            await Assert.That(allowance.GetProperty("used").ValueKind).IsEqualTo(JsonValueKind.Null);
        }
    }
}
