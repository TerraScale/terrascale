using System.Net;
using System.Text.Json;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
[Category("Slow")]
public sealed class BillingPlanJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public Task NativePlanReviewUpgradePendingDowngradeUndoAndCheckoutCancellation() => BrowserJourneys.RunAsync(fixture, "billing-plan-browser-flow.mjs");

    [Test]
    public async Task PlanMutationsRequireSignInCsrfValidPlansAndBoundCheckout()
    {
        using var client = fixture.CreateClient();
        using var anonymous = await HttpJourney.PostFormAsync(client, "/billing/change", new() { ["plan"] = "pro", ["interval"] = "monthly" });
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var missingCsrf = await HttpJourney.PostFormAsync(client, "/billing/change", new() { ["plan"] = "pro", ["interval"] = "monthly" });
        await Assert.That(missingCsrf.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        using var invalid = await HttpJourney.PostFormAsync(client, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "reserved", ["interval"] = "monthly" });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var foreignReturn = await client.GetAsync("/billing/checkout/return?session_id=foreign-checkout");
        await Assert.That(foreignReturn.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var foreignCancel = await HttpJourney.PostFormAsync(client, "/checkout/foreign-checkout/cancel", new() { ["_csrf"] = csrf });
        await Assert.That(foreignCancel.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task TwoReplicasRecoverWhenStripeChangesTheReservedSourceBeforeMutation()
    {
        using var first = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var email = "plan-race-" + suffix + "@example.test";
        var password = "E2E-plan-race-" + suffix;
        await SignUpPaidAsync(first, email, password, suffix);
        using var login = await HttpJourney.LoginAsync(replica, email, password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(first, "/organization?tab=billing");
        var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/organization?tab=billing");
        using var armed = await HttpJourney.PostFormAsync(first, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "source-race" });
        await Assert.That(armed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var held = HttpJourney.PostFormAsync(replica, "/billing/change", new() { ["_csrf"] = replicaCsrf, ["plan"] = "pro", ["interval"] = "monthly" });
        var released = false;
        try
        {
            var waiting = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var probe = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/plans/probe"));
                if (probe.RootElement.GetProperty("sourceRaceWaiting").GetBoolean()) { waiting = true; break; }
                await Task.Delay(25);
            }
            await Assert.That(waiting).IsTrue().Because("The real second Stripe read must wait after the app persisted its reservation.");
            using var competing = await HttpJourney.PostFormAsync(first, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "business", ["interval"] = "monthly" });
            await Assert.That(competing.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            using var release = await HttpJourney.PostFormAsync(first, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "release-source-race" });
            await Assert.That(release.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            released = true;
            using var refused = await held;
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            using var beforeRetry = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/plans/probe"));
            await Assert.That(beforeRetry.RootElement.GetProperty("mutations").EnumerateArray().Count(item => item.GetString()!.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))).IsEqualTo(0);
            using var retry = await HttpJourney.PostFormAsync(replica, "/billing/change", new() { ["_csrf"] = replicaCsrf, ["plan"] = "business", ["interval"] = "monthly" });
            await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await retry.Content.ReadAsStringAsync());
            using var afterRetry = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/plans/probe"));
            await Assert.That(afterRetry.RootElement.GetProperty("mutations").EnumerateArray().Count(item => item.GetString()!.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))).IsEqualTo(1);
            using var me = JsonDocument.Parse(await first.GetStringAsync("/api/v1/auth/me"));
            using var context = JsonDocument.Parse(await first.GetStringAsync("/api/v1/dashboard/context"));
            var organization = context.RootElement.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == me.RootElement.GetProperty("organizationId").GetString());
            await Assert.That(organization.GetProperty("planId").GetString()).IsEqualTo("09XCWRH05BWWBADSBPTPDMFA7D");
        }
        finally
        {
            if (!released)
            {
                using var release = await HttpJourney.PostFormAsync(first, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "release-source-race" });
                try { using var response = await held; } catch (HttpRequestException) { }
            }
        }
    }

    [Test]
    public async Task AmbiguousVendorCompletionReplaysTheBoundPriceAfterItIsRepublished()
    {
        using var first = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var email = "plan-replay-" + suffix + "@example.test";
        var password = "E2E-plan-replay-" + suffix;
        await SignUpPaidAsync(first, email, password, suffix);
        using var login = await HttpJourney.LoginAsync(replica, email, password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(first, "/organization?tab=billing");
        var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/organization?tab=billing");
        using var armed = await HttpJourney.PostFormAsync(first, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "uncertain-upgrade" });
        await Assert.That(armed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        try
        {
            using var lost = await HttpJourney.PostFormAsync(first, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly" });
            await Assert.That(lost.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
            await Assert.That(await lost.Content.ReadAsStringAsync()).Contains("Stripe could not complete this billing change.");
            using var conflicting = await HttpJourney.PostFormAsync(replica, "/billing/change", new() { ["_csrf"] = replicaCsrf, ["plan"] = "business", ["interval"] = "monthly" });
            await Assert.That(conflicting.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            using var outage = await HttpJourney.PostFormAsync(first, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "offline" });
            await Assert.That(outage.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            using var unavailable = await first.GetAsync("/organization?tab=billing");
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var unavailableBody = await unavailable.Content.ReadAsStringAsync();
            await Assert.That(unavailableBody).Contains("Could not load billing changes. Reload billing to retry.");
            await Assert.That(unavailableBody).Contains("Payment method could not be loaded. Reload billing to retry.");
            await Assert.That(unavailableBody).Contains("<dt>Payment method</dt><dd>Not reported</dd>");
            await Assert.That(unavailableBody.TrimEnd().EndsWith("</html>", StringComparison.Ordinal)).IsTrue().Because("Provider failure must produce a complete page, never a truncated Razor response.");
            using var republished = await HttpJourney.PostFormAsync(first, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "republish-price" });
            await Assert.That(republished.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            using var preview = await first.GetAsync("/organization?tab=billing&review=1&plan=pro&interval=monthly");
            await Assert.That(preview.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await preview.Content.ReadAsStringAsync()).Contains("US$ 6.66 estimated by Stripe");
            using var beforeRetry = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/plans/probe"));
            await Assert.That(beforeRetry.RootElement.GetProperty("publishedReplacementObserved").GetBoolean()).IsTrue();
            using var retried = await HttpJourney.PostFormAsync(replica, "/billing/change", new() { ["_csrf"] = replicaCsrf, ["plan"] = "pro", ["interval"] = "monthly" });
            await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await retried.Content.ReadAsStringAsync());
            using var afterRetry = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/plans/probe"));
            var attempts = afterRetry.RootElement.GetProperty("updateAttempts").EnumerateArray().ToArray();
            await Assert.That(attempts.Length).IsGreaterThan(1);
            await Assert.That(attempts.Select(item => item.GetProperty("priceId").GetString()).Distinct().Single()).IsEqualTo("price_pro_monthly");
            await Assert.That(attempts.Select(item => item.GetProperty("requestKey").GetString()).Distinct().Count()).IsEqualTo(1);
            using var billingPage = await replica.GetAsync("/organization?tab=billing");
            await Assert.That(billingPage.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await billingPage.Content.ReadAsStringAsync()).Contains("class=\"ts-billing-plan-name\">Pro</span>");
        }
        finally
        {
            using var reset = await HttpJourney.PostFormAsync(first, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "confirmed" });
        }
    }

    [Test]
    public async Task InterruptedCheckoutIndexResumesItsOriginalScopeAcrossReplicasAndRejectsWrongDocumentIdentity()
    {
        using var first = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var email = "checkout-index-" + suffix + "@example.test";
        var password = "E2E-checkout-index-" + suffix;
        await SignUpPaidAsync(first, email, password, suffix);
        var csrf = await HttpJourney.CsrfAsync(first, "/organization?tab=billing");
        using var upgrade = await HttpJourney.PostFormAsync(first, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly" });
        await Assert.That(upgrade.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var personal = JsonDocument.Parse(await first.GetStringAsync("/api/v1/auth/me"));
        var personalId = personal.RootElement.GetProperty("organizationId").GetString()!;
        var personalProject = personal.RootElement.GetProperty("projectId").GetString()!;
        using var creation = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "Interrupted checkout " + suffix });
        await Assert.That(creation.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var business = JsonDocument.Parse(await first.GetStringAsync("/api/v1/auth/me"));
        var businessId = business.RootElement.GetProperty("organizationId").GetString()!;
        var returnPath = "/organization?newOrganization=project&organizationID=" + Uri.EscapeDataString(businessId);
        using var login = await HttpJourney.LoginAsync(replica, email, password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/organization?tab=billing");
        using var armed = await HttpJourney.PostFormAsync(first, "/__test/billing/checkouts/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "missing-index" });
        await Assert.That(armed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var checkoutForm = new Dictionary<string, string> { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "monthly", ["return"] = returnPath };
        using var interrupted = await HttpJourney.PostFormAsync(first, "/billing/checkout", checkoutForm);
        await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var interruptedBody = await interrupted.Content.ReadAsStringAsync();
        await Assert.That(interruptedBody).Contains("Service unavailable");
        await Assert.That(interruptedBody.TrimEnd().EndsWith("</html>", StringComparison.Ordinal)).IsTrue();
        using var failed = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/checkouts/probe"));
        await Assert.That(failed.RootElement.GetProperty("hasIndex").GetBoolean()).IsFalse();
        var checkoutId = failed.RootElement.GetProperty("checkoutId").GetString()!;
        await Assert.That(checkoutId).IsEqualTo(failed.RootElement.GetProperty("failedIndex").GetProperty("checkoutId").GetString());
        await Assert.That(failed.RootElement.GetProperty("failedIndex").GetProperty("projectId").GetString()).IsEqualTo("");
        using var switchAway = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = personalId, ["returnUrl"] = "/organization?tab=billing" });
        await Assert.That(switchAway.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var missingCallback = await replica.GetAsync("/billing/checkout/return?session_id=" + Uri.EscapeDataString(checkoutId));
        await Assert.That(missingCallback.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var originalSelection = JsonDocument.Parse(await first.GetStringAsync("/api/v1/auth/me"));
        await Assert.That(originalSelection.RootElement.GetProperty("organizationId").GetString()).IsEqualTo(personalId);
        await Assert.That(originalSelection.RootElement.GetProperty("projectId").GetString()).IsEqualTo(personalProject);
        using var switchBack = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = replicaCsrf, ["organizationId"] = businessId, ["returnUrl"] = "/organization?tab=billing" });
        await Assert.That(switchBack.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        checkoutForm["_csrf"] = replicaCsrf;
        using var retry = await HttpJourney.PostFormAsync(replica, "/billing/checkout", checkoutForm);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(retry.Headers.Location!.OriginalString).Contains("id=" + checkoutId);
        using var resumed = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/checkouts/probe"));
        await Assert.That(resumed.RootElement.GetProperty("hasIndex").GetBoolean()).IsTrue();
        var index = resumed.RootElement.GetProperty("index");
        await Assert.That(index.GetProperty("checkoutId").GetString()).IsEqualTo(checkoutId);
        await Assert.That(index.GetProperty("organizationId").GetString()).IsEqualTo(businessId);
        await Assert.That(index.GetProperty("projectId").GetString()).IsEqualTo("");
        await Assert.That(index.GetProperty("returnPath").GetString()).IsEqualTo(returnPath);
        using var corrupt = await HttpJourney.PostFormAsync(first, "/__test/billing/checkouts/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "wrong-document-id" });
        await Assert.That(corrupt.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var corruptRetry = await HttpJourney.PostFormAsync(replica, "/billing/checkout", checkoutForm);
        await Assert.That(corruptRetry.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var corruptCallback = await first.GetAsync("/billing/checkout/return?session_id=" + Uri.EscapeDataString(checkoutId));
        await Assert.That(corruptCallback.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        await Assert.That(await corruptCallback.Content.ReadAsStringAsync()).Contains("Stripe could not complete this billing change.");
        using var restore = await HttpJourney.PostFormAsync(first, "/__test/billing/checkouts/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "normal" });
        await Assert.That(restore.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var awayAgain = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = personalId, ["returnUrl"] = "/organization?tab=billing" });
        await Assert.That(awayAgain.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var complete = await first.GetAsync(retry.Headers.Location);
        await Assert.That(complete.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(complete.Headers.Location!.OriginalString).IsEqualTo("/billing/checkout/return?session_id=" + checkoutId);
        using var callback = await replica.GetAsync(complete.Headers.Location);
        await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(callback.Headers.Location!.OriginalString).IsEqualTo(returnPath + "&payment=success");
        using var finalSelection = JsonDocument.Parse(await first.GetStringAsync("/api/v1/auth/me"));
        await Assert.That(finalSelection.RootElement.GetProperty("organizationId").GetString()).IsEqualTo(businessId);
        await Assert.That(finalSelection.RootElement.GetProperty("projectId").ValueKind).IsEqualTo(JsonValueKind.Null);
        using var finalContext = JsonDocument.Parse(await first.GetStringAsync("/api/v1/dashboard/context"));
        var organizations = finalContext.RootElement.GetProperty("organizations").EnumerateArray().ToArray();
        await Assert.That(organizations.Single(org => org.GetProperty("id").GetString() == personalId).GetProperty("planId").GetString()).IsEqualTo("7CNW2H87JE68ATZX224CV2G625");
        await Assert.That(organizations.Single(org => org.GetProperty("id").GetString() == businessId).GetProperty("planId").GetString()).IsEqualTo("443RCMMF70MNP6S1QMT230NF99");
    }

    internal static async Task SignUpPaidAsync(HttpClient client, string email, string password, string suffix)
    {
        var csrf = await HttpJourney.CsrfAsync(client, "/onboarding?plan=starter");
        using var registration = await HttpJourney.PostFormAsync(client, "/onboarding", new()
        { ["_csrf"] = csrf, ["displayName"] = "Plan race " + suffix, ["email"] = email, ["password"] = password, ["confirmPassword"] = password, ["terms"] = "true", ["plan"] = "starter" });
        await Assert.That(registration.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var verification = await HttpJourney.PostFormAsync(client, "/login/code/verify", new()
        { ["_csrf"] = csrf, ["email"] = email, ["code"] = "123456", ["signUp"] = "1" });
        await Assert.That(verification.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        csrf = await HttpJourney.CsrfAsync(client, "/onboarding?plan=starter");
        using var information = await HttpJourney.PostFormAsync(client, "/onboarding/step", new()
        { ["_csrf"] = csrf, ["step"] = "account-information", ["displayName"] = "Plan race " + suffix });
        await Assert.That(information.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var payment = await HttpJourney.PostFormAsync(client, "/onboarding/step", new()
        { ["_csrf"] = csrf, ["step"] = "payment-method", ["plan"] = "starter", ["interval"] = "monthly" });
        await Assert.That(payment.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var completed = await client.GetAsync(payment.Headers.Location);
        await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(completed.Headers.Location!.OriginalString.StartsWith("/billing/checkout/return?session_id=", StringComparison.Ordinal)).IsTrue();
        using var callback = await client.GetAsync(completed.Headers.Location);
        await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(callback.Headers.Location!.OriginalString).IsEqualTo("/onboarding?payment=success");
        using var confirmation = await client.GetAsync(callback.Headers.Location);
        await Assert.That(confirmation.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var project = await HttpJourney.PostFormAsync(client, "/onboarding/step", new()
        { ["_csrf"] = csrf, ["step"] = "project", ["name"] = "Plan race project " + suffix, ["region"] = "GRU" });
        await Assert.That(project.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
    }
}
