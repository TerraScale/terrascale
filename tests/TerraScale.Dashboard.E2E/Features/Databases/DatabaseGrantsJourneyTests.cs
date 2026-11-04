using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed partial class DatabaseGrantsJourneyTests(DashboardRootFixture fixture)
    {
        // Add a table read grant on the Grants tab, see it listed, then remove it. The producer stands in for TerraBase's read-grant routes.
        [Test]
        public async Task GrantIsAddedListedAndRemoved()
        {
            var grants = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
            {
                var path = http.Request.Path.Value!;
                if (path.EndsWith("/read-grants", StringComparison.Ordinal) && http.Request.Method == "GET")
                {
                    return Results.Json(new { grants = grants.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value).ToArray() });
                }

                if (GrantPath().Match(path) is not { Success: true } match)
                {
                    return null;
                }

                var table = match.Groups[1].Value;
                if (http.Request.Method == "DELETE")
                {
                    return grants.TryRemove(table, out _) ? Results.NoContent() : Results.Json(new { error = new { code = "no_read_grant", message = "the table has no read grant" } }, statusCode: 404);
                }

                using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                var where = body.RootElement.TryGetProperty("where", out var w) ? w.GetString() : null;
                var columns = body.RootElement.TryGetProperty("columns", out var c) ? c.EnumerateArray().Select(column => column.GetString()!).ToArray() : null;
                var grant = new { table, where, columns, updatedAt = DateTimeOffset.UtcNow };
                grants[table] = grant;
                return Results.Json(grant);
            });
            using var client = scenario.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var id = await HttpJourney.CreateDatabaseAsync(client, "grants-journey");
            var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/grants");

            using (var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/grants", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["table"] = "posts", ["where"] = "owner_id = :sub", ["columns"] = "id, body" }))
            {
                await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await saved.Content.ReadAsStringAsync());
            }

            var listed = WebUtility.HtmlDecode(await client.GetStringAsync("/databases/" + id + "/grants"));
            await Assert.That(listed).Contains("data-grant=\"posts\"");
            await Assert.That(listed).Contains("owner_id = :sub");
            await Assert.That(listed).Contains("id, body");

            using (var removed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/grants/delete", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["table"] = "posts" }))
            {
                await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await removed.Content.ReadAsStringAsync());
            }

            await Assert.That(grants.IsEmpty).IsTrue();
            await Assert.That(await client.GetStringAsync("/databases/" + id + "/grants")).DoesNotContain("data-grant=\"posts\"");
        }

        [GeneratedRegex("/tables/([A-Za-z0-9_]+)/read-grant$", RegexOptions.CultureInvariant)]
        private static partial Regex GrantPath();
    }
}
