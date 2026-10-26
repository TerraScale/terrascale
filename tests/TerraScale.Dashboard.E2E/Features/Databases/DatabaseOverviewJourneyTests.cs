using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseOverviewJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("unavailable")]
    [Arguments("invalid")]
    [Arguments("duplicate")]
    [Arguments("overflow")]
    public async Task ProviderCountFailuresRemainUnknownAndRecoverAcrossReplicas(string failure)
    {
        var measurement = "";
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            if (http.Request.Method != "GET" || !http.Request.Path.Value!.StartsWith("/v1/projects/", StringComparison.Ordinal) ||
                !http.Request.Path.Value.EndsWith("/schema", StringComparison.Ordinal) || Volatile.Read(ref measurement).Length == 0)
                return Task.FromResult<IResult?>(null);
            return Task.FromResult<IResult?>(Volatile.Read(ref measurement) switch
            {
                "unavailable" => Results.Json(new { error = "unavailable" }, statusCode: 503),
                "duplicate" => Results.Json(new { items = new[] { new { name = "users", documents = 1, bytes = 0 }, new { name = "users", documents = 1, bytes = 0 } } }),
                "overflow" => Results.Json(new { items = new[] { new { name = "users", documents = long.MaxValue, bytes = 0 }, new { name = "orders", documents = 1L, bytes = 0 } } }),
                _ => Results.Json(new { items = new[] { new { name = "users", documents = -1, bytes = 0 } } })
            });
        });
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var replicaLogin = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
        await Assert.That(replicaLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "measured-overview", ["region"] = "GRU" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = database.RootElement.GetProperty("id").GetString()!;
        var secret = await HttpJourney.CreatePatAsync(client, "Measured overview", account.Password);
        var path = "/databases/" + id;
        var known = await PageAsync(client);
        await Assert.That(Counts(known)).IsEquivalentTo(new[] { "0", "0" });
        var placement = await PageAsync(client, "/replication");
        await Assert.That(placement).Contains("gru · São Paulo is the home region and must remain selected.");
        await Assert.That(placement).Contains("<dt>Configured regions</dt><dd>gru · São Paulo, sfo · San Francisco</dd>");
        var home = Regex.Match(placement, "<input[^>]+type=\"checkbox\"[^>]+value=\"GRU\"[^>]*>").Value;
        await Assert.That(home).Contains("required");
        await Assert.That(home.Contains("disabled", StringComparison.Ordinal)).IsFalse();
        var knownTool = await DetailAsync();
        foreach (var field in new[] { "collections", "documents", "bytes" })
            await Assert.That(knownTool.GetProperty(field).GetInt64()).IsEqualTo(0);
        Volatile.Write(ref measurement, failure);
        var unknown = await PageAsync(replica);
        await Assert.That(Counts(unknown)).IsEquivalentTo(new[] { "—", "—" });
        await Assert.That(unknown).Contains("Database counts are unavailable.");
        await Assert.That(Summary(unknown)).Contains("Not reported by provider");
        await Assert.That(Summary(unknown).Contains("0 B across all collections", StringComparison.Ordinal)).IsFalse();
        var unknownTool = await DetailAsync();
        foreach (var field in new[] { "collections", "documents", "bytes" })
            await Assert.That(unknownTool.TryGetProperty(field, out _)).IsFalse()
                .Because("MCP cannot publish invented zero, invalid negative or overflowed storage measurements.");
        await Assert.That(unknownTool.GetProperty("unavailable").GetString()).Contains("Database storage");
        Volatile.Write(ref measurement, "");
        await Assert.That(Counts(await PageAsync(replica))).IsEquivalentTo(new[] { "0", "0" });
        await Assert.That((await DetailAsync()).GetProperty("documents").GetInt64()).IsEqualTo(0);
        var overview = await PageAsync(client);
        await Assert.That(overview.Contains("id=\"delete-database\"", StringComparison.Ordinal)).IsFalse()
            .Because("the delete dialog lives on the danger tab");
        await Assert.That(overview.Contains("aria-label=\"Replication totals\"", StringComparison.Ordinal)).IsFalse();
        using (var legacy = await client.GetAsync(path + "?delete=1"))
        {
            await Assert.That(legacy.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(legacy.Headers.Location?.OriginalString).IsEqualTo(path + "/danger?delete=1");
        }
        await Assert.That(await PageAsync(client, "/danger?delete=1")).Contains("data-init=\"el.showModal()\"");
        var danger = await PageAsync(client, "/danger");
        await Assert.That(Regex.IsMatch(danger, "aria-current=\"page\"[^>]*><span>Danger zone</span>")).IsTrue();
        await Assert.That(danger).Contains("data-init=\"void 0\"");
        using (var refused = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/databases/delete")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["confirmName"] = "wrong-name" }) })
        {
            refused.Headers.Accept.ParseAdd("text/html");
            refused.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            using var response = await client.SendAsync(refused);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var reopened = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            await Assert.That(Regex.IsMatch(reopened, "aria-current=\"page\"[^>]*><span>Danger zone</span>")).IsTrue();
            await Assert.That(reopened).Contains("data-init=\"el.showModal()\"");
            await Assert.That(Regex.IsMatch(reopened, "<dialog id=\"delete-database\".*role=\"alert\"", RegexOptions.Singleline)).IsTrue()
                .Because("the delete error renders inside the reopened dialog");
        }
        using var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["confirmName"] = "measured-overview" });
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        async Task<string> PageAsync(HttpClient instance, string suffix = "")
        {
            using var response = await instance.GetAsync(path + suffix);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        }
        async Task<JsonElement> DetailAsync()
        {
            var result = (await HttpJourney.McpAsync(replica, secret, "tools/call", new
            { name = "get_database", arguments = new { databaseId = id } })).GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse();
            return result.GetProperty("structuredContent").Clone();
        }
    }

    private static string Summary(string html) => Regex.Match(html, "<dl class=\"ts-stats\" aria-label=\"Database summary\">(.*?)</dl>", RegexOptions.Singleline).Groups[1].Value;
    private static string[] Counts(string html) => Regex.Matches(Summary(html), "<dd class=\"ts-stat-value\">([^<]*)</dd>").Take(2).Select(match => match.Groups[1].Value).ToArray();
}
