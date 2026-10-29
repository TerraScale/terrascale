using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.PaymentWebhookCorrectness
{
    [Category("Slow")]
    public sealed class PaymentCurrencyEvidenceJourneyTests
    {
        [Test]
        [Arguments(1440, 900, "desktop")]
        [Arguments(390, 844, "mobile")]
        public async Task CaptureProviderValidZeroDecimalInvoice(int width, int height, string device)
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await PaymentWebhookAuditJourneyTests.CompleteCheckoutAsync(owner, "starter");
            scenario.Stripe.InvoiceCurrency = "jpy";
            scenario.Stripe.InvoiceAmount = 1500;
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
            var page = await context.NewPageAsync();
            await BrowserAudit.SignInAsync(page, scenario.Origin);
            await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
            await page.GotoAsync(new Uri(scenario.Origin, "/organization?tab=billing").ToString());
            var table = page.Locator("#recent-invoices .ts-table-wrap");
            await table.FocusAsync();
            await page.Keyboard.PressAsync("Shift+Tab");
            await page.Keyboard.PressAsync("Tab");
            await Assert.That(await table.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
            if (width < 768)
            {
                await page.Keyboard.PressAsync("ArrowRight");
                await page.WaitForFunctionAsync("() => document.querySelector('#recent-invoices .ts-table-wrap').scrollLeft > 0");
                await table.EvaluateAsync("el => { el.scrollLeft = el.scrollWidth; }");
            }
            await Assert.That(await table.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
            var accessibility = await BrowserAccessibility.RunAsync(page, selector: "#recent-invoices");
            await Assert.That(accessibility.Violations.Length).IsEqualTo(0);
            var evidence = Path.Combine(scenario.Root, "docs/reviews/2026-10-13/payment-webhook-correctness/evidence/test-results");
            Directory.CreateDirectory(evidence);
            await page.Locator("#recent-invoices").ScreenshotAsync(new() { Path = Path.Combine(evidence, "currency-" + device + ".png") });
            await page.Locator("#billing-invoice-summary").ScreenshotAsync(new() { Path = Path.Combine(evidence, "currency-" + device + "-summary.png") });
            using var git = new Process
            {
                StartInfo = new("git") { WorkingDirectory = scenario.Root, RedirectStandardOutput = true, UseShellExecute = false }
            };
            git.StartInfo.ArgumentList.Add("rev-parse");
            git.StartInfo.ArgumentList.Add("HEAD");
            git.Start();
            var head = (await git.StandardOutput.ReadToEndAsync()).Trim();
            await git.WaitForExitAsync();
            await Assert.That(git.ExitCode).IsEqualTo(0);
            await File.WriteAllTextAsync(Path.Combine(evidence, "currency-" + device + ".json"), JsonSerializer.Serialize(new
            {
                flow = "Invoice currency formatting",
                auditedSourceSha = "b719878083dffd9441e063bbfaccfa0dffa95abd",
                testedHeadSha = head,
                width,
                height,
                route = "/organization?tab=billing",
                screenshotSections = new[] { "#recent-invoices", "#billing-invoice-summary" },
                minorUnits = 1500,
                currency = "jpy",
                expected = "JPY 1500",
                observed = await page.Locator("#recent-invoices td.ts-num").InnerTextAsync(),
                keyboardTableFocus = true,
                keyboardHorizontalScroll = width < 768,
                scopedAxeViolations = accessibility.Violations.Length
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
