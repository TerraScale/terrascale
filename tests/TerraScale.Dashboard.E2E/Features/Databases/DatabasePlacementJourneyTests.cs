using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabasePlacementJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task NativePlacementKeepsItsCanonicalHomeAndScopesReportedReplication()
    {
        var report = "";
        var placementWrites = 0;
        var projectId = "";
        var databaseId = "";
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (http.Request.Method == "PATCH" && http.Request.Path.Value!.Contains("/databases/", StringComparison.Ordinal)) Interlocked.Increment(ref placementWrites);
            if (http.Request.Method != "GET" || databaseId.Length == 0 || !http.Request.Path.Value!.StartsWith("/v1/projects/", StringComparison.Ordinal) ||
                !http.Request.Path.Value.EndsWith("/replication", StringComparison.Ordinal))
                return Task.FromResult<IResult?>(null);
            var range = http.Request.Query["range"].ToString();
            var unit = range == "1h" ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(1);
            var utc = DateTimeOffset.UtcNow;
            var boundary = range == "1h"
                ? new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero)
                : new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
            var to = boundary + unit;
            var from = to - (range == "1h" ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24));
            if (http.Request.Path.Value != "/v1/projects/" + projectId + "/replication" || http.Request.Query["database"] != databaseId ||
                range is not ("1h" or "24h") || http.Request.Query.ContainsKey("limit") || !http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal))
                throw new InvalidOperationException("The real replication adapter must request the current project reporting route with its scoped bearer.");
            var scenario = Volatile.Read(ref report);
            var items = scenario == "measured" ? new object[] {
                new { databaseId, databaseName = "native-placement", region = "sfo", status = "diverged", replicatedOps = 12L, lagMs = (double?)12.5, divergence = (bool?)true, convergenceMs = (double?)30.5, lastApplied = (DateTimeOffset?)to,
                    bandwidth = new[] { new { sourceRegion = "sfo", destinationRegion = "gru", kind = "delta", bytesSent = 111L, bytesReceived = 100L } }, series = Array.Empty<object>() },
                new { databaseId, databaseName = "native-placement", region = "gru", status = "healthy", replicatedOps = 8L, lagMs = (double?)null, divergence = (bool?)null, convergenceMs = (double?)null, lastApplied = (DateTimeOffset?)null,
                    bandwidth = Array.Empty<object>(), series = Array.Empty<object>() }
            } : Array.Empty<object>();
            return Task.FromResult<IResult?>(Results.Json(new
            {
                projectId = scenario == "foreign" ? "01K78000000000000000000000" : projectId,
                range,
                from,
                to,
                bucketSeconds = range == "1h" ? 60 : 3600,
                items
            }));
        });
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var producer = scenario.CreateProviderClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var identity = await ReadAsync(client, "/api/v1/auth/me");
        projectId = identity.GetProperty("projectId").GetString()!;
        var organizationId = identity.GetProperty("organizationId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        // The protocol fixture reports SFO, so SFO is the one region the dashboard can choose.
        await Control(new { command = "api-region", value = "sfo" });
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "native-placement", ["region"] = "sfo" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            databaseId = (await BodyAsync(created)).GetProperty("id").GetString()!;
        }
        var path = "/databases/" + databaseId;
        var initial = await PageAsync(client, "/replication");
        await Assert.That(initial).Contains("<dt>Home region</dt><dd>sfo · San Francisco</dd>");
        await Assert.That(initial.Contains("No provider latency measurements are available.", StringComparison.Ordinal)).IsFalse();
        await Assert.That(initial).Contains("Per database and region · 24h · TerraBase replication_bytes meter");
        await Assert.That(initial).Contains("/analytics?database=" + databaseId + "&project=" + projectId + "&range=24h");
        await Assert.That(initial.Contains("0 ms", StringComparison.Ordinal)).IsFalse();
        var home = Regex.Match(initial, "<input[^>]+type=\"checkbox\"[^>]+value=\"SFO\"[^>]*>").Value;
        await Assert.That(home).Contains("required");
        await Assert.That(home).Contains("checked");
        await Assert.That(home.Contains("disabled", StringComparison.Ordinal)).IsFalse();
        using (var credentials = await client.GetAsync("/api-keys"))
            await Assert.That(credentials.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because(await FailureEvidenceAsync(credentials));

        foreach (var regions in new[] { new[] { "GRU" }, new[] { "SFO", "sfo" }, new[] { "SFO", "unknown" } })
        {
            using var refused = await ChangeRegionsAsync(regions);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(await RegionsAsync()).IsEqualTo("SFO,GRU");
        }
        await Assert.That(Volatile.Read(ref placementWrites)).IsEqualTo(0);
        // Later GRU answers instead. The SFO home is outside the catalog now and must stay selected and addressable.
        await Control(new { command = "api-region", value = "gru" });
        var moved = Regex.Match(await PageAsync(client, "/replication"), "<input[^>]+type=\"checkbox\"[^>]+value=\"SFO\"[^>]*>").Value;
        await Assert.That(moved).Contains("required");
        await Assert.That(moved).Contains("checked");
        using (var changed = await ChangeRegionsAsync(["gru", "sfo"]))
        {
            await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther)
                .Because(await FailureEvidenceAsync(changed));
            await Assert.That(changed.Headers.Location?.OriginalString).IsEqualTo(path + "/replication?placement=saved#placement");
        }
        await Assert.That(await RegionsAsync()).IsEqualTo("SFO,GRU");
        using (var credentials = await replica.GetAsync("/api-keys"))
            await Assert.That(credentials.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because(await FailureEvidenceAsync(credentials));
        Volatile.Write(ref report, "measured");
        var overview = await PageAsync(replica, "?range=1h");
        await Assert.That(overview.Contains("Per database and region", StringComparison.Ordinal)).IsFalse()
            .Because("replication measurements render only on the replication tab");
        var identityRow = Regex.Match(overview, "<div class=\"ts-db-identity\">.*?</header>", RegexOptions.Singleline).Value;
        await Assert.That(identityRow).Contains("flags/br.svg");
        await Assert.That(identityRow.IndexOf("data-region=\"gru\"", StringComparison.Ordinal)).IsLessThan(identityRow.IndexOf("data-region=\"sfo\"", StringComparison.Ordinal));
        var measured = await PageAsync(replica, "/replication?range=1h");
        await Assert.That(Regex.IsMatch(measured, "aria-current=\"page\"[^>]*><span>Replication</span>")).IsTrue();
        await Assert.That(measured).Contains("Per database and region · 1h · TerraBase replication_bytes meter");
        await Assert.That(measured).Contains("aria-label=\"Replication totals\"");
        await Assert.That(measured).Contains("<dd class=\"ts-stat-value\">20</dd>");
        await Assert.That(measured).Contains("111 B");
        await Assert.That(measured).Contains("100 B");
        await Assert.That(measured).Contains("<span class=\"ts-mono\">sfo</span></span></td>");
        await Assert.That(measured).Contains("<span class=\"ts-mono\">gru</span></span></td>");
        await Assert.That(measured).Contains("<span class=\"ts-mono\">sfo</span></span><span aria-hidden=\"true\">→</span>");
        await Assert.That(measured).Contains(">True</td>");
        await Assert.That(measured).Contains("diverged");
        await Assert.That(measured).Contains("Not reported");
        await Assert.That(measured.Contains("No replication bandwidth samples for this period.", StringComparison.Ordinal)).IsFalse();
        await Assert.That(measured.Contains("0 ms", StringComparison.Ordinal)).IsFalse();
        Volatile.Write(ref report, "foreign");
        var unknown = await PageAsync(replica, "/replication");
        await Assert.That(unknown).Contains("Replication unavailable");
        await Assert.That(unknown.Contains("aria-label=\"Replication totals\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(unknown.Contains("111 B", StringComparison.Ordinal)).IsFalse();
        Volatile.Write(ref report, "");
        using (var removed = await ChangeRegionsAsync(["SFO"]))
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(await RegionsAsync()).IsEqualTo("SFO,GRU");
        using (var revoked = await producer.PostAsJsonAsync("/__test/control", new { command = "role", value = "member", organizationId }))
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using (var refused = await ChangeRegionsAsync(["SFO", "GRU"]))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await RegionsAsync()).IsEqualTo("SFO,GRU");
        var memberDanger = await PageAsync(client, "/danger?delete=1");
        await Assert.That(memberDanger).Contains("Only organization members who can manage databases can delete this database.");
        await Assert.That(memberDanger.Contains("id=\"delete-database\"", StringComparison.Ordinal)).IsFalse();
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/databases/delete")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = "native-placement" }) })
        {
            request.Headers.Accept.ParseAdd("text/html");
            using var refused = await client.SendAsync(request);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            var html = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
            await Assert.That(html).Contains("Organization management access is required.");
            await Assert.That(html).Contains("role=\"alert\"");
            await Assert.That(html.Contains("id=\"delete-database\"", StringComparison.Ordinal)).IsFalse();
        }
        using (var restored = await producer.PostAsJsonAsync("/__test/control", new { command = "role", value = "owner", organizationId }))
            await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using (var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
        { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = "native-placement" }))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await ReadAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);

        async Task Control(object command)
        {
            using var response = await producer.PostAsJsonAsync("/__test/control", command);
            await Assert.That(response.IsSuccessStatusCode).IsTrue();
        }

        async Task<HttpResponseMessage> ChangeRegionsAsync(string[] regions)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/databases/regions");
            request.Headers.Accept.ParseAdd("text/html");
            request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("_csrf", csrf), new("databaseId", databaseId) }
                .Concat(regions.Select(region => new KeyValuePair<string, string>("region", region))));
            return await client.SendAsync(request);
        }
        async Task<string> RegionsAsync()
        {
            var databases = await ManagementListJourney.ReadListAllAsync(replica, "/api/v1/dashboard/databases");
            var database = databases.EnumerateArray().Single(value => value.GetProperty("id").GetString() == databaseId);
            await Assert.That(database.GetProperty("region").GetString()).IsEqualTo("SFO");
            return string.Join(',', database.GetProperty("regions").EnumerateArray().Select(region => region.GetString()));
        }
        async Task<string> PageAsync(HttpClient instance, string query = "")
        {
            using var response = await instance.GetAsync(path + query);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        }
        async Task<string> FailureEvidenceAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            var observations = await ReadAsync(producer, "/__test/observations");
            return "Native response: " + body[..Math.Min(body.Length, 1200)] + "; producer failures: " + observations.GetProperty("failures").GetRawText();
        }
    }
    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    { using var value = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return value.RootElement.Clone(); }
    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK); return await BodyAsync(response); }
}
