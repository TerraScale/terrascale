using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.BillingCheckout;

[ClassDataSource<BillingCheckoutAuditFixture>]
public sealed class BillingCheckoutAuditJourneyTests(BillingCheckoutAuditFixture fixture)
{
    private const string Billing = "/organization?tab=billing";

    [Test]
    public async Task CanceledUnpaidCheckoutDoesNotMarkPaidTierAsCurrent()
    {
        using var client = fixture.CreateClient();
        var csrf = await NewOrganizationAsync(client);
        using var checkout = await CheckoutAsync(client, csrf);
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var canceled = await HttpJourney.PostFormAsync(client, "/billing/checkout/cancel", new() { ["_csrf"] = csrf });
        await Assert.That(canceled.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var html = await client.GetStringAsync(canceled.Headers.Location);
        await Assert.That(html).Contains(">No plan</span>");
        var currentPaidTier = html.Contains("ts-plan-current", StringComparison.Ordinal);
        await RecordAsync("canceled-current-tier", new { summary = "No plan", currentPaidTier });
        await Assert.That(currentPaidTier).IsFalse().Because("A canceled unpaid checkout must not label a paid tier Current.");
        var chooser = Regex.Match(html, "<dialog id=\"change-billing-plan\".*?</dialog>", RegexOptions.Singleline).Value;
        await Assert.That(chooser.Length).IsGreaterThan(0);
        await Assert.That(Regex.IsMatch(chooser, "<input[^>]*name=\"plan\"[^>]*checked")).IsFalse().Because("The chooser must not preselect a plan the organization is not subscribed to.");
    }

    [Test]
    public async Task ConcurrentCheckoutAndCancelReplayKeepOneSessionAndAllowReplacement()
    {
        using var client = fixture.CreateClient();
        var csrf = await NewOrganizationAsync(client);
        var submitted = await Task.WhenAll(CheckoutAsync(client, csrf), CheckoutAsync(client, csrf));
        using var first = submitted[0];
        using var repeated = submitted[1];
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(repeated.Headers.Location).IsEqualTo(first.Headers.Location);
        using var different = await CheckoutAsync(client, csrf, "pro");
        await Assert.That(different.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        using var cancel = await HttpJourney.PostFormAsync(client, "/billing/checkout/cancel", new() { ["_csrf"] = csrf });
        using var cancelAgain = await HttpJourney.PostFormAsync(client, "/billing/checkout/cancel", new() { ["_csrf"] = csrf });
        await Assert.That(cancel.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(cancelAgain.Headers.Location).IsEqualTo(cancel.Headers.Location);
        using var replacement = await CheckoutAsync(client, csrf, "pro", "monthly");
        await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(replacement.Headers.Location).IsNotEqualTo(first.Headers.Location);
        using var canceled = await HttpJourney.PostFormAsync(client, "/billing/checkout/cancel", new() { ["_csrf"] = csrf });
        await Assert.That(canceled.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
    }

    [Test]
    public async Task InterruptedCheckoutRetryRepairsIndexWithoutReplacingSession()
    {
        using var client = fixture.CreateClient();
        var csrf = await NewOrganizationAsync(client);
        using var armed = await HttpJourney.PostFormAsync(client, "/__test/billing/checkouts/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "missing-index" });
        await Assert.That(armed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var failed = await CheckoutAsync(client, csrf);
        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var before = JsonDocument.Parse(await client.GetStringAsync("/__test/billing/checkouts/probe"));
        await Assert.That(before.RootElement.GetProperty("hasIndex").GetBoolean()).IsFalse();
        var id = before.RootElement.GetProperty("checkoutId").GetString();
        using var retry = await CheckoutAsync(client, csrf);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var after = JsonDocument.Parse(await client.GetStringAsync("/__test/billing/checkouts/probe"));
        await Assert.That(after.RootElement.GetProperty("hasIndex").GetBoolean()).IsTrue();
        await Assert.That(after.RootElement.GetProperty("checkoutId").GetString()).IsEqualTo(id);
        using var canceled = await HttpJourney.PostFormAsync(client, "/billing/checkout/cancel", new() { ["_csrf"] = csrf });
        await Assert.That(canceled.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
    }

    [Test]
    public async Task InterruptedCheckoutOffersRetryBackToBilling()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        var csrf = await NewOrganizationAsync(client);
        using var armed = await HttpJourney.PostFormAsync(client, "/__test/billing/checkouts/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "missing-index" });
        await Assert.That(armed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var failed = await CheckoutAsync(client, csrf);
        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var html = await failed.Content.ReadAsStringAsync();
        var retry = Regex.Match(html, "<a[^>]+href=\"([^\"]+)\"[^>]*>Try again</a>").Groups[1].Value;
        await RecordAsync("interrupted-checkout", new { status = (int)failed.StatusCode, retry });
        await Assert.That(retry).IsEqualTo(Billing).Because("A failed billing action must offer recovery in its original billing journey.");
    }

    [Test]
    public async Task PartiallyPaidInvoiceUsesRemainingAmountForOutstandingBalance()
    {
        using var client = fixture.CreateClient();
        var csrf = await NewOrganizationAsync(client);
        using var checkout = await CheckoutAsync(client, csrf);
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await InvoiceScenarioAsync(client, csrf, "partial-payment");
        var html = await client.GetStringAsync(Billing);
        var outstanding = Outstanding(html);
        await RecordAsync("partial-invoice", new { amountDue = 1234, amountPaid = 1000, amountRemaining = 234, outstanding });
        await Assert.That(outstanding).IsEqualTo("$2.34").Because("An open invoice with $10.00 already paid has only $2.34 outstanding.");
    }

    [Test]
    public async Task InvoiceFailureCanRetryThroughFiltersWithoutReportingAnEmptyHistory()
    {
        using var client = fixture.CreateClient();
        var csrf = await NewOrganizationAsync(client);
        using var checkout = await CheckoutAsync(client, csrf);
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var completed = await client.GetAsync(checkout.Headers.Location);
        await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.Found);
        using var callback = await client.GetAsync(completed.Headers.Location);
        await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await InvoiceScenarioAsync(client, csrf, "offline");
        var failed = await client.GetStringAsync(Billing);
        await Assert.That(failed).Contains("Payment history could not be loaded. Apply the filters to retry.");
        await Assert.That(failed).DoesNotContain("No invoices are recorded.");
        await InvoiceScenarioAsync(client, csrf, "normal");
        var restored = await client.GetStringAsync(Billing + "&status=paid");
        await Assert.That(restored).DoesNotContain("Payment history could not be loaded");
        await Assert.That(restored).Contains("of 12");
    }

    [Test, Category("Slow")]
    public async Task CancelCheckoutReturnsVisibleOutcomeAndCanStartDifferentPlan()
    {
        var cookies = new CookieContainer();
        using var client = fixture.CreateClient(cookies);
        var csrf = await NewOrganizationAsync(client);
        using var checkout = await CheckoutAsync(client, csrf);
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await BrowserContextAsync(browser, cookies, 390, 844);
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(fixture.Origin, Billing).ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel checkout", Exact = true }).PressAsync("Enter");
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == new Uri(fixture.Origin, Billing + "&payment=canceled").ToString());
        await Assert.That(await page.GetByRole(AriaRole.Button, new() { Name = "Cancel checkout", Exact = true }).CountAsync()).IsEqualTo(0);
        await CaptureAsync(page, "canceled-checkout-390");
        var message = await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Checkout canceled" }).CountAsync();
        using var replacement = await CheckoutAsync(client, csrf, "pro", "monthly");
        await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(replacement.Headers.Location).IsNotEqualTo(checkout.Headers.Location);
        using var cleanup = await HttpJourney.PostFormAsync(client, "/billing/checkout/cancel", new() { ["_csrf"] = csrf });
        await Assert.That(cleanup.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await RecordAsync("canceled-checkout", new { route = Billing + "&payment=canceled", message, replacementAccepted = true });
        await Assert.That(message).IsEqualTo(1).Because("Cancellation must announce its outcome and leave a visible route to retry.");
    }

    [Test, Category("Slow")]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task PlanChooserUsesPublishedCadencesEvenWithAnnualQuery(int width, int height)
    {
        var cookies = new CookieContainer();
        using var client = fixture.CreateClient(cookies);
        await NewOrganizationAsync(client);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await BrowserContextAsync(browser, cookies, width, height);
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(fixture.Origin, Billing + "&interval=annual").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Choose plan", Exact = true }).ClickAsync();
        await page.Locator("#change-billing-plan[open]").WaitForAsync();
        var annual = (await page.Locator("#change-billing-plan").InnerTextAsync()).Contains("$30.00 billed yearly", StringComparison.Ordinal);
        await CaptureAsync(page, $"annual-chooser-{width}");
        await RecordAsync($"annual-chooser-{width}", new { route = Billing + "&interval=annual", width, height, annual });
        await Assert.That(annual).IsTrue().Because("The chooser must show Starter’s yearly charge without offering unapproved billing intervals.");
    }

    [Test, Category("Slow")]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task KeyboardReviewCloseEscapeBackAndForwardStayUsable(int width, int height)
    {
        var cookies = new CookieContainer();
        using var client = fixture.CreateClient(cookies);
        await NewOrganizationAsync(client);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await BrowserContextAsync(browser, cookies, width, height);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(new Uri(fixture.Origin, Billing).ToString());
        var choose = page.GetByRole(AriaRole.Button, new() { Name = "Choose plan", Exact = true });
        await choose.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.Locator("#change-billing-plan[open]").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Close plan selection", Exact = true }).PressAsync("Enter");
        await Assert.That(await choose.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.PressAsync("Escape");
        await Assert.That(await choose.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
        await choose.ClickAsync();
        await page.Locator("#change-billing-plan input[name=plan][value=starter]").PressAsync("Space");
        await page.GetByRole(AriaRole.Button, new() { Name = "Review change", Exact = true }).PressAsync("Enter");
        await page.Locator("#review-billing-plan[open]").WaitForAsync();
        await Assert.That(await page.Locator("#review-billing-plan").InnerTextAsync()).Contains("US$ 30.00 / year");
        await Assert.That(await page.Locator("#review-billing-plan").InnerTextAsync()).Contains("After Stripe confirms checkout");
        await BrowserAudit.AssertAccessibleAsync(page, "The annual review must remain keyboard accessible.");
        await Assert.That(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).IsTrue();
        await CaptureAsync(page, $"keyboard-review-{width}");
        await page.GetByRole(AriaRole.Link, new() { Name = "Close plan review", Exact = true }).PressAsync("Enter");
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == new Uri(fixture.Origin, Billing).ToString());
        await page.GoBackAsync();
        await page.Locator("#review-billing-plan[open]").WaitForAsync();
        await page.GoForwardAsync();
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == new Uri(fixture.Origin, Billing).ToString());
        await Assert.That(await page.Locator("#review-billing-plan").CountAsync()).IsEqualTo(0);
        await Assert.That(errors).IsEmpty();
    }

    internal async Task<string> NewOrganizationAsync(HttpClient client)
    {
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using var organization = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "Billing audit " + Guid.NewGuid().ToString("N")[..8] });
        await Assert.That(organization.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var selected = await client.GetAsync(organization.Headers.Location);
        await Assert.That(selected.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await HttpJourney.CsrfAsync(client, Billing);
    }

    private static Task<HttpResponseMessage> CheckoutAsync(HttpClient client, string csrf, string plan = "starter", string? interval = null) =>
        HttpJourney.PostFormAsync(client, "/billing/checkout", new() { ["_csrf"] = csrf, ["plan"] = plan, ["interval"] = interval ?? (plan == "starter" ? "annual" : "monthly"), ["return"] = Billing });

    private static async Task InvoiceScenarioAsync(HttpClient client, string csrf, string scenario)
    {
        using var response = await HttpJourney.PostFormAsync(client, "/__audit/billing/invoices", new() { ["_csrf"] = csrf, ["scenario"] = scenario });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    private static string Outstanding(string html) => WebUtility.HtmlDecode(Regex.Match(html,
        "Outstanding balance</dt><dd[^>]*>([^<]+)</dd>").Groups[1].Value);

    private Task RecordAsync(string name, object value) => File.WriteAllTextAsync(Path.Combine(fixture.Evidence, name + ".json"),
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private async Task<IBrowserContext> BrowserContextAsync(IBrowser browser, CookieContainer cookies, int width, int height)
    {
        var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
        await context.AddCookiesAsync(cookies.GetCookies(fixture.Origin).Cast<System.Net.Cookie>().Select(cookie => new Microsoft.Playwright.Cookie
        { Name = cookie.Name, Value = cookie.Value, Url = fixture.Origin.ToString() }));
        return context;
    }

    private async Task CaptureAsync(IPage page, string name) => await page.ScreenshotAsync(new()
    { Path = Path.Combine(fixture.Evidence, name + ".png"), FullPage = true, Mask = [page.Locator(".ts-topbar-end"), page.Locator(".ts-sidebar-footer")] });
}
