using System.Net;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    public sealed partial class TsAuthPlatformJourneyTests
    {
        [Test, Category("Slow")]
        public async Task ProjectInvitationRevocationIsConfirmedNativeAndRecoversFromProviderFailure()
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("invitation-revoke-browser");
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var login = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });
            await BrowserAudit.SignInAsync(login, platform.Origin, "/project/invitations", user.Email, user.Password);
            var destination = login.Url;
            var scope = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(destination).Query);
            var org = scope["organizationId"].ToString(); var project = scope["projectId"].ToString();
            var token = platform.Producer.AddInvitation(org, "recipient@example.test", "member", user.Subject, project);
            var id = platform.Producer.InvitationId(org, token);
            var output = BrowserAudit.Output(Directory.GetCurrentDirectory(), "q03-project-invitation-revoke");
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
                await route.FulfillAsync(new() { Response = response, Body = await response.TextAsync() + "\n.ts-account-trigger,.ts-account-card,main tbody td:first-child,[id^=revoke-invitation-] h2+p{visibility:hidden!important}" });
            });
            await login.Context.CloseAsync();
            var page = await recording.NewPageAsync();
            var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(destination);
            var row = page.Locator("tr[data-project-invitation='" + id + "']");
            var dialog = page.Locator("#revoke-invitation-" + id);
            var path = "/v1/terrascale/organizations/" + org + "/invitations/" + id;
            await WaitForProjectNativeFormAsync(page);
            await row.GetByRole(AriaRole.Button, new() { Name = "Revoke invitation", Exact = true }).ClickAsync();
            await ShotAsync("invitation-revoke-confirmation");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assert.That(platform.Producer.Count("DELETE", path, user.Subject)).IsEqualTo(0);
            platform.Producer.RefuseNext("DELETE", path, HttpStatusCode.ServiceUnavailable, "unavailable", "Private provider detail");
            await row.GetByRole(AriaRole.Button, new() { Name = "Revoke invitation", Exact = true }).ClickAsync();
            await SubmitProjectNativeFormAsync(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Confirm revocation", Exact = true }), "/api/v1/dashboard/project/invitations/revoke", 503);
            await Assert.That(await page.GetByRole(AriaRole.Alert).InnerTextAsync()).Contains("could not be confirmed").And.DoesNotContain("Private provider detail");
            await ShotAsync("invitation-revoke-error");
            await page.GetByRole(AriaRole.Link, new() { Name = "Open the first page", Exact = true }).ClickAsync();
            await WaitForProjectNativeFormAsync(page);
            await row.GetByRole(AriaRole.Button, new() { Name = "Revoke invitation", Exact = true }).ClickAsync();
            await SubmitProjectNativeFormAsync(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Confirm revocation", Exact = true }), "/api/v1/dashboard/project/invitations/revoke", 303);
            await page.GetByText("Project invitation revoked. Its acceptance link no longer works.", new() { Exact = true }).WaitForAsync();
            await Assert.That(await row.Locator("td").Nth(2).InnerTextAsync()).IsEqualTo("revoked");
            await Assert.That(await row.GetByRole(AriaRole.Button).CountAsync()).IsEqualTo(0);
            await ShotAsync("invitation-revoked");
            await Assert.That(errors).IsEmpty(); await Assert.That(platform.Producer.Failures).IsEmpty();
            await page.CloseAsync(); await recording.CloseAsync();
            await page.Video!.SaveAsAsync(Path.Combine(output, "invitation-revoke-flow.webm"));
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
