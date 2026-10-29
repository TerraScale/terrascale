using System.Net;

namespace TerraScale.Dashboard.E2E.Features.QuerySubscriptions;

[ClassDataSource<DashboardE2EFixture>]
public sealed class QuerySubscriptionJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task SavedConfigurationListingIsScopedAndMutationSurfacesAreAbsent()
    {
        using var client = fixture.CreateClient();
        using var anonymous = await client.GetAsync("/subscriptions");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(anonymous.Headers.Location!.OriginalString).IsEqualTo("/login?returnTo=%2Fsubscriptions");
        using var anonymousWrite = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/subscriptions/attach", new());
        await Assert.That(anonymousWrite.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var page = await client.GetAsync("/subscriptions");
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("Query subscriptions");
        await Assert.That(html).Contains("TerraBase does not deliver them.");
        await Assert.That(html).Contains("No shared-result configurations yet.");
        await Assert.That(html.Contains("action=\"/api/v1/dashboard/subscriptions/attach\"", StringComparison.Ordinal)).IsFalse();
        using var head = await client.SendAsync(new(HttpMethod.Head, "/subscriptions"));
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await head.Content.ReadAsStringAsync()).IsEqualTo("");
        foreach (var action in new[] { "attach", "pause", "resume", "detach" })
        {
            await Assert.That(html.Contains("/api/v1/dashboard/subscriptions/" + action, StringComparison.Ordinal)).IsFalse();
            using var removed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/subscriptions/" + action, new());
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        var token = await HttpJourney.CreatePatAsync(client, "TUnit shared results");
        var listing = await HttpJourney.McpAsync(client, token, "tools/call", new { name = "list_query_subscriptions", arguments = new { } });
        await Assert.That(listing.GetProperty("result").GetProperty("structuredContent").GetProperty("subscriptions").GetArrayLength()).IsEqualTo(0);
        var foreign = await HttpJourney.McpAsync(client, token, "tools/call", new { name = "list_query_subscriptions", arguments = new { projectId = "01JA0000000000000000000001" } });
        await Assert.That(foreign.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
        var discovery = await HttpJourney.McpAsync(client, token, "tools/list", new { });
        var tools = discovery.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToArray();
        await Assert.That(tools.Contains("list_query_subscriptions")).IsTrue();
        foreach (var name in new[] { "attach_query_subscription", "pause_query_subscription", "resume_query_subscription", "detach_query_subscription" })
            await Assert.That(tools.Contains(name)).IsFalse();
        var unchanged = await HttpJourney.McpAsync(client, token, "tools/call", new { name = "list_query_subscriptions", arguments = new { } });
        await Assert.That(unchanged.GetProperty("result").GetProperty("structuredContent").GetProperty("subscriptions").GetArrayLength()).IsEqualTo(0);
    }
}
