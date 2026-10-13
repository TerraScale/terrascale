using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E.Features.Usage;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
public sealed class UsageStorageJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task AbsentOperationMetersKeepReadsUnknownWhileNativeSdkStorageRemainsIndependentlyKnown()
    {
        Func<string, (string UserId, string Email)?> resolveToken = null!;
        var organizationId = "";
        var userId = "";
        var storagePath = "";
        var storageFault = 0;
        var observedStorageFaults = 0;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root,
            builder =>
            {
                resolveToken = ((TestAuth)builder.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!).ResolveAccessToken;
                builder.Services.Replace(ServiceDescriptor.Singleton<IUsageMeasurements>(new ReferenceUsageMeasurements()));
                builder.Services.Replace(ServiceDescriptor.Singleton<IUsageReplication>(new ReferenceUsageReplication()));
            },
            http =>
            {
                if (!HttpMethods.IsGet(http.Request.Method) || http.Request.Path.Value != storagePath || Volatile.Read(ref storageFault) == 0)
                    return Task.FromResult<IResult?>(null);
                var bearer = http.Request.Headers.Authorization.ToString();
                var actor = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? resolveToken(bearer[7..]) : null;
                if (http.Request.Host.Host is not ("gru.api.terrascale.tech" or "sfo.api.terrascale.tech") || actor?.UserId != userId ||
                    http.Request.Headers["X-TerraBase-Organization-Id"] != organizationId)
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
        var projectId = identity.RootElement.GetProperty("projectId").GetString()!;
        userId = identity.RootElement.GetProperty("userId").GetString()!;
        var secret = await HttpJourney.CreatePatAsync(first, "Independent storage E2E", account.Password);
        var empty = await UsageAsync(first);
        await AssertUnknownOperationsAsync(empty);
        await Assert.That(empty.GetProperty("totals").GetProperty("logicalBytes").GetInt64()).IsEqualTo(0);
        await Assert.That(empty.GetProperty("totals").GetProperty("storageMeasured").GetBoolean()).IsTrue();
        await Assert.That(empty.GetProperty("allowances").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "Storage").GetProperty("used").GetInt64()).IsEqualTo(0);
        var emptyBody = WebUtility.HtmlDecode(await replica.GetStringAsync("/usage"));
        await Assert.That(emptyBody).Contains("This month's reads and writes aren't metered here, so their utilization is unavailable.");
        await Assert.That(emptyBody).Contains("<dd class=\"ts-stat-value\">0 B</dd>");
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
        const string payload = "{\"message\":\"known storage\"}";
        using (var document = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/data/documents", new()
        { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["collection"] = "events", ["operation"] = "create", ["id"] = "independent-storage", ["document"] = payload }))
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
        await Assert.That(knownBody).Contains("<dd class=\"ts-stat-value\">" + knownBytes + " B</dd>");
        await Assert.That(knownBody).Contains("1 documents");
        await Assert.That(knownBody.Contains("Storage isn't measured here.", StringComparison.Ordinal)).IsFalse();

        storagePath = "/v1/management/organizations/" + organizationId + "/projects/" + projectId + "/databases/" + databaseId + "/collections";
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

        async Task<JsonElement> UsageAsync(HttpClient client, string selectedDatabase = "")
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "get_usage", arguments = new { organizationId, projectId, databaseId = selectedDatabase } });
            var result = response.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(response.ToString());
            return result.GetProperty("structuredContent");
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
        foreach (var allowance in report.GetProperty("allowances").EnumerateArray().Where(row => row.GetProperty("name").GetString() != "Storage"))
        {
            await Assert.That(allowance.GetProperty("measured").GetBoolean()).IsFalse();
            await Assert.That(allowance.GetProperty("used").ValueKind).IsEqualTo(JsonValueKind.Null);
        }
    }
}
