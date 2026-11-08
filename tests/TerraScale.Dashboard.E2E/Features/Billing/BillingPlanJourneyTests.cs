using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
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
                await page.Locator("#displayName").FillAsync("Plan owner " + suffix);
                await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Save and continue");
                var starter = page.Locator("input[name=plan][value=starter]");
                await page.Locator("label").Filter(new() { Has = starter }).ClickAsync();
                await Assert.That(await starter.IsCheckedAsync()).IsTrue();
                await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Continue to checkout on Starter");
                await page.Locator("#projectName").FillAsync("Plan project " + suffix);
                await page.Locator("#region").SelectOptionAsync("GRU");
                await SubmitAsync(page, "form:is([action=\"/onboarding/step\"],[action^=\"/onboarding/step?\"])", "Create project and open Dashboard");

                await ChooseAsync("pro");
                await Assert.That(await page.Locator("#review-billing-plan").InnerTextAsync()).Contains("$6.66 estimated by Stripe (including $1.66 in prorations)");
                await ScreenshotAsync("upgrade-review-desktop");
                await ScenarioAsync("pending");
                await ConfirmAsync();
                await page.GetByText("Awaiting payment", new() { Exact = true }).WaitForAsync();
                await Assert.That(await page.GetByRole(AriaRole.Link, new() { Name = "Complete payment (opens in a new tab)", Exact = true }).GetAttributeAsync("href"))
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
                await PostAsync("/billing/change", new() { ["plan"] = "starter", ["interval"] = "monthly" });
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
                await guestPage.Locator("#displayName").FillAsync("Plan member " + suffix);
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
                if (guest is not null)
                {
                    await guest.DisposeAsync();
                }
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
                if (expected is { } status)
                {
                    await Assert.That((await response).Status).IsEqualTo(status).Because(path);
                }
                else
                {
                    await response;
                }
            }
            async Task<IAPIResponse> PostAsync(string path, Dictionary<string, string> fields, int expected = 303)
            {
                var form = owner.APIRequest.CreateFormData().Set("_csrf", await page.Locator("input[name=_csrf]").First.InputValueAsync());
                foreach (var (key, value) in fields)
                {
                    form.Set(key, value);
                }

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
            await Assert.That(foreignReturn.Content.Headers.ContentType!.MediaType).IsEqualTo("text/html");
            var foreignHtml = await foreignReturn.Content.ReadAsStringAsync();
            foreach (var text in new[] { "<html lang=\"en\"", "<title>Checkout unavailable · TerraScale</title>", "role=\"alert\"", "Checkout is unavailable. Return to billing to check your current plan before trying again.", ">Return to billing</a>" })
            {
                await Assert.That(foreignHtml).Contains(text);
            }

            await Assert.That(foreignHtml.Contains("<form", StringComparison.Ordinal)).IsFalse().Because("The recovery page never resubmits a billing mutation.");
            using var foreignCancel = await HttpJourney.PostFormAsync(client, "/checkout/foreign-checkout/cancel", new() { ["_csrf"] = csrf });
            await Assert.That(foreignCancel.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
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
}
