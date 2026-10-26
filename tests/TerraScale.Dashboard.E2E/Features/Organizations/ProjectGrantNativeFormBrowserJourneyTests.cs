using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

public sealed partial class TsAuthPlatformJourneyTests
{
    [Test, Category("Slow")]
    public async Task ProjectGrantsSubmitNativelyWithDatastarAndRequireRemovalConfirmation()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("native-grant-admin");
        var owner = platform.Principal("native-grant-owner");
        var target = platform.Principal("native-grant-target");
        var org = platform.Producer.AddOrganization("Native grants", owner.Subject);
        var project = platform.Producer.AddProject(org, "Selected grants", user.Subject);
        platform.Producer.AddMember(org, user.Subject, user.Email, "viewer");
        platform.Producer.AddMember(org, target.Subject, target.Email, "viewer");
        await platform.StoreAsync(new OrganizationRecord { Id = org });
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });
        var errors = new List<string>();
        await BrowserAudit.SignInAsync(page, platform.Origin, "/project/members?organizationId=" + org + "&projectId=" + project, user.Email, user.Password);
        var output = BrowserAudit.Output(Directory.GetCurrentDirectory(), "q03-project-grant-mutations");
        var destination = new Uri(platform.Origin, "/project/members?organizationId=" + org + "&projectId=" + project).ToString();
        await using var recording = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, StorageState = await page.Context.StorageStateAsync(), RecordVideoDir = output, ViewportSize = new() { Width = 1440, Height = 1000 }, ColorScheme = ColorScheme.Dark });
        await recording.RouteAsync("**/css/terrascale.css*", async route => { var response = await route.FetchAsync(); await route.FulfillAsync(new() { Response = response, Body = await response.TextAsync() + "\n.ts-account-trigger,.ts-account-card,main tbody td:nth-child(2),[id^=edit-grant-] label,[id^=edit-grant-] h2+p,[id^=remove-grant-] h2+p{visibility:hidden!important}" }); });
        await page.Context.CloseAsync();
        page = await recording.NewPageAsync();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(destination);
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Grant or change explicit role", Exact = true });
        var row = page.Locator("tr[data-project-grant-user='" + target.Subject + "']");
        await WaitForProjectNativeFormAsync(page);
        await page.GetByLabel("Existing organization member user ID", new() { Exact = true }).FillAsync(new string('!', 26));
        await SubmitProjectNativeFormAsync(page, submit, "/api/v1/dashboard/project/grants", 422);
        await Assert.That(await page.GetByRole(AriaRole.Alert).InnerTextAsync()).Contains("canonical user ULID");
        await ShotAsync("grant-invalid-user");
        foreach (var role in new[] { "member", "admin" })
        {
            await WaitForProjectNativeFormAsync(page);
            if (role == "member")
            {
                await page.GetByLabel("Existing organization member user ID", new() { Exact = true }).FillAsync(target.Subject);
                await page.GetByLabel("Explicit project role", new() { Exact = true }).SelectOptionAsync(role);
                await SubmitProjectNativeFormAsync(page, submit, "/api/v1/dashboard/project/grants", 303);
            }
            else
            {
                await row.GetByRole(AriaRole.Button, new() { Name = "Change role", Exact = true }).ClickAsync();
                var edit = page.Locator("#edit-grant-" + target.Subject);
                await edit.GetByLabel("Explicit role for " + target.Email, new() { Exact = true }).SelectOptionAsync(role);
                await SubmitProjectNativeFormAsync(page, edit.GetByRole(AriaRole.Button, new() { Name = "Save role", Exact = true }), "/api/v1/dashboard/project/grants", 303);
            }
            await page.GetByText("Explicit project grant updated.", new() { Exact = true }).WaitForAsync();
            await Assert.That(await row.Locator("td").Nth(2).InnerTextAsync()).IsEqualTo(role);
            await ShotAsync("grant-role-" + role);
        }
        await WaitForProjectNativeFormAsync(page);
        await row.GetByRole(AriaRole.Button, new() { Name = "Remove explicit grant", Exact = true }).ClickAsync();
        var dialog = page.Locator("#remove-grant-" + target.Subject);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assert.That(await dialog.IsVisibleAsync()).IsFalse();
        var providerPath = "/v1/terrascale/organizations/" + org + "/projects/" + project + "/members/" + target.Subject;
        await Assert.That(platform.Producer.Count("DELETE", providerPath, user.Subject)).IsEqualTo(0);
        await row.GetByRole(AriaRole.Button, new() { Name = "Remove explicit grant", Exact = true }).ClickAsync();
        await Assert.That(await dialog.InnerTextAsync()).Contains("Organization-implied access and independent service keys remain");
        await ShotAsync("grant-remove-confirmation");
        await SubmitProjectNativeFormAsync(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Remove only this grant", Exact = true }),
            "/api/v1/dashboard/project/grants", 303);
        await page.GetByText("Explicit project grant updated.", new() { Exact = true }).WaitForAsync();
        await Assert.That(await row.CountAsync()).IsEqualTo(0);
        await Assert.That(platform.Producer.Count("PUT", providerPath, user.Subject)).IsEqualTo(2);
        await Assert.That(platform.Producer.Count("DELETE", providerPath, user.Subject)).IsEqualTo(1);
        await Assert.That(errors).IsEmpty();
        await Assert.That(platform.Producer.Failures).IsEmpty();
        await ShotAsync("grant-removed");
        await page.CloseAsync(); await recording.CloseAsync();
        await page.Video!.SaveAsAsync(Path.Combine(output, "grant-mutation-flow.webm"));
        Task ShotAsync(string name) => page.ScreenshotAsync(new() { Path = Path.Combine(output, name + ".png"), FullPage = true, Caret = ScreenshotCaret.Hide, Animations = ScreenshotAnimations.Disabled });
    }
}
