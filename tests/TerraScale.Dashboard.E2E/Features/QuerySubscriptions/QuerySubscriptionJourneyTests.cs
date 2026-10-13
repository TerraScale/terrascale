using System.Net;

namespace TerraScale.Dashboard.E2E.Features.QuerySubscriptions;

[ClassDataSource<DashboardE2EFixture>]
public sealed class QuerySubscriptionJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task SavedConfigurationMetadataIsScopedAndUnavailableDeliveryCannotBeFaked()
    {
        using var client = fixture.CreateClient();
        using var anonymous = await client.GetAsync("/subscriptions");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(anonymous.Headers.Location!.OriginalString).IsEqualTo("/login?returnTo=%2Fsubscriptions");
        using var anonymousWrite = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/subscriptions/attach", new());
        await Assert.That(anonymousWrite.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
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
        var csrf = await HttpJourney.CsrfAsync(client, "/subscriptions");
        foreach (var action in new[] { "attach", "pause", "resume", "detach" })
        {
            using var noCsrf = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/subscriptions/" + action, new());
            await Assert.That(noCsrf.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            using var unavailable = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/subscriptions/" + action, new() { ["_csrf"] = csrf });
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await unavailable.Content.ReadAsStringAsync()).IsEqualTo("Live shared-result configuration is unavailable.\n");
        }
        var token = await HttpJourney.CreatePatAsync(client, "TUnit shared results");
        var listing = await HttpJourney.McpAsync(client, token, "tools/call", new { name = "list_query_subscriptions", arguments = new { } });
        await Assert.That(listing.GetProperty("result").GetProperty("structuredContent").GetProperty("subscriptions").GetArrayLength()).IsEqualTo(0);
        var foreign = await HttpJourney.McpAsync(client, token, "tools/call", new { name = "list_query_subscriptions", arguments = new { projectId = "foreign-project" } });
        await Assert.That(foreign.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
        foreach (var name in new[] { "attach_query_subscription", "pause_query_subscription", "resume_query_subscription", "detach_query_subscription" })
        {
            var arguments = name.StartsWith("attach", StringComparison.Ordinal)
                ? new Dictionary<string, string> { ["databaseId"] = "foreign-database", ["name"] = "shared", ["query"] = "SELECT * FROM documents", ["transport"] = "http" }
                : new Dictionary<string, string> { ["subscriptionId"] = "foreign-configuration" };
            var refused = await HttpJourney.McpAsync(client, token, "tools/call", new { name, arguments });
            await Assert.That(refused.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            await Assert.That(refused.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()).IsEqualTo("Live shared-result configuration is unavailable.");
        }
        var unchanged = await HttpJourney.McpAsync(client, token, "tools/call", new { name = "list_query_subscriptions", arguments = new { } });
        await Assert.That(unchanged.GetProperty("result").GetProperty("structuredContent").GetProperty("subscriptions").GetArrayLength()).IsEqualTo(0);
    }
}
