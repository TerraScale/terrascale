using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.AuthenticationManagement
{
    [ClassDataSource<DashboardRootFixture>]
    [Category("Slow")]
    public sealed class HostedBrandingBrowserJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments(1440, 1000)]
        [Arguments(390, 844)]
        public async Task DraftPreviewSaveAndAuthenticatedOrganizationSelectionWorkAtEveryWidth(int width, int height)
        {
            await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var output = BrowserAudit.Output(fixture.Root, "hosted-auth-v3/" + width);
            await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height }, ColorScheme = ColorScheme.Light, RecordVideoDir = output });
            var page = await context.NewPageAsync();
            var errors = new ConcurrentQueue<string>();
            page.PageError += (_, error) => errors.Enqueue(error);
            var persona = scenario.Personas.Routes;
            await BrowserAudit.SignInAsync(page, scenario.Origin, returnTo: "/api/v1/auth/me", email: persona.Email, password: persona.Password);
            await page.GotoAsync(new Uri(scenario.Origin, "/auth/branding").ToString());
            var preview = page.Locator("#auth-brand-preview");
            await Shot("01-branding-before");
            var savedName = await page.Locator("#auth-brand-name").InputValueAsync();
            await page.Locator("#auth-brand-name").FillAsync("Unsaved draft");
            await page.Locator("#auth-brand-email-message").FillAsync("Unsaved email message");
            await page.Locator("#auth-branding-form button[type=reset]").ClickAsync();
            await Assert.That(await page.Locator("#auth-brand-name").InputValueAsync()).IsEqualTo(savedName);
            await Assert.That(await page.Locator("#auth-brand-email-message").InputValueAsync()).IsEqualTo("");
            await page.Locator("[data-brand-defaults]").ClickAsync();
            await Assert.That(await page.Locator("#auth-brand-title-size").InputValueAsync()).IsEqualTo("24");
            await page.Locator("#auth-brand-name").FillAsync("Preview application");
            await page.Locator("#auth-brand-email-message").FillAsync("Welcome to the application.\nYour team");
            await page.Locator("#auth-brand-mode").SelectOptionAsync("light");
            await page.Locator("#auth-brand-title-size").FillAsync("32");
            await page.Locator("#auth-brand-background_color").FillAsync("#ffffff");
            await page.Locator("#auth-brand-text_color").FillAsync("#0a0a0a");
            await page.Locator("#auth-brand-footer").FillAsync("Privacy | https://example.com/privacy");
            await page.WaitForFunctionAsync("() => document.querySelector('[data-brand-heading]').textContent === 'Sign in to Preview application' && document.getElementById('auth-brand-preview').dataset.theme === 'light'");
            await Assert.That(await preview.Locator("h3").EvaluateAsync<string>("el => getComputedStyle(el).fontSize")).IsEqualTo("32px");
            await Assert.That(await page.Locator("[data-brand-dirty]").IsVisibleAsync()).IsTrue();
            await Assert.That(await preview.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in code" }).IsVisibleAsync()).IsTrue();
            await Assert.That(await preview.GetByRole(AriaRole.Button, new() { Name = "Use a password instead" }).IsVisibleAsync()).IsTrue();
            await Assert.That(await preview.Locator("#auth-preview-password").IsVisibleAsync()).IsFalse();
            await Shot("02-sign-in-draft");
            await page.Locator("[data-brand-page=sign-up]").ClickAsync();
            await Assert.That(await preview.Locator("#auth-preview-password").IsVisibleAsync()).IsTrue();
            await Assert.That(await preview.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true }).IsVisibleAsync()).IsTrue();
            await Shot("03-sign-up-draft");
            await page.Locator("[data-brand-page=reset]").ClickAsync();
            await Assert.That(await preview.Locator("h3").InnerTextAsync()).IsEqualTo("Reset your password");
            await Assert.That(await preview.Locator("[data-brand-subtitle]").InnerTextAsync()).IsEqualTo("Enter your account email and we will send you a reset code.");
            await Assert.That(await preview.GetByRole(AriaRole.Button, new() { Name = "Email me a reset code" }).IsVisibleAsync()).IsTrue();
            await Assert.That(await preview.Locator("#auth-preview-password").IsVisibleAsync()).IsFalse();
            await Assert.That(await preview.Locator(":scope > :last-child").InnerTextAsync()).IsEqualTo("Powered by TerraScale");
            await Assert.That(await page.Locator("#auth-brand-name").InputValueAsync()).IsEqualTo("Preview application");
            await Shot("04-reset-draft");
            await page.Locator("#auth-branding-form button[type=submit]").ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/auth/branding" && new Uri(url).Query.Contains("saved=1", StringComparison.Ordinal));
            await Assert.That(await page.Locator("#auth-brand-name").InputValueAsync()).IsEqualTo("Preview application");
            await Assert.That(await preview.GetAttributeAsync("data-theme")).IsEqualTo("light");
            await Assert.That(await page.Locator("#auth-brand-email-message").InputValueAsync()).IsEqualTo("Welcome to the application.\nYour team");
            await Assert.That(await preview.Locator("[data-brand-footer] a").GetAttributeAsync("href")).IsEqualTo("https://example.com/privacy");
            await Assert.That(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth")).IsTrue();
            await Shot("05-branding-saved");
            // Only saved first factors appear; preview navigation never edits those methods.
            await page.GotoAsync(new Uri(scenario.Origin, "/auth/sign-in").ToString());
            await page.Locator("#auth-method-password").UncheckAsync();
            await page.Locator("#auth-method-sign_up").UncheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save sign-in methods" }).ClickAsync();
            await page.GotoAsync(new Uri(scenario.Origin, "/auth/branding").ToString());
            await Assert.That(await preview.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in code" }).IsVisibleAsync()).IsTrue();
            await Assert.That(await preview.GetByRole(AriaRole.Button, new() { Name = "Use a password instead" }).CountAsync()).IsEqualTo(0);
            await Assert.That(await page.Locator("[data-brand-page=sign-up]").IsVisibleAsync()).IsFalse();
            await Assert.That(await page.Locator("[data-brand-page=reset]").IsVisibleAsync()).IsFalse();
            await Shot("06-email-code-only");
            await page.GotoAsync(new Uri(scenario.Origin, "/auth/sign-in").ToString());
            await page.Locator("#auth-method-password").CheckAsync();
            await page.Locator("#auth-method-passcode").UncheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save sign-in methods" }).ClickAsync();
            await page.GotoAsync(new Uri(scenario.Origin, "/auth/branding").ToString());
            await Assert.That(await preview.GetByRole(AriaRole.Button, new() { Name = "Continue with a password" }).IsVisibleAsync()).IsTrue();
            await Assert.That(await preview.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in code" }).CountAsync()).IsEqualTo(0);
            await Shot("07-password-only");
            // Create a second membership through the existing authorized flow using only synthetic identity data.
            var organization = context.APIRequest.CreateFormData();
            organization.Set("_csrf", await page.Locator("#auth-branding-form input[name=_csrf]").InputValueAsync());
            organization.Set("step", "organization");
            organization.Set("name", "Example Company");
            var created = await context.APIRequest.PostAsync(new Uri(scenario.Origin, "/api/v1/dashboard/organization/new-organization").ToString(), new() { Form = organization, MaxRedirects = 0 });
            await Assert.That(created.Status).IsEqualTo(303);
            await page.GotoAsync(new Uri(scenario.Origin, "/organizations/choose?returnTo=%2Fauth%2Fconfiguration").ToString());
            await Assert.That(await page.GetByRole(AriaRole.Heading, new() { Name = "Choose an organization" }).IsVisibleAsync()).IsTrue();
            await Assert.That(await page.Locator(".ts-org-picker > :last-child").InnerTextAsync()).IsEqualTo("Powered by TerraScale");
            await Assert.That(await page.GetByRole(AriaRole.Link, new() { Name = "Create organization", Exact = true }).GetAttributeAsync("href")).IsEqualTo("/organization?newOrganization=organization");
            await AssertPickerPalette("rgb(255, 255, 255)", "rgb(10, 10, 10)"); // System follows the light OS preference.
            await Shot("08-picker-system-light");
            await page.Locator("#org-picker-theme").SelectOptionAsync("dark");
            await AssertPickerPalette("rgb(21, 21, 21)", "rgb(242, 242, 242)");
            await Shot("09-picker-dark");
            await page.Locator("#org-picker-theme").SelectOptionAsync("light");
            await Assert.That(await page.Locator("html").GetAttributeAsync("data-theme")).IsEqualTo("light");
            await AssertPickerPalette("rgb(255, 255, 255)", "rgb(10, 10, 10)");
            await Shot("10-picker-light");
            await page.Locator("#org-picker-theme").SelectOptionAsync("system");
            await AssertPickerPalette("rgb(255, 255, 255)", "rgb(10, 10, 10)");
            await Assert.That(await page.Locator(".ts-sidebar").CountAsync()).IsEqualTo(0);
            await page.Locator(".ts-org-picker-option").Filter(new() { HasText = "Personal" }).ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/auth/configuration");
            await Assert.That(await page.Locator("html").GetAttributeAsync("data-theme")).IsEqualTo("dark");
            await Shot("11-configuration-before");
            await page.Locator("#auth-redirects").FillAsync("https://example.com/callback#fragment");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save configuration" }).ClickAsync();
            await Assert.That(await page.Locator("#auth-error").IsVisibleAsync()).IsTrue();
            await Shot("12-configuration-error");
            await page.Locator("#auth-redirects").FillAsync("https://example.com/callback");
            await page.Locator("#auth-lifetime").FillAsync("3600");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save configuration" }).ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).Query.Contains("saved=1", StringComparison.Ordinal));
            await Assert.That(await page.Locator("#auth-lifetime").InputValueAsync()).IsEqualTo("3600");
            await Shot("13-configuration-saved");
            await page.GotoAsync(new Uri(scenario.Origin, "/auth/danger").ToString());
            await Shot("14-danger-confirmation");
            await Assert.That(errors.ToArray()).IsEmpty();

            async Task Shot(string name)
            {
                await page.EvaluateAsync("() => document.fonts.ready");
                await page.ScreenshotAsync(new() { Path = Path.Combine(output, name + ".png"), FullPage = true, Caret = ScreenshotCaret.Hide });
            }

            async Task AssertPickerPalette(string canvas, string ink)
            {
                await Assert.That(await page.Locator(".ts-org-picker").EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor")).IsEqualTo(canvas);
                await Assert.That(await page.Locator(".ts-org-picker-card").EvaluateAsync<string>("el => getComputedStyle(el).color")).IsEqualTo(ink);
            }
        }
    }
}
