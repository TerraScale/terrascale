using System.Collections.Concurrent;
using System.Net;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Platform
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class NavigationDiscoveryJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("")]
        [Arguments("?collection=orders")]
        public async Task AuthenticatedShellRendersSecondaryNavigationOverHttp(string query)
        {
            await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            var persona = scenario.Personas.Seeded;
            using var login = await HttpJourney.LoginAsync(client, persona.Email, persona.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var responses = await Task.WhenAll(new[] { "/", "/support", "/databases/" + ReferenceFixtures.PrimaryId + "/danger" + query }.Select(path => client.GetAsync(path)));
            foreach (var response in responses)
            {
                using var rendered = response;
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
                foreach (var label in new[] { "Support", "Profile", "Notifications", "Imports", "Connect", "Billing", "Members", "Projects" })
                {
                    await Assert.That(html).Contains("aria-label=\"" + label + "\"");
                }

                await Assert.That(html).Contains("No pages match");
                if (html.Contains("ts-db-tabs", StringComparison.Ordinal))
                {
                    await Assert.That(html).Contains("/databases/" + ReferenceFixtures.PrimaryId + "/backups");
                    await Assert.That(html).Contains("/databases/" + ReferenceFixtures.PrimaryId + "/documents" + query);
                }
            }
        }

        [Test, Category("Slow")]
        public async Task SecondaryPagesPreserveWorkspaceAndDatabaseSectionsRemainVisibleOnPhones()
        {
            await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 375, Height = 812 } });
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(15000);
            var errors = new ConcurrentQueue<string>();
            page.PageError += (_, error) => errors.Enqueue(error);
            var persona = scenario.Personas.Seeded;
            await BrowserAudit.SignInAsync(page, scenario.Origin, email: persona.Email, password: persona.Password);
            await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
            await Assert.That(await page.Locator(".ts-mobile-context").IsVisibleAsync()).IsTrue();
            await Assert.That(await page.Locator(".ts-mobile-context").EvaluateAsync<bool>("el => { const r = el.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= 68; }")).IsTrue();
            await page.Locator("#ts-nav-toggle").FocusAsync();
            await page.Keyboard.PressAsync("Control+k");
            var dialog = page.Locator("#ts-search-dialog[open]");
            var search = dialog.GetByRole(AriaRole.Searchbox);
            foreach (var label in new[] { "Support", "Profile", "Notifications", "Imports", "Connect", "Billing", "Members", "Projects", "Single sign-on", "Query subscriptions" })
            {
                await search.FillAsync(label);
                var link = dialog.GetByRole(AriaRole.Link, new() { Name = label, Exact = true });
                await link.WaitForAsync();
                await Assert.That(await link.GetAttributeAsync("href")).Contains("projectId=");
                await Assert.That(await link.GetAttributeAsync("href")).Contains("organizationId=");
                await Assert.That(await dialog.GetByText("No pages match", new() { Exact = true }).IsVisibleAsync()).IsFalse();
            }
            await search.FillAsync("no matching destination");
            await dialog.GetByText("No pages match", new() { Exact = true }).WaitForAsync();
            await search.FillAsync("Support");
            await page.Keyboard.PressAsync("ArrowUp");
            await Assert.That(await dialog.GetByRole(AriaRole.Link, new() { Name = "Support", Exact = true }).EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForFunctionAsync("() => document.activeElement?.id === 'ts-nav-toggle'");
            await page.Keyboard.PressAsync("Control+k");
            await search.FillAsync("Support");
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/support");
            await Assert.That(page.Url).Contains("projectId=");
            await page.GotoAsync(new Uri(scenario.Origin, "/databases/" + ReferenceFixtures.PrimaryId + "/danger").ToString());
            var tabs = page.Locator(".ts-db-tabs");
            await Assert.That(await tabs.EvaluateAsync<bool>("el => [...el.querySelectorAll('a')].every(a => { const r = a.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth; })")).IsTrue();
            await tabs.GetByRole(AriaRole.Link, new() { Name = "Danger zone", Exact = true }).FocusAsync();
            await Assert.That(await tabs.Locator("[aria-current=page]").InnerTextAsync()).IsEqualTo("Danger zone");
            await page.Keyboard.PressAsync("Control+k");
            await search.FillAsync("Backups");
            var backups = dialog.GetByRole(AriaRole.Link, new() { Name = "Backups", Exact = true });
            await backups.WaitForAsync();
            await Assert.That(await backups.GetAttributeAsync("href")).Contains("/databases/" + ReferenceFixtures.PrimaryId + "/backups");
            await Assert.That(await backups.Locator("small").InnerTextAsync()).Contains("Database ·");
            await Assert.That(errors.ToArray()).IsEmpty();
        }
    }
}
