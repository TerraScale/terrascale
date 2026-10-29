using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    public sealed partial class TsAuthPlatformJourneyTests
    {
        [Test, Category("Slow")]
        public async Task ExplicitProjectGrantsNavigatePagesAndRenderEmptyAndErrorStates()
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("grant-browser");
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var loginPage = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });
            await BrowserAudit.SignInAsync(loginPage, platform.Origin, "/project/members", user.Email, user.Password);
            var destination = loginPage.Url;
            var scope = new Uri(destination).Query;
            var org = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(scope)["organizationId"].ToString();
            var project = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(scope)["projectId"].ToString();
            var targets = new[] { platform.Principal("grant-browser-a"), platform.Principal("grant-browser-b") };
            foreach (var target in targets)
            {
                platform.Producer.AddCollaborator(org, target.Subject, project, "member");
            }

            var output = BrowserAudit.Output(Directory.GetCurrentDirectory(), "q03-project-grant-list");
            await using var recording = await browser.NewContextAsync(new()
            {
                IgnoreHTTPSErrors = true,
                StorageState = await loginPage.Context.StorageStateAsync(),
                ViewportSize = new() { Width = 1440, Height = 1000 },
                ColorScheme = ColorScheme.Dark,
                RecordVideoDir = output
            });
            await recording.RouteAsync("**/css/terrascale.css*", async route =>
            {
                var response = await route.FetchAsync();
                await route.FulfillAsync(new() { Response = response, Body = await response.TextAsync() + "\n.ts-account-trigger,.ts-account-card,main tbody td:nth-child(2){visibility:hidden!important}" });
            });
            await loginPage.Context.CloseAsync();
            var page = await recording.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(destination);
            await Assert.That(await page.Locator("tr[data-project-grant-user]").CountAsync()).IsEqualTo(2);
            await Assert.That(await page.Locator(".ts-account-trigger").EvaluateAsync<string>("el => getComputedStyle(el).visibility")).IsEqualTo("hidden");
            await ShotAsync("grant-list");
            await page.GetByRole(AriaRole.Link, new() { Name = "Next page", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "First page", Exact = true }).WaitForAsync();
            await Assert.That(await page.Locator("tr[data-project-grant-user]").CountAsync()).IsEqualTo(1);
            await ShotAsync("grant-next-page");
            foreach (var subject in targets.Select(target => target.Subject).Append(user.Subject))
            {
                platform.Producer.RemoveGrant(org, subject, project);
            }

            await page.GetByRole(AriaRole.Link, new() { Name = "First page", Exact = true }).ClickAsync();
            await page.GetByText("No explicit project grants on this page. Organization-implied access may still apply.", new() { Exact = true }).WaitForAsync();
            await Assert.That(await page.Locator("tr[data-project-grant-user]").CountAsync()).IsEqualTo(0);
            await ShotAsync("grant-empty");
            var rejected = await page.GotoAsync(destination + "&after=a&after=b");
            await Assert.That(rejected!.Status).IsEqualTo(400);
            await Assert.That(await page.GetByRole(AriaRole.Alert).InnerTextAsync()).Contains("Choose one page cursor");
            await ShotAsync("grant-invalid-page");
            await page.GetByRole(AriaRole.Link, new() { Name = "Open the first page", Exact = true }).ClickAsync();
            await page.GetByText("No explicit project grants on this page. Organization-implied access may still apply.", new() { Exact = true }).WaitForAsync();
            await Assert.That(errors).IsEmpty();
            await Assert.That(platform.Producer.Failures).IsEmpty();
            await page.CloseAsync();
            await recording.CloseAsync();
            await page.Video!.SaveAsAsync(Path.Combine(output, "grant-list-flow.webm"));
            Task ShotAsync(string name) => page.ScreenshotAsync(new() { Path = Path.Combine(output, name + ".png"), FullPage = true, Caret = ScreenshotCaret.Hide });
        }
    }
}
