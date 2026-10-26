using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class BillingBrowserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments(1440, 900, false)]
    [Arguments(390, 844, false)]
    [Arguments(390, 844, true)]
    public async Task PaidSignupInvoiceFiltersAndBranchSnapshot(int width, int height, bool holdImage)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var password = "E2E-billing-" + suffix;
        var email = "billing-" + suffix + "@example.test";
        var signedUp = await BrowserAudit.SignInAsync(page, fixture.Origin, "/onboarding?plan=starter", email, password, signUp: true);
        await Assert.That(signedUp!.Status).IsEqualTo(200);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Review your account information", Exact = true }).WaitForAsync();
        await SubmitAsync("/onboarding/step", "Save and continue →");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Choose your plan", Exact = true }).WaitForAsync();
        await page.Locator("label.ts-onboarding-plan").Filter(new() { Has = page.Locator("input[name=plan][value=starter]") }).ClickAsync();
        await SubmitAsync("/onboarding/step", "Continue to checkout →");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Create your first project", Exact = true }).WaitForAsync();
        await page.Locator("#projectName").FillAsync("Billing project " + suffix);
        await page.Locator("#region").SelectOptionAsync("GRU");
        await SubmitAsync("/onboarding/step", "Create project and open Dashboard →");
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == fixture.Origin.ToString());

        await NavigateAsync("/organization?tab=billing");
        await Assert.That(await page.Locator("#billing-summary").InnerTextAsync()).Contains("Starter");
        await Assert.That(await page.Locator("#billing-summary").InnerTextAsync()).Contains("Active");
        await Assert.That(await page.Locator(".ts-plan-current").GetByRole(AriaRole.Button, new() { Name = "Current plan", Exact = true }).IsDisabledAsync()).IsTrue();
        await Assert.That(await page.Locator("#recent-invoices tbody tr").CountAsync()).IsEqualTo(10);
        await Assert.That(await page.Locator("#recent-invoices tbody tr").First.Locator("a").CountAsync()).IsEqualTo(0);
        await Assert.That(await page.Locator("#billing-invoice-summary").InnerTextAsync()).Contains("24 recorded invoices");
        await Assert.That(await page.Locator("#billing-invoice-summary").InnerTextAsync()).Contains("Multiple currencies");
        await Assert.That((await page.Locator(".ts-plan-features").AllInnerTextsAsync()).All(text => !text.Contains("free trial", StringComparison.Ordinal))).IsTrue();
        await page.Locator("#invoice-status").SelectOptionAsync("paid");
        await page.Locator("#invoice-issued").SelectOptionAsync(DateTimeOffset.UtcNow.Year.ToString());
        var applied = await page.RunAndWaitForResponseAsync(() => page.GetByRole(AriaRole.Search, new() { Name = "Filter payment history" })
            .GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }).ClickAsync(),
            response => response.Request.Method == "GET" && new Uri(response.Url).AbsolutePath == "/organization");
        await Assert.That(applied.Status).IsEqualTo(200);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/organization" && QueryHelpers.ParseQuery(new Uri(url).Query)["tab"] == "billing" && QueryHelpers.ParseQuery(new Uri(url).Query)["status"] == "paid", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assert.That(await page.Locator("#recent-invoices tbody tr").CountAsync()).IsEqualTo(10);
        await Assert.That(await page.Locator("#recent-invoices .ts-pager").InnerTextAsync()).Contains("of 12");
        await page.GetByRole(AriaRole.Link, new() { Name = "Next", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/organization" && QueryHelpers.ParseQuery(new Uri(url).Query)["tab"] == "billing" && QueryHelpers.ParseQuery(new Uri(url).Query)["page"] == "2", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assert.That(await page.Locator("#recent-invoices tbody tr").CountAsync()).IsEqualTo(2);
        // Invoice navigation must work while an unrelated image is still loading.
        const string brandImage = "**/brand/logo-terrascale-principal.svg*";
        var imageRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseImage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (holdImage)
            await page.RouteAsync(brandImage, async route =>
            {
                imageRequested.TrySetResult();
                await releaseImage.Task;
                await route.ContinueAsync();
            });
        try
        {
            await page.GetByRole(AriaRole.Link, new() { Name = "Clear", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/organization" && QueryHelpers.ParseQuery(new Uri(url).Query)["tab"] == "billing" && !QueryHelpers.ParseQuery(new Uri(url).Query).ContainsKey("status"), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(await page.Locator("#invoice-status").InputValueAsync()).IsEqualTo("");
            await Assert.That(await page.Locator("#recent-invoices tbody tr").CountAsync()).IsEqualTo(10);
            if (holdImage)
            {
                await imageRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(releaseImage.Task.IsCompleted).IsFalse();
                await Assert.That(await page.EvaluateAsync<string>("document.readyState")).IsEqualTo("interactive");
            }
        }
        finally
        {
            releaseImage.TrySetResult();
            if (holdImage) await page.UnrouteAsync(brandImage);
        }
        if (holdImage) await page.WaitForLoadStateAsync(LoadState.Load);

        using var client = fixture.CreateClient();
        using var clientLogin = await HttpJourney.LoginAsync(client, email, password);
        await Assert.That(clientLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(client, "Billing branch " + suffix, password);
        var listing = await ToolAsync("list_invoices", new { page = 2 });
        await Assert.That(listing.GetProperty("pageCount").GetInt32()).IsEqualTo(3);
        await Assert.That(listing.GetProperty("total").GetInt32()).IsEqualTo(24);
        var paid = await ToolAsync("list_invoices", new { status = "paid", issued = DateTimeOffset.UtcNow.Year.ToString(), page = 999 });
        await Assert.That(paid.GetProperty("page").GetInt32()).IsEqualTo(2);
        await Assert.That(paid.GetProperty("invoices").GetArrayLength()).IsEqualTo(2);
        var invalid = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "list_invoices", arguments = new { status = "draft" } });
        await Assert.That(invalid.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(invalid.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()).Contains("status");

        var name = "snapshot-" + suffix;
        var sourceId = "";
        var branchId = "";
        try
        {
            var source = await ToolAsync("create_database", new { name, regions = new[] { "GRU", "SFO" } });
            sourceId = source.GetProperty("id").GetString()!;
            var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + sourceId + "/documents");
            using var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
            { ["_csrf"] = csrf, ["databaseId"] = sourceId, ["name"] = "customers" });
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await ToolAsync("create_document", new { databaseId = sourceId, collection = "customers", documentId = "01K78D0C000000000000000012", document = new { name = "Before branch" } });
            var branch = await ToolAsync("branch_database", new { databaseId = sourceId, name = name + "-copy" });
            branchId = branch.GetProperty("id").GetString()!;
            await ToolAsync("replace_document", new { databaseId = sourceId, collection = "customers", documentId = "01K78D0C000000000000000012", document = new { name = "After branch" } });
            var branchDocument = await ToolAsync("get_document", new { databaseId = branchId, collection = "customers", documentId = "01K78D0C000000000000000012" });
            await Assert.That(branchDocument.GetProperty("value").GetProperty("name").GetString()).IsEqualTo("Before branch");
            var sourceDocument = await ToolAsync("get_document", new { databaseId = sourceId, collection = "customers", documentId = "01K78D0C000000000000000012" });
            await Assert.That(sourceDocument.GetProperty("value").GetProperty("name").GetString()).IsEqualTo("After branch");
        }
        finally
        {
            if (branchId.Length > 0) await ToolAsync("delete_database", new { databaseId = branchId, confirmName = name + "-copy" });
            if (sourceId.Length > 0) await ToolAsync("delete_database", new { databaseId = sourceId, confirmName = name });
        }
        await page.GetByRole(AriaRole.Link, new() { Name = "Manage payment", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url).EndsWith("/__test/portal", StringComparison.Ordinal));
        await Assert.That(await page.GetByRole(AriaRole.Heading).InnerTextAsync()).IsEqualTo("Test-only Stripe customer portal");
        await Assert.That(errors.ToArray()).IsEmpty();
        var screenshots = Path.Combine(fixture.Root, "test-results", "tunit", "billing");
        Directory.CreateDirectory(screenshots);
        await NavigateAsync("/organization?tab=billing");
        await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, $"invoices-{width}{(holdImage ? "-pending-brand" : "")}.png"), FullPage = true });

        async Task NavigateAsync(string path)
        {
            var response = await page.GotoAsync(new Uri(fixture.Origin, path).ToString());
            await Assert.That(response!.Status).IsEqualTo(200);
        }
        async Task SubmitAsync(string path, string label, int status = 303, bool exact = true)
        {
            if (label.EndsWith(" →", StringComparison.Ordinal)) label = label[..^2];
            var button = page.Locator("form:is([action='" + path + "'],[action^='" + path + "?'])").GetByRole(AriaRole.Button, new() { Name = label, Exact = exact });
            await button.WaitForAsync();
            var response = await page.RunAndWaitForResponseAsync(() => button.ClickAsync(),
                response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == path);
            await Assert.That(response.Status).IsEqualTo(status);
        }
        async Task<JsonElement> ToolAsync(string name, object arguments)
        {
            var rpc = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
            var result = rpc.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(name + ": " + result.GetRawText());
            return result.GetProperty("structuredContent");
        }
    }
}
