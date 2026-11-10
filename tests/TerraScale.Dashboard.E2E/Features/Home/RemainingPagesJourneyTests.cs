using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Home
{
    [ClassDataSource<DashboardRootFixture>]
    [Category("Slow")]
    public sealed class RemainingPagesJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task SignedInWalkOpensMembersUsageBackupsOrganizationSettingsAndAuth()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            await using var app = await environment.CreateTestHostAsync(fixture.Root, 0, "http://127.0.0.1");
            await app.StartAsync();
            var origin = new Uri(app.Urls.Single());
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
            var page = await context.NewPageAsync();
            await BrowserAudit.SignInAsync(page, origin);
            var listed = await page.GotoAsync(new Uri(origin, "/databases").ToString());
            await AssertHealthyAsync(page, listed, "/databases");
            var csrf = await page.Locator("input[name=\"_csrf\"]").First.InputValueAsync();
            var form = context.APIRequest.CreateFormData();
            form.Set("_csrf", csrf);
            form.Set("name", "walkdb");
            form.Set("homeRegion", "GRU");
            var created = await context.APIRequest.PostAsync(new Uri(origin, "/api/v1/dashboard/databases").ToString(), new() { Form = form, MaxRedirects = 0 });
            await Assert.That(created.Status).IsEqualTo(201);
            using var document = JsonDocument.Parse(await created.TextAsync());
            var id = document.RootElement.GetProperty("id").GetString()!;
            foreach (var path in new[]
            {
                "/organization?tab=members",
                "/usage",
                "/databases/" + Uri.EscapeDataString(id) + "/backups",
                "/organization",
                "/auth",
            })
            {
                var response = await page.GotoAsync(new Uri(origin, path).ToString());
                await AssertHealthyAsync(page, response, path);
            }
        }

        private static async Task AssertHealthyAsync(IPage page, IResponse? response, string path)
        {
            await Assert.That(response).IsNotNull().Because(path);
            await Assert.That(response!.Status).IsNotEqualTo(500).Because(path);
            await Assert.That((await page.ContentAsync()).Contains("unhandled exception", StringComparison.OrdinalIgnoreCase)).IsFalse().Because(path);
        }
    }
}
