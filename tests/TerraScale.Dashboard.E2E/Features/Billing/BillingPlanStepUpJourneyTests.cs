using System.Net;
using System.Text.Json;
using TUnit.Core.Enums;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
public sealed class BillingPlanStepUpJourneyTests(DashboardE2EFixture fixture)
{
    [ExecutionPriority(Priority.High)]
    [Test]
    public async Task PlanChangeAndUndoNeedAFreshConfirmation()
    {
        using var client = fixture.CreateClient();
        var suffix = Guid.NewGuid().ToString()[..8];
        await BillingPlanJourneyTests.SignUpPaidAsync(client, "plan-stepup-" + suffix + "@example.test", "E2E-stepup-" + suffix, suffix, "monthly");
        var baseline = await SubscriptionMutationsAsync(client);
        var change = new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/organization?tab=billing"), ["plan"] = "free", ["interval"] = "monthly" };
        using (var unconfirmed = await NavigateAsync(client, "/billing/change", change))
        {
            await Assert.That(unconfirmed.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(unconfirmed.Headers.Location!.AbsolutePath.StartsWith("/ui/terrascale/step-up/", StringComparison.Ordinal)).IsTrue();
            await Assert.That(await SubscriptionMutationsAsync(client)).IsEqualTo(baseline).Because("an unconfirmed plan change never reaches the provider");
        }

        using var confirmed = await HttpJourney.PostConfirmedFormAsync(client, "/billing/change", change);
        await Assert.That(confirmed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(confirmed.Headers.Location!.OriginalString.StartsWith("/organization?tab=billing&billing=scheduled", StringComparison.Ordinal)).IsTrue();
        await Assert.That(await SubscriptionMutationsAsync(client)).IsEqualTo(baseline + 1);

        var undo = new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/organization?tab=billing") };
        using (var unconfirmedUndo = await NavigateAsync(client, "/billing/change/undo", undo))
        {
            await Assert.That(unconfirmedUndo.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(unconfirmedUndo.Headers.Location!.AbsolutePath.StartsWith("/ui/terrascale/step-up/", StringComparison.Ordinal)).IsTrue();
            await Assert.That(await SubscriptionMutationsAsync(client)).IsEqualTo(baseline + 1).Because("an unconfirmed undo never reaches the provider");
        }

        using var confirmedUndo = await HttpJourney.PostConfirmedFormAsync(client, "/billing/change/undo", undo);
        await Assert.That(confirmedUndo.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(confirmedUndo.Headers.Location!.OriginalString.StartsWith("/organization?tab=billing&billing=undone", StringComparison.Ordinal)).IsTrue();
        await Assert.That(await SubscriptionMutationsAsync(client)).IsEqualTo(baseline + 2);
    }

    [Test]
    public async Task UnapprovedResumeChangesNothing()
    {
        using var client = fixture.CreateClient();
        var suffix = Guid.NewGuid().ToString()[..8];
        await BillingPlanJourneyTests.SignUpPaidAsync(client, "plan-unapproved-" + suffix + "@example.test", "E2E-stepup-" + suffix, suffix, "monthly");
        var baseline = await SubscriptionMutationsAsync(client);
        var change = new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/organization?tab=billing"), ["plan"] = "free", ["interval"] = "monthly" };

        using (var started = await NavigateAsync(client, "/billing/change", change))
        {
            await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(started.Headers.Location!.AbsolutePath.StartsWith("/ui/terrascale/step-up/", StringComparison.Ordinal)).IsTrue();
        }

        using var resumed = await client.GetAsync("/auth/step-up/callback");
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await resumed.Content.ReadAsStringAsync()).Contains("Confirm this action from the dashboard");
        await Assert.That(await SubscriptionMutationsAsync(client)).IsEqualTo(baseline).Because("a resume without an approved proof never reaches the provider");
    }

    private static async Task<int> SubscriptionMutationsAsync(HttpClient client)
    {
        using var probe = await client.GetAsync("/__test/billing/plans/probe");
        await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("mutations").EnumerateArray().Count(path => path.GetString()!.StartsWith("/v1/subscriptions/", StringComparison.Ordinal));
    }

    private static async Task<HttpResponseMessage> NavigateAsync(HttpClient client, string path, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        return await client.SendAsync(request);
    }
}
