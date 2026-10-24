using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.E2E.Features.Notifications;

[ClassDataSource<DashboardRootFixture>]
public sealed class AlertJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task FiringRuleEmailsAndSignsOneWebhookThenResolvesWhenTheThresholdIsRaised()
    {
        await using var scenario = new AlertScenario(fixture.Root);
        await scenario.StartAsync();
        string secret;
        using (var created = await scenario.PostAsync("/api/v1/notifications/alerts", new()
        { ["name"] = "Slow orders", ["metric"] = "p95_latency_ms", ["threshold"] = "200", ["database"] = scenario.DatabaseId, ["email"] = "on", ["webhook"] = "https://hooks.example.com/alerts" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            secret = WebUtility.HtmlDecode(Regex.Match(await created.Content.ReadAsStringAsync(), "id=\"alert-secret\"[^>]*>([^<]+)<").Groups[1].Value);
            await Assert.That(WebEncoders.Base64UrlDecode(secret).Length).IsEqualTo(32);
        }
        scenario.Source.Observe(scenario.DatabaseId, 250);
        await scenario.RunAsync();
        await scenario.RunAsync();
        var firing = scenario.Source.Emails.Single();
        await Assert.That(firing.State).IsEqualTo("firing");
        await Assert.That(firing.Value).IsEqualTo(250);
        await Assert.That(firing.DatabaseName).IsEqualTo("orders");
        await Assert.That(firing.OrganizationId).IsEqualTo(scenario.OrganizationId);
        var hook = scenario.Delivery.Requests.Single();
        await Assert.That(hook.Url.ToString()).IsEqualTo("https://hooks.example.com/alerts");
        await AssertSignedAsync(hook, secret);
        using (var body = JsonDocument.Parse(hook.Body))
        {
            await Assert.That(body.RootElement.GetProperty("type").GetString()).IsEqualTo("alert.firing");
            await Assert.That(body.RootElement.GetProperty("alertId").GetString()).IsEqualTo(firing.AlertId);
            await Assert.That(body.RootElement.GetProperty("rule").GetProperty("threshold").GetDouble()).IsEqualTo(200);
            await Assert.That(body.RootElement.GetProperty("database").GetProperty("name").GetString()).IsEqualTo("orders");
        }
        var page = await scenario.Client.GetStringAsync("/notifications/alerts");
        await Assert.That(page).Contains("data-alert-state=\"firing\"");
        await Assert.That(page.Contains(secret, StringComparison.Ordinal)).IsFalse();
        var ruleId = Regex.Match(page, "/api/v1/notifications/alerts/([0-9A-Z]{26})/threshold").Groups[1].Value;
        using (var raised = await scenario.PostAsync($"/api/v1/notifications/alerts/{ruleId}/threshold", new() { ["threshold"] = "300" }))
            await Assert.That(raised.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await scenario.RunAsync();
        await scenario.RunAsync();
        var emails = scenario.Source.Emails.ToArray();
        await Assert.That(emails.Length).IsEqualTo(2);
        await Assert.That(emails[1].State).IsEqualTo("resolved");
        await Assert.That(emails[1].AlertId).IsEqualTo(firing.AlertId);
        var hooks = scenario.Delivery.Requests.ToArray();
        await Assert.That(hooks.Length).IsEqualTo(2);
        await AssertSignedAsync(hooks[1], secret);
        await Assert.That(hooks[1].Body).Contains("\"type\":\"alert.resolved\"");
        await Assert.That(hooks[1].Headers["X-TerraScale-Delivery"]).IsNotEqualTo(hooks[0].Headers["X-TerraScale-Delivery"]);
        await Assert.That(await scenario.Client.GetStringAsync("/notifications/alerts")).Contains("data-alert-state=\"ok\"");
    }

    [Test]
    public async Task FailedWebhookRetriesTheSameDeliveryAndGivesUpAfterThreeAttempts()
    {
        await using var scenario = new AlertScenario(fixture.Root);
        await scenario.StartAsync();
        using (var created = await scenario.PostAsync("/api/v1/notifications/alerts", new()
        { ["name"] = "Errors", ["metric"] = "error_rate_percent", ["threshold"] = "5", ["webhook"] = "https://hooks.example.com/alerts" }))
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        scenario.Delivery.Status = 503;
        scenario.Source.Observe(scenario.DatabaseId, 10, requests: 100, errors: 10);
        for (var run = 0; run < 5; run++) await scenario.RunAsync();
        var attempts = scenario.Delivery.Requests.ToArray();
        await Assert.That(attempts.Length).IsEqualTo(3);
        await Assert.That(attempts.Select(item => item.Headers["X-TerraScale-Delivery"]).Distinct().Count()).IsEqualTo(1);
        await Assert.That(scenario.Source.Emails.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("http://hooks.example.com/alerts")]
    [Arguments("https://user:secret@hooks.example.com/alerts")]
    [Arguments("https://10.1.2.3/alerts")]
    [Arguments("https://100.64.0.7/alerts")]
    [Arguments("https://[::1]/alerts")]
    [Arguments("https://localhost/alerts")]
    public async Task OnlyOwnersCreatePublicHttpsRules(string webhook)
    {
        await using var scenario = new AlertScenario(fixture.Root);
        await scenario.StartAsync();
        var rule = new Dictionary<string, string> { ["name"] = "Storage", ["metric"] = "storage_gb", ["threshold"] = "1", ["webhook"] = webhook };
        using (var rejected = await scenario.PostAsync("/api/v1/notifications/alerts", new(rule)))
            await Assert.That(rejected.StatusCode).IsEqualTo((HttpStatusCode)422);
        scenario.Role("admin");
        rule["webhook"] = "https://hooks.example.com/alerts";
        using (var forbidden = await scenario.PostAsync("/api/v1/notifications/alerts", new(rule)))
            await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var page = await scenario.Client.GetStringAsync("/notifications/alerts");
        await Assert.That(page).Contains("Organization owners create alert rules.");
        await Assert.That(page.Contains("action=\"/api/v1/notifications/alerts\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(await scenario.Client.GetStringAsync("/notifications")).Contains("href=\"/notifications/alerts\"");
    }

    private static async Task AssertSignedAsync((Uri Url, string Body, Dictionary<string, string> Headers) hook, string secret)
    {
        var timestamp = hook.Headers["X-TerraScale-Timestamp"];
        var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(WebEncoders.Base64UrlDecode(secret), Encoding.UTF8.GetBytes(timestamp + "." + hook.Body)));
        await Assert.That(hook.Headers["X-TerraScale-Signature"]).IsEqualTo(expected);
        await Assert.That(TerraScale.DotNet.TerraScaleIds.IsValid(hook.Headers["X-TerraScale-Delivery"])).IsTrue();
    }
}
