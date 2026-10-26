using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseAllRegionsJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task AllRegionsPlacementIsChosenShownAndChangedThroughTheFormsAndMcp()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var identity = await ReadAsync(client, "/api/v1/auth/me");
        var organizationId = identity.GetProperty("organizationId").GetString()!;
        var projectId = identity.GetProperty("projectId").GetString()!;
        await Control(new { command = "api-region", value = "sfo" });
        // TerraBase's /v1/regions catalog lists every serving region, including one this dashboard never named before.
        await Control(new { command = "catalog", value = new[] { "fra", "gru", "sfo" } });
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        var list = WebUtility.HtmlDecode(await (await client.GetAsync("/databases")).Content.ReadAsStringAsync());
        await Assert.That(list).Contains("All regions: copies follow new regions automatically");
        await Assert.That(list).Contains("data-attr:disabled=\"$_allRegions\"");
        foreach (var code in new[] { "FRA", "GRU", "SFO" })
            await Assert.That(Regex.IsMatch(list, "<input type=\"checkbox\" name=\"region\" value=\"" + code + "\"")).IsTrue().Because("New database offers " + code);

        var secret = await HttpJourney.CreatePatAsync(client, "All regions MCP", account.Password);
        // Both choices together are refused before anything is provisioned.
        await Assert.That((await RpcAsync("create_database", new() { ["name"] = "conflict", ["regions"] = new[] { "SFO", "GRU" }, ["allRegions"] = true })).GetRawText())
            .Contains("not both");
        await Assert.That((await RpcAsync("create_database", new() { ["name"] = "single", ["regions"] = new[] { "SFO" } })).GetRawText())
            .Contains("Choose at least two regions");
        var viaMcp = await CallAsync("create_database", new() { ["name"] = "mcp-everywhere", ["region"] = "SFO", ["allRegions"] = true });
        await Assert.That(viaMcp.GetProperty("allRegions").GetBoolean()).IsTrue();
        var mcpId = viaMcp.GetProperty("id").GetString()!;
        await CallAsync("delete_database", new() { ["databaseId"] = mcpId, ["confirmName"] = "mcp-everywhere" });

        var databaseId = "";
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "everywhere", ["homeRegion"] = "SFO", ["allRegions"] = "true" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
            var body = await BodyAsync(created);
            databaseId = body.GetProperty("id").GetString()!;
            await Assert.That(body.GetProperty("allRegions").GetBoolean()).IsTrue();
        }
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/databases")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["name"] = "everywhere", ["homeRegion"] = "SFO", ["allRegions"] = "true" }) })
        {
            request.Headers.Accept.ParseAdd("text/html");
            using var duplicate = await client.SendAsync(request);
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            var rejected = WebUtility.HtmlDecode(await duplicate.Content.ReadAsStringAsync());
            await Assert.That(rejected).Contains("name=\"name\" value=\"everywhere\"");
            await Assert.That(rejected).Contains("_allRegions: true");
            await Assert.That(Regex.IsMatch(rejected, "<option value=\"SFO\" selected(?:[ >]|=)")).IsTrue();
            await Assert.That(IsChecked(Regex.Match(rejected, "<input[^>]+name=\"allRegions\"[^>]*>").Value)).IsTrue();
            await Assert.That(Regex.IsMatch(rejected, "<fieldset class=\"ts-field ts-db-regions\"[^>]* disabled(?:[ >]|=)")).IsTrue();
        }
        var page = await PageAsync("?created=1");
        await Assert.That(page).Contains("<span class=\"ts-badge ts-badge-info\">All regions</span>");
        await Assert.That(page).Contains("It serves sfo · San Francisco, gru · São Paulo, fra · Frankfurt.");
        await Assert.That(page).Contains("data-region=\"fra\"");
        var placement = await PageAsync("/replication");
        await Assert.That(Regex.Match(placement, "<input[^>]+name=\"allRegions\"[^>]*>").Value).Contains("checked");
        await Assert.That(Regex.Match(placement, "<fieldset class=\"ts-field ts-db-regions\"[^>]*>").Value).Contains("disabled");
        await Assert.That(await AllRegionsAsync()).IsTrue();

        // Choosing regions turns all regions off; checking it again turns it back on.
        using (var regions = await ChangeAsync(["SFO", "GRU"], allRegions: false))
            await Assert.That(regions.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(await AllRegionsAsync()).IsFalse();
        await Assert.That((await PageAsync("")).Contains(">All regions</span>", StringComparison.Ordinal)).IsFalse();
        // A removed region can be chosen again: the catalog still lists it.
        using (var readded = await ChangeAsync(["SFO", "FRA"], allRegions: false))
        {
            await Assert.That(readded.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await readded.Content.ReadAsStringAsync());
            await Assert.That(readded.Headers.Location?.OriginalString).IsEqualTo("/databases/" + databaseId + "/replication?placement=saved#placement");
            await Assert.That(await PageAsync("/replication?placement=saved")).Contains("Placement saved.");
        }
        // The rejected draft differs from the saved regions and policy; only the form may show it.
        using (var conflict = await ChangeAsync(["sfo", "gru"], allRegions: true))
        {
            await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var rejected = WebUtility.HtmlDecode(await conflict.Content.ReadAsStringAsync());
            await Assert.That(IsChecked(Regex.Match(rejected, "<input[^>]+name=\"allRegions\"[^>]*>").Value)).IsTrue();
            await Assert.That(rejected).Contains("_allRegions: true, _regionCount: 2");
            await Assert.That(Regex.IsMatch(rejected, "<fieldset class=\"ts-field ts-db-regions\"[^>]* disabled(?:[ >]|=)")).IsTrue();
            await Assert.That(RegionInput(rejected, "GRU")).Contains("checked");
            await Assert.That(RegionInput(rejected, "FRA").Contains("checked", StringComparison.Ordinal)).IsFalse();
            await Assert.That(rejected).Contains("<dt>Configured regions</dt><dd>sfo · San Francisco, fra · Frankfurt</dd>");
            await Assert.That(rejected).Contains("<dt>Placement policy</dt><dd>Explicit regions</dd>");
            await Assert.That(rejected.Contains("Placement saved.", StringComparison.Ordinal)).IsFalse();
        }
        await Assert.That(await AllRegionsAsync()).IsFalse();
        using (var invalid = await ChangeAsync(["GRU"], allRegions: false))
        {
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var rejected = WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync());
            await Assert.That(RegionInput(rejected, "SFO").Contains("checked", StringComparison.Ordinal)).IsFalse();
            await Assert.That(RegionInput(rejected, "GRU")).Contains("checked");
            await Assert.That(IsChecked(Regex.Match(rejected, "<input[^>]+name=\"allRegions\"[^>]*>").Value)).IsFalse();
            await Assert.That(Regex.IsMatch(rejected, "<fieldset class=\"ts-field ts-db-regions\"[^>]* disabled(?:[ >]|=)")).IsFalse();
        }
        using (var explicitEverywhere = await ChangeAsync(["SFO", "GRU", "FRA"], allRegions: false))
            await Assert.That(explicitEverywhere.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var allThree = await PageAsync("/replication");
        await Assert.That(allThree).Contains("Add a region to replicate the database there; remove one to stop serving it there.");
        await Assert.That(allThree.Contains("Additional regions are unavailable here.", StringComparison.Ordinal)).IsFalse();
        await Assert.That(allThree.Contains("Placement lock", StringComparison.Ordinal)).IsFalse();
        await Assert.That(allThree.Contains("Placement status", StringComparison.Ordinal)).IsFalse();
        await Assert.That(allThree.Contains("<dt>Generation</dt>", StringComparison.Ordinal)).IsFalse();
        using (var everywhere = await ChangeAsync([], allRegions: true))
            await Assert.That(everywhere.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(await AllRegionsAsync()).IsTrue();
        using (var invalid = await ChangeAsync(["GRU"], allRegions: false))
        {
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var rejected = WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync());
            await Assert.That(rejected).Contains("_allRegions: false, _regionCount: 1");
            await Assert.That(IsChecked(Regex.Match(rejected, "<input[^>]+name=\"allRegions\"[^>]*>").Value)).IsFalse();
            await Assert.That(Regex.IsMatch(rejected, "<fieldset class=\"ts-field ts-db-regions\"[^>]* disabled(?:[ >]|=)")).IsFalse();
            await Assert.That(rejected).Contains("<dt>Placement policy</dt><dd>All serving regions, including regions added later</dd>");
        }
        using (var conflict = await ChangeAsync(["SFO", "GRU"], allRegions: true))
            await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(await AllRegionsAsync()).IsTrue();

        var listed = await CallAsync("list_databases", new());
        await Assert.That(listed.GetProperty("databases").EnumerateArray().Single(db => db.GetProperty("id").GetString() == databaseId)
            .GetProperty("allRegions").GetBoolean()).IsTrue();
        await Assert.That((await CallAsync("get_database", new() { ["databaseId"] = databaseId })).GetProperty("allRegions").GetBoolean()).IsTrue();
        var narrowed = await CallAsync("set_database_regions", new() { ["databaseId"] = databaseId, ["regions"] = new[] { "SFO", "GRU" } });
        await Assert.That(narrowed.GetProperty("allRegions").GetBoolean()).IsFalse();
        var widened = await CallAsync("set_database_regions", new() { ["databaseId"] = databaseId, ["allRegions"] = true });
        await Assert.That(widened.GetProperty("allRegions").GetBoolean()).IsTrue();

        // Placement stays owner/admin-only.
        using (var demoted = await producer.PostAsJsonAsync("/__test/control", new { command = "role", value = "member", organizationId }))
            await Assert.That(demoted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using (var member = await ChangeAsync(["SFO"], allRegions: false))
            await Assert.That(member.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await RpcAsync("set_database_regions", new() { ["databaseId"] = databaseId, ["regions"] = new[] { "SFO" } })).GetRawText())
            .Contains("Organization management access is required.");
        await Assert.That(await AllRegionsAsync()).IsTrue();
        await Assert.That((await ReadAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);

        async Task Control(object command)
        {
            using var response = await producer.PostAsJsonAsync("/__test/control", command);
            await Assert.That(response.IsSuccessStatusCode).IsTrue();
        }
        async Task<HttpResponseMessage> ChangeAsync(string[] regions, bool allRegions)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/databases/regions");
            request.Headers.Accept.ParseAdd("text/html");
            request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("_csrf", csrf), new("databaseId", databaseId) }
                .Concat(regions.Select(region => new KeyValuePair<string, string>("region", region)))
                .Concat(allRegions ? [new("allRegions", "true")] : []));
            return await client.SendAsync(request);
        }
        async Task<bool> AllRegionsAsync() => (await ReadAsync(client, "/api/v1/dashboard/databases")).EnumerateArray()
            .Single(value => value.GetProperty("id").GetString() == databaseId).GetProperty("allRegions").GetBoolean();
        static bool IsChecked(string input) => Regex.IsMatch(input, @"\schecked(?:[ >]|=)");
        static string RegionInput(string html, string code) => Regex.Match(html, "<input[^>]+name=\"region\"[^>]+value=\"" + code + "\"[^>]*>").Value;
        async Task<string> PageAsync(string tab)
        {
            using var response = await client.GetAsync("/databases/" + databaseId + tab);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        }
        async Task<JsonElement> RpcAsync(string name, Dictionary<string, object?> arguments)
        {
            arguments.TryAdd("organizationId", organizationId);
            arguments.TryAdd("projectId", projectId);
            return await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
        }
        async Task<JsonElement> CallAsync(string name, Dictionary<string, object?> arguments)
        {
            var result = (await RpcAsync(name, arguments)).GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(result.GetRawText());
            return result.GetProperty("structuredContent");
        }
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    { using var value = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return value.RootElement.Clone(); }
    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK); return await BodyAsync(response); }
}
