using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    public sealed partial class TsAuthPlatformJourneyTests
    {
        [Test, Category("Slow")]
        public async Task ProjectInvitationBrowserShowsOnlySelectedScopeAndRecoversFromInvalidPages()
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("project-invitation-browser");
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var login = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });
            await BrowserAudit.SignInAsync(login, platform.Origin, "/project/invitations", user.Email, user.Password);
            var destination = login.Url;
            var scope = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(destination).Query);
            var org = scope["organizationId"].ToString();
            var project = scope["projectId"].ToString();
            foreach (var role in new[] { "viewer", "member", "admin" })
            {
                platform.Producer.AddInvitation(org, role + "@example.test", role, user.Subject, project);
            }

            platform.Producer.AddInvitation(org, "org-sentinel@example.test", "member", user.Subject);
            var output = BrowserAudit.Output(Directory.GetCurrentDirectory(), "q03-project-invitation-list");
            await using var recording = await browser.NewContextAsync(new()
            {
                IgnoreHTTPSErrors = true,
                StorageState = await login.Context.StorageStateAsync(),
                RecordVideoDir = output,
                ViewportSize = new() { Width = 1440, Height = 1000 },
                ColorScheme = ColorScheme.Dark
            });
            await recording.RouteAsync("**/css/terrascale.css*", async route =>
            {
                var response = await route.FetchAsync();
                await route.FulfillAsync(new() { Response = response, Body = await response.TextAsync() + "\n.ts-account-trigger,.ts-account-card,main tbody td:first-child{visibility:hidden!important}" });
            });
            await login.Context.CloseAsync();
            var page = await recording.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(destination);
            await Assert.That(await page.Locator("tr[data-project-invitation]").CountAsync()).IsEqualTo(2);
            await Assert.That(await page.Locator(".ts-account-trigger").EvaluateAsync<string>("el => getComputedStyle(el).visibility")).IsEqualTo("hidden");
            await Assert.That(await page.Locator("main").InnerTextAsync()).DoesNotContain("org-sentinel");
            await ShotAsync("invitation-list");
            await page.GetByRole(AriaRole.Link, new() { Name = "Next page", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "First page", Exact = true }).WaitForAsync();
            await Assert.That(await page.Locator("tr[data-project-invitation]").CountAsync()).IsEqualTo(1);
            await ShotAsync("invitation-next-page");
            var rejected = await page.GotoAsync(destination + "&after=a&after=b");
            await Assert.That(rejected!.Status).IsEqualTo(400);
            await Assert.That(await page.GetByRole(AriaRole.Alert).InnerTextAsync()).Contains("Choose one page cursor");
            await ShotAsync("invitation-invalid-page");
            platform.Producer.ClearProjectInvitations(org, project);
            await page.GetByRole(AriaRole.Link, new() { Name = "Open the first page", Exact = true }).ClickAsync();
            await page.GetByText("No project invitations on this page.", new() { Exact = true }).WaitForAsync();
            await ShotAsync("invitation-empty");
            await Assert.That(errors).IsEmpty();
            await Assert.That(platform.Producer.Failures).IsEmpty();
            await page.CloseAsync();
            await recording.CloseAsync();
            await page.Video!.SaveAsAsync(Path.Combine(output, "invitation-list-flow.webm"));
            async Task ShotAsync(string name)
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(output, name + "-desktop.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled, Caret = ScreenshotCaret.Hide });
                await page.SetViewportSizeAsync(390, 844);
                await page.ScreenshotAsync(new() { Path = Path.Combine(output, name + "-mobile.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled, Caret = ScreenshotCaret.Hide });
                await page.SetViewportSizeAsync(1440, 1000);
            }
        }
    }
}
