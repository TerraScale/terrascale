using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class BillingPlanJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task NativePlanReviewUpgradePendingDowngradeUndoAndCheckoutCancellation()
    {
        const string Starter = "443RCMMF70MNP6S1QMT230NF99", Pro = "7CNW2H87JE68ATZX224CV2G625";
        var output = BrowserAudit.Output(fixture.Root, "billing-plan-journey");
        var origin = fixture.Origin.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var owner = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await owner.NewPageAsync();
        page.SetDefaultTimeout(20000);
        IBrowserContext? guest = null;
        var errors = new ConcurrentQueue<string>();
        var checks = new List<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        var suffix = Guid.NewGuid().ToString()[..8];
        try
        {
            var password = "E2E-plan-" + Guid.NewGuid();
            var signedUp = await BrowserAudit.SignInAsync(page, fixture.Origin, "/onboarding?plan=starter", "plan-owner-" + suffix + "@example.test", password, signUp: true);
            await Assert.That(signedUp!.Status).IsEqualTo(200);
            await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Save and continue");
            var starter = page.Locator("input[name=plan][value=starter]");
            await page.Locator("label").Filter(new() { Has = starter }).ClickAsync();
            await Assert.That(await starter.IsCheckedAsync()).IsTrue();
            await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Continue to checkout");
            await page.Locator("#projectName").FillAsync("Plan project " + suffix);
            await page.Locator("#region").SelectOptionAsync("GRU");
            await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Create project and open Dashboard");

            await ChooseAsync("pro");
            await Assert.That(await page.Locator("#review-billing-plan").InnerTextAsync()).Contains("$6.66 estimated by Stripe (including $1.66 in prorations)");
            await ScreenshotAsync("upgrade-review-desktop");
            await ScenarioAsync("pending");
            await ConfirmAsync();
            await page.GetByText("Awaiting payment", new() { Exact = true }).WaitForAsync();
            await Assert.That(await page.GetByRole(AriaRole.Link, new() { Name = "Complete payment", Exact = true }).GetAttributeAsync("href"))
                .StartsWith("https://invoice.stripe.com/i/pending_").Because("a payment that needs 3D Secure links to Stripe's hosted invoice page");
            await Assert.That(await PlanAsync()).IsEqualTo(Starter).Because("Pending payment keeps the existing allowance.");
            await ScenarioAsync("confirm-payment");
            await GotoAsync("/organization?tab=billing&payment=success");
            await Assert.That(await page.Locator("#billing-summary").InnerTextAsync()).Contains("Pro");
            await Assert.That(await PlanAsync()).IsEqualTo(Pro);
            await ScenarioAsync("portal-default");
            await GotoAsync("/organization?tab=billing&payment=success");
            await Assert.That(await PlanAsync()).IsEqualTo(Pro).Because("a default card set in the billing portal lives on the customer and keeps the paid plan");
            var upgraded = await MutationsAsync(path => path.StartsWith("/v1/subscriptions/", StringComparison.Ordinal));
            await PostAsync("/billing/change", new() { ["plan"] = "pro", ["interval"] = "monthly" });
            await Assert.That(await MutationsAsync(path => path.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))).IsEqualTo(upgraded).Because("repeated confirmed change never calls the vendor again");
            checks.Add("native Stripe invoice preview, confirmed immediate upgrade and idempotent replay");

            await ChooseAsync("starter");
            await Assert.That(await page.Locator("#review-billing-plan").InnerTextAsync()).Contains("No charge today");
            await ConfirmAsync();
            await page.GetByText("Change scheduled", new() { Exact = true }).WaitForAsync();
            await Assert.That(await PlanAsync()).IsEqualTo(Pro).Because("a scheduled downgrade keeps current resources until renewal");
            var schedules = await MutationsAsync(path => path == "/v1/subscription_schedules");
            await PostAsync("/billing/change", new() { ["plan"] = "starter", ["interval"] = "annual" });
            await Assert.That(await MutationsAsync(path => path == "/v1/subscription_schedules")).IsEqualTo(schedules);
            await PostAsync("/billing/change", new() { ["plan"] = "pro", ["interval"] = "monthly" }, 409);
            await SubmitAsync(page, "form:is([action=\"/billing/change/undo\"],[action^=\"/billing/change/undo?\"])", "Undo scheduled change");
            await Assert.That(await page.GetByText("Change scheduled", new() { Exact = true }).CountAsync()).IsEqualTo(0);
            await ChooseAsync("free");
            await ConfirmAsync();
            await page.GetByText("Change scheduled", new() { Exact = true }).WaitForAsync();
            await Assert.That(await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Change scheduled" }).InnerTextAsync()).Contains("Free");
            await Assert.That(await PlanAsync()).IsEqualTo(Pro);
            await ScenarioAsync("reconcile-outage");
            await PostAsync("/billing/change/undo", []);
            await ScenarioAsync("confirmed");
            await GotoAsync("/organization?tab=billing");
            await Assert.That(await page.GetByText("Change scheduled", new() { Exact = true }).CountAsync()).IsEqualTo(0).Because("a change Stripe accepted redirects even when reconciliation cannot read Stripe yet");
            checks.Add("pending payment cannot grant a new plan; provider confirmation, scheduled downgrade/replay/undo and Personal Free cancellation use real SDK operations");

            var personal = await SelectedAsync();
            var personalId = personal.GetProperty("id").GetString();
            var personalPlan = personal.GetProperty("planId").GetString();
            var personalProject = (await JsonAsync(owner, "/api/v1/auth/me")).GetProperty("projectId").GetString();
            await ScenarioAsync("foreign-customer");
            await GotoAsync("/organization?tab=billing&review=1&plan=pro&interval=monthly");
            await Assert.That(await page.Locator("#review-billing-plan button").CountAsync()).IsEqualTo(0);
            await Assert.That(await page.Locator("#billing-summary").InnerTextAsync()).Matches(@"Payment method\s+Not reported");
            await Assert.That(await PlanAsync()).IsEqualTo(Pro);
            await ScenarioAsync("confirmed");
            await page.SetViewportSizeAsync(390, 844);
            await ChooseAsync("starter");
            await ScreenshotAsync("downgrade-review-mobile");
            await GotoAsync("/organization?newOrganization=organization");
            await page.Locator("#create-organization input[name=name]").FillAsync("Pending billing " + suffix);
            await SubmitAsync(page, "#create-organization form", "Create organization");
            await GotoAsync("/organization?tab=billing");
            await PostAsync("/billing/change", new() { ["plan"] = "free" }, 400);
            var checkout = CheckoutId(await PostAsync("/billing/checkout", new() { ["plan"] = "starter", ["interval"] = "annual", ["return"] = "/organization?tab=billing" }));
            await Assert.That(checkout).IsNotNull().And.IsNotEmpty();
            await GotoAsync("/organization?tab=billing");
            await SubmitAsync(page, "form:is([action=\"/billing/checkout/cancel\"],[action^=\"/billing/checkout/cancel?\"])", "Cancel checkout");
            await Assert.That((await SelectedAsync()).GetProperty("billingStatus").GetString()).IsEqualTo("billing_required");
            var ended = await ReturnAsync(owner, checkout!);
            await Assert.That(ended.Status).IsEqualTo(303);
            await Assert.That(ended.Headers["location"]).Contains("payment=canceled");
            await PostAsync("/checkout/foreign-checkout/cancel", [], 404);
            var replacement = await PostAsync("/billing/checkout", new() { ["plan"] = "pro", ["interval"] = "monthly", ["return"] = "/organization?tab=billing" });
            await Assert.That(CheckoutId(replacement)).IsNotEqualTo(checkout);
            await PostAsync("/billing/checkout/cancel", []);
            await ScreenshotAsync("canceled-checkout-mobile");
            checks.Add("cross-customer state is rejected, Business Free is refused, checkout expiration/cancel/replacement stays bound and does not grant resources");

            var businessId = (await SelectedAsync()).GetProperty("id").GetString();
            var originalCheckout = await PostAsync("/billing/checkout", new() { ["plan"] = "starter", ["interval"] = "annual", ["return"] = "/organization?tab=billing" });
            var originalCheckoutUrl = new Uri(fixture.Origin, originalCheckout.Headers["location"]).ToString();
            var originalCheckoutId = CheckoutId(originalCheckout);
            await Assert.That(originalCheckoutId).IsNotNull().And.IsNotEmpty();
            await SwitchOrganizationAsync(personalId!);
            await Assert.That(await PlanAsync()).IsEqualTo(personalPlan);
            await Assert.That((await JsonAsync(owner, "/api/v1/auth/me")).GetProperty("projectId").GetString()).IsEqualTo(personalProject);
            await page.GotoAsync(originalCheckoutUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(BrowserAudit.WithoutWorkspace(page.Url)).EndsWith("/organization?tab=billing&payment=success");
            await Assert.That((await SelectedAsync()).GetProperty("id").GetString()).IsEqualTo(businessId).Because("callback restores the original organization after a native shell switch");
            await Assert.That(await PlanAsync()).IsEqualTo(Starter);
            await Assert.That((await JsonAsync(owner, "/api/v1/auth/me")).GetProperty("projectId").ValueKind).IsEqualTo(JsonValueKind.Null).Because("the other organization project is never adopted by the original checkout");
            var afterCallback = (await JsonAsync(owner, "/api/v1/dashboard/context")).GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == personalId);
            await Assert.That(afterCallback.GetProperty("planId").GetString()).IsEqualTo(personalPlan).Because("only the original organization receives the checkout entitlement");
            await SwitchOrganizationAsync(personalId!);
            var obsolete = await ReturnAsync(owner, checkout!);
            await Assert.That(obsolete.Status).IsEqualTo(404).Because("replaced checkout cannot change selection");
            await Assert.That((await SelectedAsync()).GetProperty("id").GetString()).IsEqualTo(personalId);
            var repeated = await ReturnAsync(owner, originalCheckoutId!);
            await Assert.That(repeated.Status).IsEqualTo(303);
            await Assert.That(repeated.Headers["location"]).Contains("payment=success");
            await Assert.That((await SelectedAsync()).GetProperty("id").GetString()).IsEqualTo(businessId);
            checks.Add("native two-organization checkout completion restores only its bound original organization, preserves the other plan/project, safely replays and rejects replaced callbacks");

            guest = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 390, Height = 844 } });
            var guestPage = await guest.NewPageAsync();
            guestPage.SetDefaultTimeout(20000);
            guestPage.PageError += (_, error) => errors.Enqueue(error);
            var email = "plan-member-" + suffix + "@example.test";
            await BrowserAudit.SignInAsync(guestPage, fixture.Origin, "/onboarding", email, password, signUp: true);
            await SubmitAsync(guestPage, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Save and continue", null);
            await SubmitAsync(guestPage, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Continue on Free", null);
            await guestPage.Locator("#projectName").FillAsync("Member project " + suffix);
            await guestPage.Locator("#region").SelectOptionAsync("GRU");
            await SubmitAsync(guestPage, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Create project and open Dashboard", null);
            var unrelated = await ReturnAsync(guest, originalCheckoutId!);
            await Assert.That(unrelated.Status).IsEqualTo(404).Because("an unrelated authenticated account cannot resolve the original organization");
            await GotoAsync("/organization?tab=members");
            await page.Locator("#invite-email").FillAsync(email);
            await page.Locator("#invite-role").SelectOptionAsync("member");
            await SubmitAsync(page, "#team-invite form", "Create invitation", 200);
            await guestPage.GotoAsync((await page.Locator("#invitation-link").GetAttributeAsync("href"))!, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await SubmitAsync(guestPage, "form[action$=\"/respond\"]", "Accept invitation", null);
            await guestPage.GotoAsync(origin + "/organization?tab=billing&review=1&plan=pro&interval=monthly", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(await guestPage.Locator("#review-billing-plan").CountAsync()).IsEqualTo(0);
            await Assert.That(await guestPage.GetByRole(AriaRole.Button, new() { Name = "Change plan", Exact = true }).CountAsync()).IsEqualTo(0);
            var guestCsrf = await guestPage.Locator("input[name=_csrf]").First.InputValueAsync();
            foreach (var action in new[] { "/billing/change", "/billing/change/undo", "/billing/checkout/cancel" })
            {
                var form = guest.APIRequest.CreateFormData().Set("_csrf", guestCsrf).Set("plan", "pro").Set("interval", "monthly");
                var denied = await guest.APIRequest.PostAsync(origin + action, new() { Headers = new Dictionary<string, string> { ["Origin"] = origin }, Form = form, MaxRedirects = 0 });
                await Assert.That(denied.Status).IsEqualTo(403).Because("fresh non-owner role denies " + action);
            }
            var memberCallback = await ReturnAsync(guest, originalCheckoutId!);
            await Assert.That(memberCallback.Status).IsEqualTo(403).Because("an accepted Member cannot confirm or restore owner checkout scope");
            checks.Add("native accepted Member sees no billing controls and cannot forge plan change, undo or checkout cancellation");

            await Assert.That(errors.ToArray()).IsEmpty().Because("no Datastar or browser errors");
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { passed = true, checks, browserErrors = errors }, Indented));
            Console.WriteLine("PASS: " + string.Join("; ", checks));
        }
        catch (Exception failure)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "failure.png"), FullPage = true });
            await File.WriteAllTextAsync(Path.Combine(output, "failure.html"), await page.ContentAsync());
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { passed = false, checks, browserErrors = errors, url = page.Url, failure = failure.ToString() }, Indented));
            throw;
        }
        finally
        {
            if (guest is not null) await guest.DisposeAsync();
        }

        async Task GotoAsync(string path)
        {
            var response = await page.GotoAsync(origin + path, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(response!.Status).IsEqualTo(200).Because(path);
        }
        // Waits for the POST response and the resulting navigation like the former Promise.all; status is checked only when given.
        async Task SubmitAsync(IPage target, string selector, string name, int? expected = 303)
        {
            var form = target.Locator(selector);
            var path = new Uri(fixture.Origin, await form.GetAttributeAsync("action")).AbsolutePath;
            var response = target.WaitForResponseAsync(value => new Uri(value.Url).AbsolutePath == path && value.Request.Method == "POST");
#pragma warning disable CS0612, CS0618 // Same navigation wait as the former waitForNavigation; a POST may land on the same URL.
            var navigation = target.WaitForNavigationAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
#pragma warning restore CS0612, CS0618
            await form.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).ClickAsync();
            await navigation;
            if (expected is { } status) await Assert.That((await response).Status).IsEqualTo(status).Because(path);
            else await response;
        }
        async Task<IAPIResponse> PostAsync(string path, Dictionary<string, string> fields, int expected = 303)
        {
            var form = owner.APIRequest.CreateFormData().Set("_csrf", await page.Locator("input[name=_csrf]").First.InputValueAsync());
            foreach (var (key, value) in fields) form.Set(key, value);
            var response = await owner.APIRequest.PostAsync(origin + path, new() { Headers = new Dictionary<string, string> { ["Origin"] = origin }, Form = form, MaxRedirects = 0 });
            await Assert.That(response.Status).IsEqualTo(expected).Because(path + ": " + await response.TextAsync());
            return response;
        }
        async Task ScenarioAsync(string value) => await PostAsync("/__test/billing/plans/scenario", new() { ["scenario"] = value }, 204);
        async Task<int> MutationsAsync(Func<string, bool> match)
        {
            var response = await owner.APIRequest.GetAsync(origin + "/__test/billing/plans/probe");
            await Assert.That(response.Status).IsEqualTo(200);
            return (await response.JsonAsync())!.Value.GetProperty("mutations").EnumerateArray().Count(path => match(path.GetString()!));
        }
        async Task<JsonElement> JsonAsync(IBrowserContext context, string path) => (await (await context.APIRequest.GetAsync(origin + path)).JsonAsync())!.Value;
        async Task<JsonElement> SelectedAsync()
        {
            var id = (await JsonAsync(owner, "/api/v1/auth/me")).GetProperty("organizationId").GetString();
            return (await JsonAsync(owner, "/api/v1/dashboard/context")).GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == id);
        }
        async Task<string?> PlanAsync() => (await SelectedAsync()).GetProperty("planId").GetString();
        string? CheckoutId(IAPIResponse response) => QueryHelpers.ParseQuery(new Uri(fixture.Origin, response.Headers["location"]).Query).GetValueOrDefault("id").ToString();
        Task<IAPIResponse> ReturnAsync(IBrowserContext context, string session) => context.APIRequest.GetAsync(origin + "/billing/checkout/return?session_id=" + session, new() { MaxRedirects = 0 });
        async Task SwitchOrganizationAsync(string id)
        {
            await page.SetViewportSizeAsync(1440, 900);
            await GotoAsync("/organization?tab=billing");
            await page.Locator(".ts-context-menu > summary").ClickAsync();
            var response = page.WaitForResponseAsync(value => new Uri(value.Url).AbsolutePath == "/api/v1/dashboard/switch-organization" && value.Request.Method == "POST");
#pragma warning disable CS0612, CS0618
            var navigation = page.WaitForNavigationAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
#pragma warning restore CS0612, CS0618
            await page.Locator("button[data-organization-option=\"" + id + "\"]").ClickAsync();
            await navigation;
            await Assert.That((await response).Status).IsEqualTo(303);
            await Assert.That((await SelectedAsync()).GetProperty("id").GetString()).IsEqualTo(id);
        }
        async Task ChooseAsync(string plan)
        {
            await GotoAsync("/organization?tab=billing");
            await page.GetByRole(AriaRole.Button, new() { Name = "Change plan", Exact = true }).ClickAsync();
            await page.Locator("#change-billing-plan[open]").WaitForAsync();
            foreach (var (name, value) in new[] { ("plan", plan) })
            {
                var selector = "input[name=" + name + "][value=" + value + "]";
                await page.Locator("#change-billing-plan label").Filter(new() { Has = page.Locator(selector) }).ClickAsync();
                await Assert.That(await page.Locator("#change-billing-plan").Locator(selector).IsCheckedAsync()).IsTrue();
            }
#pragma warning disable CS0612, CS0618
            var navigation = page.WaitForNavigationAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
#pragma warning restore CS0612, CS0618
            await page.Locator("#change-billing-plan form").GetByRole(AriaRole.Button, new() { Name = "Review change", Exact = true }).ClickAsync();
            await navigation;
            await page.Locator("#review-billing-plan[open]").WaitForAsync();
        }
        Task ConfirmAsync() => SubmitAsync(page, "#review-billing-plan form", "Confirm change");
        async Task ScreenshotAsync(string name)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, name + ".png"), FullPage = true, Caret = ScreenshotCaret.Hide });
            await Assert.That(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 2")).IsTrue().Because(name + " has no horizontal overflow");
            await BrowserAudit.AssertAccessibleAsync(page, name + " billing dialog WCAG 2.2 checks pass");
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Test]
    public async Task PlanMutationsRequireSignInCsrfValidPlansAndBoundCheckout()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var anonymous = await HttpJourney.PostFormAsync(client, "/billing/change", new() { ["plan"] = "pro", ["interval"] = "monthly" });
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var missingCsrf = await HttpJourney.PostFormAsync(client, "/billing/change", new() { ["plan"] = "pro", ["interval"] = "monthly" });
        await Assert.That(missingCsrf.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        using var invalid = await HttpJourney.PostFormAsync(client, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "reserved", ["interval"] = "monthly" });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(invalid.Content.Headers.ContentType!.MediaType).IsEqualTo("text/plain");
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
            using var competing = await HttpJourney.PostFormAsync(first, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "team", ["interval"] = "monthly" });
            await Assert.That(competing.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await competing.Content.ReadAsStringAsync()).Contains("Another billing change is in progress.");
            using var release = await HttpJourney.PostFormAsync(first, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "release-source-race" });
            await Assert.That(release.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            released = true;
            using var refused = await held;
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("The subscription changed in Stripe.");
            using var beforeRetry = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/plans/probe"));
            await Assert.That(beforeRetry.RootElement.GetProperty("mutations").EnumerateArray().Count(item => item.GetString()!.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))).IsEqualTo(0);
            using var retry = await HttpJourney.PostFormAsync(replica, "/billing/change", new() { ["_csrf"] = replicaCsrf, ["plan"] = "pro", ["interval"] = "monthly" });
            await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await retry.Content.ReadAsStringAsync());
            using var afterRetry = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/plans/probe"));
            await Assert.That(afterRetry.RootElement.GetProperty("mutations").EnumerateArray().Count(item => item.GetString()!.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))).IsEqualTo(1);
            using var me = JsonDocument.Parse(await first.GetStringAsync("/api/v1/auth/me"));
            using var context = JsonDocument.Parse(await first.GetStringAsync("/api/v1/dashboard/context"));
            var organization = context.RootElement.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == me.RootElement.GetProperty("organizationId").GetString());
            await Assert.That(organization.GetProperty("planId").GetString()).IsEqualTo("7CNW2H87JE68ATZX224CV2G625");
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
            await Assert.That(await lost.Content.ReadAsStringAsync()).IsEqualTo("Stripe could not complete this billing change. Reload billing and try again.");
            await Assert.That(lost.Content.Headers.ContentType!.MediaType).IsEqualTo("text/plain");
            using var selection = JsonDocument.Parse(await first.GetStringAsync("/api/v1/auth/me"));
            var organizationId = selection.RootElement.GetProperty("organizationId").GetString()!;
            using var htmlRequest = new HttpRequestMessage(HttpMethod.Post, "/billing/change?organizationId=" + Uri.EscapeDataString(organizationId))
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly" }) };
            htmlRequest.Headers.Add("Origin", first.BaseAddress!.GetLeftPart(UriPartial.Authority));
            htmlRequest.Headers.Accept.ParseAdd("text/html");
            using var htmlFailure = await first.SendAsync(htmlRequest);
            await Assert.That(htmlFailure.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
            await Assert.That(htmlFailure.Content.Headers.ContentType!.MediaType).IsEqualTo("text/html");
            await Assert.That(htmlFailure.Headers.Location).IsNull();
            var html = await htmlFailure.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("<!DOCTYPE html>");
            await Assert.That(html.TrimEnd().EndsWith("</html>", StringComparison.Ordinal)).IsTrue();
            await Assert.That(html).Contains("<title>Billing change uncertain · TerraScale</title>");
            await Assert.That(html).Contains("aria-labelledby=\"billing-change-title\"");
            await Assert.That(html).Contains("role=\"alert\"");
            await Assert.That(html).Contains("brand/logo-icon-blue.svg");
            await Assert.That(html).Contains("We could not confirm whether Stripe completed this billing change");
            await Assert.That(html).Contains("Review your current plan and billing status before trying another billing change.");
            await Assert.That(html).Contains("Stripe may have already processed your request.");
            foreach (var privateValue in new[] { csrf, "price_", "cus_", "sub_", "requestKey", "test-only vendor", "HttpRequestException", "_csrf", "<form", "http-equiv=\"refresh\"" })
                await Assert.That(html.Contains(privateValue, StringComparison.OrdinalIgnoreCase)).IsFalse().Because("The error page must not expose provider details or resubmit a mutation.");
            var link = System.Text.RegularExpressions.Regex.Match(html, "<a[^>]+href=\"([^\"]+)\"[^>]*>Review current plan and billing</a>");
            await Assert.That(link.Success).IsTrue();
            var billingPath = WebUtility.HtmlDecode(link.Groups[1].Value);
            await Assert.That(billingPath).IsEqualTo("/organization?tab=billing&organizationId=" + organizationId);
            using var beforeReview = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/plans/probe"));
            using var reviewed = await first.GetAsync(billingPath);
            await Assert.That(reviewed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var afterReview = JsonDocument.Parse(await first.GetStringAsync("/__test/billing/plans/probe"));
            await Assert.That(afterReview.RootElement.GetProperty("mutations").GetArrayLength()).IsEqualTo(beforeReview.RootElement.GetProperty("mutations").GetArrayLength());
            using var conflicting = await HttpJourney.PostFormAsync(replica, "/billing/change", new() { ["_csrf"] = replicaCsrf, ["plan"] = "team", ["interval"] = "monthly" });
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
            await Assert.That(await preview.Content.ReadAsStringAsync()).Contains("$6.66 estimated by Stripe");
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
        var returnPath = "/organization?newOrganization=project&organizationId=" + Uri.EscapeDataString(businessId);
        using var login = await HttpJourney.LoginAsync(replica, email, password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/organization?tab=billing");
        using var armed = await HttpJourney.PostFormAsync(first, "/__test/billing/checkouts/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "missing-index" });
        await Assert.That(armed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var checkoutForm = new Dictionary<string, string> { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "annual", ["return"] = returnPath };
        using var interrupted = await HttpJourney.PostFormAsync(first, "/billing/checkout", checkoutForm);
        await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var interruptedBody = await interrupted.Content.ReadAsStringAsync();
        await Assert.That(interrupted.Content.Headers.ContentType!.MediaType).IsEqualTo("application/problem+json");
        using var interruptedProblem = JsonDocument.Parse(interruptedBody);
        await Assert.That(interruptedProblem.RootElement.GetProperty("status").GetInt32()).IsEqualTo(503);
        await Assert.That(interruptedProblem.RootElement.GetProperty("title").GetString()).IsEqualTo("Service Unavailable");
        await Assert.That(interruptedProblem.RootElement.GetProperty("detail").GetString()).IsEqualTo("Checkout is temporarily unavailable. Return to billing to reconcile before trying again.");
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

    [Test]
    public async Task ReloadingBillingGrantsACheckoutWhoseReturnNeverRan()
    {
        using var client = fixture.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await SignUpPaidAsync(client, "plan-stuck-" + suffix + "@example.test", "E2E-plan-stuck-" + suffix, suffix);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        using var creation = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "Stuck checkout " + suffix });
        await Assert.That(creation.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var business = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        var businessId = business.RootElement.GetProperty("organizationId").GetString()!;
        using var started = await HttpJourney.PostFormAsync(client, "/billing/checkout", new()
        { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "annual", ["return"] = "/organization?tab=billing" });
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        // Stripe completes the checkout, but the browser never reaches the return URL.
        using var paid = await client.GetAsync(started.Headers.Location);
        await Assert.That(paid.StatusCode).IsEqualTo(HttpStatusCode.Found);

        using var other = new HttpRequestMessage(HttpMethod.Post, "/billing/checkout")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly", ["return"] = "/organization?tab=billing" }) };
        other.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        other.Headers.Accept.ParseAdd("text/html");
        using var blocked = await client.SendAsync(other);
        await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because("a browser form post gets a page, not problem+json");
        await Assert.That(BrowserAudit.WithoutWorkspace(new Uri(fixture.Origin, blocked.Headers.Location!.OriginalString).ToString())).IsEqualTo(new Uri(fixture.Origin, "/organization?tab=billing&payment=blocked").ToString());
        using var notice = await client.GetAsync(blocked.Headers.Location);
        await Assert.That(await notice.Content.ReadAsStringAsync()).Contains("Checkout did not open");

        using var context = JsonDocument.Parse(await client.GetStringAsync("/api/v1/dashboard/context"));
        var org = context.RootElement.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == businessId);
        await Assert.That(org.GetProperty("billingStatus").GetString()).IsEqualTo("active").Because("reloading billing reconciles the completed checkout");
        await Assert.That(org.GetProperty("paymentMethodValidated").GetBoolean()).IsTrue();
        using var cancel = await HttpJourney.PostFormAsync(client, "/billing/checkout/cancel", new() { ["_csrf"] = csrf });
        await Assert.That(cancel.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because("canceling a paid checkout reconciles instead of dead-ending");
        await Assert.That(cancel.Headers.Location!.OriginalString).Contains("payment=success");
    }

    internal static async Task SignUpPaidAsync(HttpClient client, string email, string password, string suffix, string interval = "annual")
    {
        using var verification = await HttpJourney.LoginAsync(client, email, password, "/onboarding?plan=starter", signUp: true);
        await Assert.That(verification.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(client, "/onboarding?plan=starter");
        using var information = await HttpJourney.PostFormAsync(client, "/onboarding/step", new()
        { ["_csrf"] = csrf, ["step"] = "account-information", ["displayName"] = "Plan race " + suffix });
        await Assert.That(information.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var payment = await HttpJourney.PostFormAsync(client, "/onboarding/step", new()
        { ["_csrf"] = csrf, ["step"] = "payment-method", ["plan"] = "starter", ["interval"] = interval });
        await Assert.That(payment.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var completed = await client.GetAsync(payment.Headers.Location);
        await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(completed.Headers.Location!.OriginalString.StartsWith("/billing/checkout/return?session_id=", StringComparison.Ordinal)).IsTrue();
        using var callback = await client.GetAsync(completed.Headers.Location);
        await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(BrowserAudit.WithoutWorkspace(new Uri(client.BaseAddress!, callback.Headers.Location!.OriginalString).ToString())).IsEqualTo(new Uri(client.BaseAddress!, "/onboarding?payment=success").ToString());
        using var confirmation = await client.GetAsync(callback.Headers.Location);
        await Assert.That(confirmation.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var project = await HttpJourney.PostFormAsync(client, "/onboarding/step", new()
        { ["_csrf"] = csrf, ["step"] = "project", ["name"] = "Plan race project " + suffix, ["region"] = "GRU" });
        await Assert.That(project.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
    }
}
