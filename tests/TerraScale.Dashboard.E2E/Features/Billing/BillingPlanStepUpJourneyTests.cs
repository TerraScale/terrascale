using System.Net;

namespace TerraScale.Dashboard.E2E.Features.Billing;

public sealed class BillingPlanStepUpJourneyTests
{
    [Test]
    public async Task PlanChangeAndUndoNeedAFreshConfirmation()
    {
        await using var scenario = await Scenario.StartAsync();
        var suffix = Guid.NewGuid().ToString()[..8];
        await BillingPlanJourneyTests.SignUpPaidAsync(scenario.Client, "plan-stepup-" + suffix + "@example.test", "E2E-stepup-" + suffix, suffix, "monthly");
        var baseline = await scenario.SubscriptionMutationsAsync();
        var change = new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(scenario.Client, "/organization?tab=billing"), ["plan"] = "free", ["interval"] = "monthly" };
        using (var unconfirmed = await scenario.PostAsync("/billing/change", change))
        {
            await Assert.That(unconfirmed.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(unconfirmed.Headers.Location!.AbsolutePath.StartsWith("/ui/terrascale/step-up/", StringComparison.Ordinal)).IsTrue();
            await Assert.That(await scenario.SubscriptionMutationsAsync()).IsEqualTo(baseline).Because("an unconfirmed plan change never reaches the provider");
        }

        using var confirmed = await HttpJourney.PostConfirmedFormAsync(scenario.Client, "/billing/change", change);
        await Assert.That(confirmed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(confirmed.Headers.Location!.OriginalString.StartsWith("/organization?tab=billing&billing=scheduled", StringComparison.Ordinal)).IsTrue();
        await Assert.That(await scenario.SubscriptionMutationsAsync()).IsEqualTo(baseline + 1);

        var undo = new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(scenario.Client, "/organization?tab=billing") };
        using (var unconfirmedUndo = await scenario.PostAsync("/billing/change/undo", undo))
        {
            await Assert.That(unconfirmedUndo.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(unconfirmedUndo.Headers.Location!.AbsolutePath.StartsWith("/ui/terrascale/step-up/", StringComparison.Ordinal)).IsTrue();
            await Assert.That(await scenario.SubscriptionMutationsAsync()).IsEqualTo(baseline + 1).Because("an unconfirmed undo never reaches the provider");
        }

        using var confirmedUndo = await HttpJourney.PostConfirmedFormAsync(scenario.Client, "/billing/change/undo", undo);
        await Assert.That(confirmedUndo.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(confirmedUndo.Headers.Location!.OriginalString.StartsWith("/organization?tab=billing&billing=undone", StringComparison.Ordinal)).IsTrue();
        await Assert.That(await scenario.SubscriptionMutationsAsync()).IsEqualTo(baseline + 2);
    }

    [Test]
    public async Task UnapprovedResumeChangesNothing()
    {
        await using var scenario = await Scenario.StartAsync();
        var suffix = Guid.NewGuid().ToString()[..8];
        await BillingPlanJourneyTests.SignUpPaidAsync(scenario.Client, "plan-unapproved-" + suffix + "@example.test", "E2E-stepup-" + suffix, suffix, "monthly");
        var baseline = await scenario.SubscriptionMutationsAsync();
        var change = new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(scenario.Client, "/organization?tab=billing"), ["plan"] = "free", ["interval"] = "monthly" };

        using (var started = await scenario.PostAsync("/billing/change", change))
        {
            await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(started.Headers.Location!.AbsolutePath.StartsWith("/ui/terrascale/step-up/", StringComparison.Ordinal)).IsTrue();
        }

        using var resumed = await scenario.Client.GetAsync("/auth/step-up/callback");
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await resumed.Content.ReadAsStringAsync()).Contains("Confirm this action from the dashboard");
        await Assert.That(await scenario.SubscriptionMutationsAsync()).IsEqualTo(baseline).Because("a resume without an approved proof never reaches the provider");
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public required DemoEnvironment Environment { get; init; }
        public required Microsoft.AspNetCore.Builder.WebApplication App { get; init; }
        public required HttpClient Client { get; init; }

        public static async Task<Scenario> StartAsync()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            var app = await environment.CreateTestHostAsync(new DashboardE2EFixture().Root, 0, "http://127.0.0.1");
            await app.StartAsync();
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() };
            var client = new HttpClient(handler) { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            return new() { Environment = environment, App = app, Client = client };
        }

        public async Task<int> SubscriptionMutationsAsync()
        {
            using var probe = await Client.GetAsync("/__test/billing/plans/probe");
            await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var json = System.Text.Json.JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("mutations").EnumerateArray().Count(path => path.GetString()!.StartsWith("/v1/subscriptions/", StringComparison.Ordinal));
        }

        public async Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> form)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Add("Origin", Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
