using System.Net;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseSqlJourneyTests(DashboardRootFixture fixture)
    {
        // Create a database, run CREATE, INSERT and SELECT in the SQL editor, then see the row on the Tables tab and the Overview.
        [Test]
        public async Task SqlEditorWritesShowInTablesAndOverview()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var id = await HttpJourney.CreateDatabaseAsync(client, "sql-journey");
            var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/sql");
            async Task<string> RunAsync(string sql)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/sql")
                { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["sql"] = sql }) };
                request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
                request.Headers.Accept.ParseAdd("text/html");
                using var posted = await client.SendAsync(request);
                await Assert.That(posted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                using var page = await client.GetAsync(posted.Headers.Location);
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
                await Assert.That(html).DoesNotContain("Query failed");
                return html;
            }

            await RunAsync("CREATE TABLE people (id TEXT PRIMARY KEY, name TEXT NOT NULL)");
            await RunAsync("INSERT INTO people (id, name) VALUES ('01K78D0C000000000000000020', 'Ada')");
            var selected = await RunAsync("SELECT id, name FROM people");
            await Assert.That(selected).Contains("<td data-kind=\"string\">Ada</td>");

            using var tables = await client.GetAsync("/databases/" + id + "/tables");
            await Assert.That(tables.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var listing = WebUtility.HtmlDecode(await tables.Content.ReadAsStringAsync());
            await Assert.That(listing).Contains("data-table=\"people\"");
            await Assert.That(listing).Contains("<td data-kind=\"string\">Ada</td>");

            using var overview = await client.GetAsync("/databases/" + id);
            var summary = await overview.Content.ReadAsStringAsync();
            await Assert.That(summary).Contains("Tables</dt><dd class=\"ts-stat-value\">1</dd>");
            await Assert.That(summary).Contains("id=\"db-connect-psql\"");
        }
    }
}
