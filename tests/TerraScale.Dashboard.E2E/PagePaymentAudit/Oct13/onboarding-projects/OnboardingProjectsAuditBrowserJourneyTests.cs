using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.OnboardingProjects
{
    [ClassDataSource<DashboardRootFixture>]
    [Category("Slow")]
    public sealed class OnboardingProjectsAuditBrowserJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task ProjectDialogKeyboardRepeatSubmitHistoryAndFailureEvidence()
        {
            var root = fixture.Root;
            await using var host = await OnboardingProjectsAuditHost.StartAsync(root);
            var origin = host.Origin;
            var output = Path.Combine(root, "docs/reviews/2026-10-13/onboarding-projects/test-results/approved-fixes/browser");
            Directory.CreateDirectory(output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var evidence = new List<object>();
            foreach (var (size, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
            {
                await using var context = await browser.NewContextAsync(new()
                { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height }, RecordVideoDir = output, RecordVideoSize = new() { Width = width, Height = height } });
                // Redact synthetic account identity before screenshots/video. Product markup is unchanged.
                await context.AddInitScriptAsync("""
                document.addEventListener('DOMContentLoaded', () => {
                  const style = document.createElement('style');
                  style.nonce = document.documentElement.dataset.nonce;
                  style.textContent = '.ts-account-email,.ts-account-name,.ts-account-card,#accountEmail,#account-information-title + p { visibility:hidden!important }';
                  document.head.append(style);
                  const text = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                  while (text.nextNode()) {
                    text.currentNode.nodeValue = text.currentNode.nodeValue.replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, '[redacted email]');
                  }
                }, {once:true});
                """);
                var page = await context.NewPageAsync();
                await BrowserAudit.SignInAsync(page, new Uri(origin), "/organization?tab=projects");
                await host.PlanAsync(await page.EvaluateAsync<string>("() => fetch('/api/v1/auth/me').then(response => response.json()).then(me => me.organizationId)"), PlanCatalog.For("pro").Id);
                var postCount = 0;
                page.Request += (_, request) => { if (request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/api/v1/dashboard/organization/projects") { Interlocked.Increment(ref postCount); } };
                await page.GotoAsync(origin + "/organization?tab=projects");
                await page.WaitForFunctionAsync("() => document.getElementById('ts-dev-updates')?.dataset.devReady === 'true'");
                var dialog = page.Locator("#create-project");
                var open = page.GetByRole(AriaRole.Button, new() { Name = "Create project", Exact = true }).First;
                await open.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await dialog.Locator("input[name=name]").WaitForAsync(new() { State = WaitForSelectorState.Visible });
                await dialog.Locator("input[name=name]").FocusAsync();
                await page.Keyboard.PressAsync("Shift+Tab");
                await Assert.That(await dialog.EvaluateAsync<bool>("el => el.contains(document.activeElement)")).IsTrue();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Assert.That(await dialog.EvaluateAsync<bool>("el => el.open")).IsFalse();
                await Assert.That(await open.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
                await open.ClickAsync();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
                await Assert.That(await dialog.EvaluateAsync<bool>("el => el.open")).IsFalse();
                await open.ClickAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assert.That(await dialog.EvaluateAsync<bool>("el => el.open")).IsFalse();
                await open.ClickAsync();
                await dialog.Locator("input[name=name]").FillAsync("Browser audit " + size);
                var bounds = await dialog.BoundingBoxAsync();
                await Assert.That(bounds!.X >= 0 && bounds.X + bounds.Width <= width + 1).IsTrue();
                var accessibility = await BrowserAccessibility.RunAsync(page, ["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]);
                await ShotAsync("dialog");
                var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var projectRoute = new Regex("^" + Regex.Escape(origin + "/api/v1/dashboard/organization/projects") + "(?:\\?.*)?$");
                await page.RouteAsync(projectRoute, async route =>
                {
                    arrived.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await route.ContinueAsync();
                });
                var submitted = page.WaitForResponseAsync(response => response.Request.Method == "POST" && BrowserAudit.WithoutWorkspace(response.Url) == origin + "/api/v1/dashboard/organization/projects");
                var submitting = await page.EvaluateAsync<JsonElement>("""
                () => { const form = document.querySelector('#create-project form');
                  const button = form.querySelector('[type=submit]'); const valid = form.checkValidity();
                  form.requestSubmit(button); form.requestSubmit(button);
                  return { valid, busy: button.getAttribute('aria-busy'), url: location.pathname }; }
                """);
                await Assert.That(submitting.GetProperty("busy").GetString()).IsEqualTo("true").Because(submitting.GetRawText());
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                // Playwright's normal screenshot waits for this paused native navigation.
                // CDP captures the actual rendered pending frame without waiting for its response.
                var capture = await context.NewCDPSessionAsync(page);
                var pendingFrame = await capture.SendAsync("Page.captureScreenshot", new Dictionary<string, object> { ["format"] = "png" });
                await File.WriteAllBytesAsync(Path.Combine(output, size + "-loading.png"),
                    Convert.FromBase64String(pendingFrame!.Value.GetProperty("data").GetString()!));
                release.TrySetResult();
                await submitted.WaitAsync(TimeSpan.FromSeconds(20));
                await page.WaitForFunctionAsync("() => !document.querySelector('#create-project[open]')");
                await page.UnrouteAsync(projectRoute);
                await Assert.That(Volatile.Read(ref postCount)).IsEqualTo(1);
                await Assert.That(await page.Locator("#projects tbody").GetByText("Browser audit " + size, new() { Exact = true }).CountAsync()).IsEqualTo(1);
                await ShotAsync("success");
                await page.GotoAsync(origin + "/organization?newProject=1#projects");
                var sidebarDialogCount = await page.Locator("#create-project").CountAsync();
                await Assert.That(sidebarDialogCount).IsEqualTo(1);
                await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible });
                await Assert.That(await dialog.EvaluateAsync<bool>("el => el.open")).IsTrue();
                await ShotAsync("sidebar-link-create-dialog");
                await page.GoBackAsync();
                await page.GoForwardAsync();
                await Assert.That(new Uri(BrowserAudit.WithoutWorkspace(page.Url)).Query).IsEqualTo("?newProject=1");
                await page.GotoAsync(origin + "/organization?tab=projects");
                await open.ClickAsync();
                await dialog.Locator("input[name=name]").FillAsync("Browser audit " + size);
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Create project", Exact = true }).ClickAsync();
                await dialog.GetByRole(AriaRole.Alert).Filter(new() { HasText = "A project with that name already exists." }).WaitForAsync();
                var errorDialogCount = await page.Locator("#create-project").CountAsync();
                await Assert.That(errorDialogCount).IsEqualTo(1);
                await Assert.That(await dialog.Locator("input[name=name]").InputValueAsync()).IsEqualTo("Browser audit " + size);
                await ShotAsync("duplicate-error-retained-form");
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Assert.That(await dialog.EvaluateAsync<bool>("el => el.open")).IsFalse();
                await open.ClickAsync();
                await dialog.Locator("input[name=name]").FillAsync("Recovered audit " + size);
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Create project", Exact = true }).ClickAsync();
                await page.WaitForFunctionAsync("() => !document.querySelector('#create-project[open]')");
                await Assert.That(await page.Locator("#projects tbody").GetByText("Recovered audit " + size, new() { Exact = true }).CountAsync()).IsEqualTo(1);
                await ShotAsync("retry-success");
                await Assert.That(Volatile.Read(ref postCount)).IsEqualTo(3);
                await context.ClearCookiesAsync();
                var signedUp = await BrowserAudit.SignInAsync(page, new Uri(origin), "/onboarding", "audit-browser-" + size + "@example.test", "Audit-browser-password-123", signUp: true);
                await Assert.That(signedUp!.Status).IsEqualTo(200);
                await page.Locator("#displayName").FillAsync("Audit browser " + size);
                await page.GetByRole(AriaRole.Button, new() { Name = "Save and continue" }).ClickAsync();
                await page.GetByRole(AriaRole.Heading, new() { Name = "Choose your plan" }).WaitForAsync();
                await ShotAsync("optional-plan");
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue on Free", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Heading, new() { Name = "Create your first project" }).WaitForAsync();
                await ShotAsync("first-project");
                var onboardingAccessibility = await BrowserAccessibility.RunAsync(page, ["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]);
                await page.GetByLabel("Project name", new() { Exact = true }).FillAsync("Browser onboarding " + size);
                await page.GetByRole(AriaRole.Button, new() { Name = "Create project and open Dashboard" }).ClickAsync();
                await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == origin + "/");
                await page.GotoAsync(origin + "/databases");
                await Assert.That(await page.Locator(".ts-empty-title").InnerTextAsync()).Contains("No databases");
                await ShotAsync("initial-empty-databases");
                evidence.Add(new
                {
                    size,
                    width,
                    height,
                    sidebarDialogCount,
                    errorDialogCount,
                    nativePosts = postCount,
                    keyboardCancelCloseEscape = "pass",
                    repeatSubmit = "one create POST",
                    backForward = "pass",
                    wcagViolations = accessibility.Violations.Select(v => new { v.Id, v.Impact, targets = v.Nodes.Select(n => n.Target.ToString()).ToArray() }).ToArray(),
                    onboardingWcagViolations = onboardingAccessibility.Violations.Select(v => new { v.Id, v.Impact, targets = v.Nodes.Select(n => n.Target.ToString()).ToArray() }).ToArray()
                });
                await page.CloseAsync();
                await context.CloseAsync();
                await page.Video!.SaveAsAsync(Path.Combine(output, size + "-project-flow.webm"));

                async Task ShotAsync(string state) => await page.ScreenshotAsync(new()
                { Path = Path.Combine(output, size + "-" + state + ".png"), FullPage = false, Animations = ScreenshotAnimations.Disabled, Mask = [page.Locator(".ts-account-email"), page.Locator(".ts-account-name"), page.Locator("#accountEmail")] });
            }
            await File.WriteAllTextAsync(Path.Combine(output, "observations.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }

    }
}
