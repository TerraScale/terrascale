using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class SqlCatalogJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("ok")]
        [Arguments("empty")]
        [Arguments("cap")]
        [Arguments("quotes")]
        [Arguments("no-type")]
        [Arguments("denied")]
        [Arguments("listing-denied")]
        [Arguments("malformed")]
        [Arguments("oversized")]
        [Arguments("missing-columns")]
        [Arguments("missing-result")]
        public async Task TwoReadOnlySqlRequestsBoundCatalogAndNeverChangeRunOutcome(string answer)
        {
            TestAuth auth = null!;
            var catalogs = 0; var batches = 0; var executions = 0; var documentReads = 0;
            var names = answer == "empty" ? Array.Empty<string>() : answer == "cap" ? Enumerable.Range(0, 101).Select(index => "table_" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).ToArray() : new[] { answer == "quotes" ? "quote'table" : "empty_table" };
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), async http =>
            {
                var path = http.Request.Path.Value!;
                if (path.Contains("/collections", StringComparison.Ordinal) || path.EndsWith("/schema", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref documentReads);
                }

                if (!path.EndsWith("/sql/execute", StringComparison.Ordinal))
                {
                    return null;
                }

                using var body = await JsonDocument.ParseAsync(http.Request.Body);
                var statements = body.RootElement.GetProperty("statements").EnumerateArray().Select(item => item.GetProperty("sql").GetString()!).ToArray();
                var listing = statements[0] == "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name LIMIT 101";
                if (!listing && !statements[0].StartsWith("PRAGMA table_info(", StringComparison.Ordinal))
                { Interlocked.Increment(ref executions); return Results.Json(new { results = new[] { new { columns = Array.Empty<object>(), rows = Array.Empty<object>(), rows_affected = 1 } } }); }
                if (http.Request.Method != "POST" || http.Request.QueryString.HasValue || http.Request.Headers[TerraScale.TerraBase.Contracts.TerraBaseRpc.PeerCredentialHeader] != TestIdentity.PeerSecret ||
                    !auth.ResolveProjectToken(http.Request.Headers.Authorization.ToString()[7..])!.Value.Scopes.SequenceEqual(new[] { "documents:read" }))
                {
                    throw new InvalidOperationException("SQL metadata changed its current-user read authority or region.");
                }

                if (listing)
                { Interlocked.Increment(ref catalogs); return answer == "listing-denied" ? Results.StatusCode(403) : Results.Json(new { results = new[] { Result(["name"], names.Select(name => new object?[] { name }).ToArray()) } }); }
                Interlocked.Increment(ref batches);
                await Assert.That(statements.SequenceEqual(names.Take(100).Select(name => "PRAGMA table_info('" + name.Replace("'", "''", StringComparison.Ordinal) + "')"))).IsTrue();
                return answer switch
                {
                    "denied" => Results.StatusCode(403),
                    "malformed" => Results.Text("{", "application/json"),
                    "oversized" => Results.Text(new string(' ', 8 * 1024 * 1024 + 1), "application/json"),
                    "missing-result" => Results.Json(new { results = Array.Empty<object>() }),
                    _ => Results.Json(new
                    {
                        results = statements.Select(_ => Result(["cid", "name", "type", "notnull", "dflt_value", "pk"],
                        answer == "missing-columns" ? [] : [new object?[] { 0, "declared_only", answer == "no-type" ? "" : "TEXT", 0, null, 0 }])).ToArray()
                    })
                };
            });
            using var client = scenario.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var id = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "sql-catalog");
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new() { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "documents_only" }))
            {
                collection.EnsureSuccessStatusCode();
            }

            using var controls = scenario.CreateProviderClient();
            using (var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "viewer" }))
            {
                role.EnsureSuccessStatusCode();
            }

            Interlocked.Exchange(ref documentReads, 0);
            using var page = await client.GetAsync("/databases/" + id + "/sql");
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await page.Content.ReadAsStringAsync();
            using var catalog = JsonDocument.Parse(Regex.Match(html, "<script[^>]*id=\"sql-catalog\"[^>]*>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value);
            var available = answer is "ok" or "quotes" or "cap" or "no-type";
            await Assert.That(catalog.RootElement.GetProperty("tables").GetArrayLength()).IsEqualTo(available ? Math.Min(100, names.Length) : 0);
            await Assert.That(catalog.RootElement.GetProperty("truncated").GetBoolean()).IsEqualTo(answer == "cap");
            await Assert.That(html).DoesNotContain("documents_only");
            if (available) { await Assert.That(html).Contains("declared_only"); await Assert.That(html).Contains(answer == "no-type" ? "No declared type" : "TEXT"); }
            else
            {
                await Assert.That(html).Contains(answer == "empty" ? "No SQL tables yet." : "SQL table suggestions are unavailable.");
            }

            if (answer == "cap")
            {
                await Assert.That(html).Contains("Showing the first 100 SQL tables");
            }

            if (answer == "denied") { using var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "owner" }); role.EnsureSuccessStatusCode(); }
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/sql")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["sql"] = answer == "denied" ? "INSERT INTO audit VALUES (1)" : "SELECT 1" }) };
            request.Headers.Accept.ParseAdd("text/html");
            using var posted = await client.SendAsync(request);
            await Assert.That(posted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var run = await client.GetAsync(posted.Headers.Location);
            await Assert.That(run.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var outcome = await run.Content.ReadAsStringAsync();
            await Assert.That(outcome).Contains("id=\"sql-result\"");
            await Assert.That(outcome).DoesNotContain("Query failed");
            await Assert.That(Volatile.Read(ref catalogs)).IsEqualTo(2);
            await Assert.That(Volatile.Read(ref batches)).IsEqualTo(answer is "empty" or "listing-denied" ? 0 : 2);
            await Assert.That(Volatile.Read(ref executions)).IsEqualTo(1);
            await Assert.That(Volatile.Read(ref documentReads)).IsEqualTo(0);
        }

        internal static object Result(string[] fields, object?[][] rows)
        {
            return new
            { columns = fields.Select(name => new { name, decltype = (string?)null }).ToArray(), rows, rows_affected = 0, last_insert_rowid = (long?)null };
        }
    }
}
