using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardRootFixture>]
public sealed class SqlEditorCorrectnessJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments('+', 12000, false)]
    [Arguments('+', 16375, false)]
    [Arguments('+', 16376, true)]
    [Arguments('é', 8187, false)]
    [Arguments('é', 8188, true)]
    public async Task EncodedFormsApplyTheDecodedSqlLimit(char character, int characters, bool invalid)
    {
        var executions = 0;
        var sql = "SELECT '" + new string(character, characters) + "'";
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (http.Request.Method != "POST" || !http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal)) return null;
            if (await DatabaseSdkScenario.EmptySqlCatalogAsync(http) is { } catalog) return catalog;
            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            await Assert.That(body.RootElement.GetProperty("statements")[0].GetProperty("sql").GetString()).IsEqualTo(sql);
            Interlocked.Increment(ref executions);
            return Results.Json(new { results = new[] { new { columns = Array.Empty<object>(), rows = Array.Empty<object>(), rows_affected = 0 } } });
        });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "sql-size");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/sql");
        foreach (var action in new[] { "sql/check", "sql", "saved-queries" })
        {
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/" + action, new()
            {
                ["_csrf"] = csrf,
                ["databaseId"] = id,
                ["sql"] = sql,
                ["name"] = "Encoded query",
                ["history"] = "[\"" + new string('+', 4000) + "\"]"
            });
            await Assert.That(response.StatusCode).IsEqualTo(action == "sql/check" ? HttpStatusCode.OK : invalid ? HttpStatusCode.UnprocessableEntity : action == "saved-queries" ? HttpStatusCode.Created : HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            if (invalid) await Assert.That(body).Contains("SQL must be 16 KiB or less.");
            else if (action == "sql/check") await Assert.That(body).IsEqualTo("[]");
        }
        await Assert.That(Volatile.Read(ref executions)).IsEqualTo(invalid ? 0 : 1);
    }

    [Test]
    [Arguments("")]
    [Arguments("items")]
    public async Task SavedQueryLinksLoadTheQueryAndUseUlids(string collection)
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "sql-saved");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/sql");
        const string sql = "SELECT 'saved & loaded'";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/saved-queries")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = collection, ["name"] = "Saved query", ["sql"] = sql }) };
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var location = response.Headers.Location!.OriginalString;
        var query = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, location).Query);
        var savedId = query["savedQueryId"].ToString();
        await Assert.That(TerraScaleIds.IsValid(savedId)).IsTrue();
        using var page = await client.GetAsync(location);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        var link = WebUtility.HtmlDecode(Regex.Match(html, "class=\"ts-data-query\" href=\"([^\"]+)\"", RegexOptions.CultureInvariant).Groups[1].Value);
        await Assert.That(link).IsEqualTo(location);
        using var loaded = await client.GetAsync(link);
        await Assert.That(loaded.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var submitted = Regex.Match(await loaded.Content.ReadAsStringAsync(), "<textarea[^>]*id=\"sql\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Groups[1].Value;
        await Assert.That(WebUtility.HtmlDecode(submitted)).IsEqualTo(sql);
        using var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/saved-queries/delete", new() { ["_csrf"] = csrf, ["databaseId"] = id, ["id"] = savedId });
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task DatabaseSavedQueriesStayInTheirDatabaseAndProjectTemplatesRemainVisible()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var producer = scenario.CreateProviderClient();
        using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        var organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
        using (var admission = await producer.PostAsJsonAsync("/__test/control", new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId }))
            await Assert.That(admission.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var origin = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "query-origin");
        var other = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "query-other");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + origin + "/sql");
        using var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/saved-queries", new()
        { ["_csrf"] = csrf, ["databaseId"] = origin, ["name"] = "Origin-only statement", ["sql"] = "SELECT 1234567" });
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var query = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        var queryId = query.RootElement.GetProperty("id").GetString();
        using var template = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/saved-queries", new()
        { ["_csrf"] = csrf, ["databaseId"] = "", ["name"] = "Project template", ["sql"] = "SELECT 2" });
        await Assert.That(template.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var originHtml = await client.GetStringAsync("/databases/" + origin + "/sql");
        var otherHtml = await client.GetStringAsync("/databases/" + other + "/sql?savedQueryId=" + queryId);
        await Assert.That(originHtml).Contains("Origin-only statement").And.Contains("Project template");
        await Assert.That(otherHtml).DoesNotContain("Origin-only statement").And.DoesNotContain("SELECT 1234567").And.Contains("Project template");
    }

    [Test]
    [Arguments("WITH data AS (SELECT 1 AS replace) SELECT replace FROM data", false)]
    [Arguments("WITH data(replace) AS NOT MATERIALIZED (SELECT 1), more AS (SELECT * FROM data) SELECT * FROM more", false)]
    [Arguments("WITH RECURSIVE data(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM data WHERE n<3) SELECT * FROM data", false)]
    [Arguments("PRAGMA main.table_info('items')", false)]
    [Arguments("PRAGMA index_list(items)", false)]
    [Arguments("PRAGMA read_uncommitted", false)]
    [Arguments("WITH data AS (SELECT 1) UPDATE items SET value=1", true)]
    [Arguments("PRAGMA user_version = 1", true)]
    [Arguments("PRAGMA foreign_keys = OFF", true)]
    [Arguments("CREATE TABLE audit (id TEXT)", true)]
    public async Task ReadsUseReadGrantsAndViewersCannotExecuteWrites(string sql, bool write)
    {
        TestAuth auth = null!;
        var executions = 0;
        var scope = "";
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), async http =>
        {
            if (http.Request.Method != "POST" || !http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal)) return null;
            if (await DatabaseSdkScenario.EmptySqlCatalogAsync(http) is { } catalog) return catalog;
            scope = string.Join(',', auth.ResolveProjectToken(http.Request.Headers.Authorization.ToString()[7..])!.Value.Scopes.Order(StringComparer.Ordinal));
            Interlocked.Increment(ref executions);
            return Results.Json(new { results = new[] { new { columns = Array.Empty<object>(), rows = Array.Empty<object>(), rows_affected = 0 } } });
        });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var id = await PagePaymentAudit.Oct13.DatabaseDataAccess.DatabaseDataAccessJourneyTests.CreateDatabaseAsync(client, "sql-grants");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/sql");
        var fields = new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["sql"] = sql };
        using var owner = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql", fields);
        await Assert.That(owner.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(scope).IsEqualTo(write ? "documents:read,documents:write" : "documents:read");
        using var controls = scenario.CreateProviderClient();
        using var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "viewer" });
        role.EnsureSuccessStatusCode();
        using var viewer = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql", fields);
        await Assert.That(viewer.StatusCode).IsEqualTo(write ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        await Assert.That(Volatile.Read(ref executions)).IsEqualTo(write ? 1 : 2);
    }
}
