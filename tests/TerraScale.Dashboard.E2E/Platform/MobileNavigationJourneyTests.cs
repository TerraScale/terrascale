using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class MobileNavigationJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task NotificationsOpenedFromTheMobileDrawerCloseOnlyTheTopmostDialog()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        var persona = scenario.Personas.Seeded;
        await page.GotoAsync(new Uri(scenario.Origin, "/login").ToString());
        await page.Locator("#email").FillAsync(persona.Email);
        await page.Locator("#password").FillAsync(persona.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(scenario.Origin.ToString());
        await page.Locator("#ts-nav-toggle").ClickAsync();
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'ts-nav-close'");
        var account = page.Locator("details.ts-account summary");
        var dialog = page.Locator("#ts-notifications-dialog");

        foreach (var dismissal in new[] { "Escape", "Close", "Backdrop" })
        {
            await account.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Tab");
            await Assert.That(await page.Locator("#ts-notifications-menu").EvaluateAsync<bool>("el => document.activeElement === el")).IsTrue();
            var opened = await page.RunAndWaitForResponseAsync(
                () => page.Keyboard.PressAsync("Enter"),
                response => response.Request.Method == "GET" && new Uri(response.Url).AbsolutePath == "/notifications/dialog");
            await Assert.That(opened.Status).IsEqualTo(200);
            await dialog.Locator(".ts-notification-list article").Nth(3).WaitForAsync();
            await Assert.That(await dialog.GetAttributeAsync("open")).IsNotNull();
            await Assert.That(await page.Locator("#ts-nav-toggle").GetAttributeAsync("aria-expanded")).IsEqualTo("true");
            await Assert.That(await page.Locator(".ts-inset").GetAttributeAsync("inert")).IsNotNull();
            await Assert.That(await page.Locator("details.ts-account").GetAttributeAsync("open")).IsNull();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).FocusAsync();
            await page.Keyboard.PressAsync("Shift+Tab");
            await Assert.That(await dialog.EvaluateAsync<bool>("el => el.contains(document.activeElement)")).IsTrue();
            await page.Keyboard.PressAsync("Tab");
            await Assert.That(await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).EvaluateAsync<bool>("el => document.activeElement === el")).IsTrue();

            if (dismissal == "Escape") await page.Keyboard.PressAsync("Escape");
            else if (dismissal == "Close") await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            else await page.Mouse.ClickAsync(4, 4);
            await page.WaitForFunctionAsync("() => !document.querySelector('#ts-notifications-dialog')?.open");
            await Assert.That(await page.Locator("#ts-nav-toggle").GetAttributeAsync("aria-expanded")).IsEqualTo("true")
                .Because(dismissal + " dismisses only the topmost notification dialog, preserving the underlying drawer.");
            await page.WaitForFunctionAsync("() => document.activeElement === document.querySelector('details.ts-account summary')");
            await Assert.That(await page.Locator(".ts-inset").GetAttributeAsync("inert")).IsNotNull();
        }

        await page.Keyboard.PressAsync("Escape");
        await page.WaitForFunctionAsync("() => document.querySelector('#ts-nav-toggle')?.getAttribute('aria-expanded') === 'false' && document.activeElement?.id === 'ts-nav-toggle'");
        await Assert.That(await page.Locator(".ts-inset").GetAttributeAsync("inert")).IsNull();
        await Assert.That(page.Url).IsEqualTo(scenario.Origin.ToString());
        await Assert.That(errors.ToArray()).IsEmpty();
    }

    [Test]
    public async Task MobileDrawerClosesByButtonEscapeBackdropAndNavigationThenResetsOnDesktopResize()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);

        var persona = scenario.Personas.Seeded;
        await NavigateAsync("/login");
        await page.Locator("#email").FillAsync(persona.Email);
        await page.Locator("#password").FillAsync(persona.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(scenario.Origin.ToString());

        var toggle = page.Locator("#ts-nav-toggle");
        var close = page.Locator("#ts-nav-close");
        var sidebar = page.Locator("#ts-navigation");
        var inset = page.Locator(".ts-inset");
        await Assert.That(await toggle.GetAttributeAsync("aria-expanded")).IsEqualTo("false");
        await Assert.That(await sidebar.IsVisibleAsync()).IsFalse();

        await toggle.ClickAsync();
        await WaitForDrawerAsync(true);
        await Assert.That(await sidebar.IsVisibleAsync()).IsTrue();
        await Assert.That(await inset.GetAttributeAsync("inert")).IsNotNull();
        await Assert.That(await page.Locator(".ts-skip-link").GetAttributeAsync("inert")).IsNotNull();
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'ts-nav-close'");

        // The close button is the first control when the drawer opens. Both
        // focus directions should remain in the drawer at its boundaries.
        await page.Keyboard.PressAsync("Shift+Tab");
        await Assert.That(await page.Locator("details.ts-account summary").EvaluateAsync<bool>("el => document.activeElement === el"))
            .IsTrue().Because("Shift+Tab from the first drawer control wraps to its last visible control.");
        await page.Keyboard.PressAsync("Tab");
        await Assert.That(await close.EvaluateAsync<bool>("el => document.activeElement === el"))
            .IsTrue().Because("Tab from the last drawer control wraps to Close navigation.");

        // The organization chooser is a native details/summary control. Its
        // opened choices remain part of the keyboard sequence inside the drawer.
        var contextMenu = page.Locator("details.ts-context-menu");
        var contextTrigger = contextMenu.Locator("summary");
        await contextTrigger.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assert.That(await contextMenu.GetAttributeAsync("open")).IsNotNull();
        var firstOrganization = contextMenu.Locator("button[data-organization-option]").First;
        await page.Keyboard.PressAsync("Tab");
        await Assert.That(await firstOrganization.EvaluateAsync<bool>("el => document.activeElement === el"))
            .IsTrue().Because("Open organization choices remain keyboard reachable in the mobile drawer.");
        await contextTrigger.ClickAsync();

        // Opening the account menu adds its links and sign-out control to the
        // same focus cycle; the final action must wrap back to Close navigation.
        var accountTrigger = page.Locator("details.ts-account summary");
        await accountTrigger.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assert.That(await page.Locator("details.ts-account").GetAttributeAsync("open")).IsNotNull();
        await page.Keyboard.PressAsync("Tab");
        await Assert.That(await page.GetByRole(AriaRole.Link, new() { Name = "Profile", Exact = true }).EvaluateAsync<bool>("el => document.activeElement === el"))
            .IsTrue().Because("Account menu links join the mobile drawer's keyboard sequence.");
        await page.Keyboard.PressAsync("Tab");
        await Assert.That(await page.Locator("#ts-notifications-menu").EvaluateAsync<bool>("el => document.activeElement === el"))
            .IsTrue();
        await page.Keyboard.PressAsync("Tab");
        var signOut = page.Locator("details.ts-account form[action='/api/v1/auth/logout'] button");
        await Assert.That(await signOut.EvaluateAsync<bool>("el => document.activeElement === el")).IsTrue();
        await page.Keyboard.PressAsync("Tab");
        await Assert.That(await close.EvaluateAsync<bool>("el => document.activeElement === el"))
            .IsTrue().Because("Tab from the final open account-menu control wraps to Close navigation.");
        await Assert.That(await sidebar.GetAttributeAsync("role")).IsEqualTo("dialog");
        await Assert.That(await sidebar.GetAttributeAsync("aria-modal")).IsEqualTo("true");

        await close.ClickAsync();
        await WaitForDrawerAsync(false);
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'ts-nav-toggle'");
        await Assert.That(await sidebar.IsVisibleAsync()).IsFalse();
        await Assert.That(await sidebar.GetAttributeAsync("role")).IsNull();
        await Assert.That(await sidebar.GetAttributeAsync("aria-modal")).IsNull();
        await Assert.That(await page.Locator("#ts-nav-toggle").EvaluateAsync<bool>("el => document.activeElement === el")).IsTrue();

        await toggle.ClickAsync();
        await WaitForDrawerAsync(true);
        await page.Keyboard.PressAsync("Escape");
        await WaitForDrawerAsync(false);
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'ts-nav-toggle'");
        await Assert.That(await sidebar.IsVisibleAsync()).IsFalse();
        await Assert.That(await sidebar.GetAttributeAsync("role")).IsNull();
        await Assert.That(await sidebar.GetAttributeAsync("aria-modal")).IsNull();
        await Assert.That(await page.Locator("#ts-nav-toggle").EvaluateAsync<bool>("el => document.activeElement === el")).IsTrue();

        await toggle.ClickAsync();
        await WaitForDrawerAsync(true);
        await page.Mouse.ClickAsync(350, 400);
        await WaitForDrawerAsync(false);
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'ts-nav-toggle'");
        await Assert.That(await sidebar.IsVisibleAsync()).IsFalse();
        await Assert.That(await sidebar.GetAttributeAsync("role")).IsNull();
        await Assert.That(await sidebar.GetAttributeAsync("aria-modal")).IsNull();

        await toggle.ClickAsync();
        await WaitForDrawerAsync(true);
        await page.Locator("#ts-navigation a[href='/databases']").ClickAsync();
        await page.WaitForURLAsync(new Uri(scenario.Origin, "/databases").ToString());
        await Assert.That(await page.Locator("main").CountAsync()).IsEqualTo(1);

        await toggle.ClickAsync();
        await WaitForDrawerAsync(true);
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'ts-nav-close'");
        // Assert dynamic modal semantics and their resize cleanup after the
        // keyboard boundary regressions above have been exercised.
        await Assert.That(await sidebar.GetAttributeAsync("role")).IsEqualTo("dialog");
        await Assert.That(await sidebar.GetAttributeAsync("aria-modal")).IsEqualTo("true");
        await page.SetViewportSizeAsync(1440, 900);
        await WaitForDrawerAsync(false);
        await Assert.That(await sidebar.IsVisibleAsync()).IsTrue();
        await Assert.That(await sidebar.GetAttributeAsync("role")).IsNull();
        await Assert.That(await sidebar.GetAttributeAsync("aria-modal")).IsNull();
        await Assert.That(await toggle.GetAttributeAsync("aria-expanded")).IsEqualTo("false");
        await Assert.That(await inset.GetAttributeAsync("inert")).IsNull();
        await Assert.That(await page.Locator(".ts-skip-link").GetAttributeAsync("inert")).IsNull();
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'ts-navigation'");
        await Assert.That(errors.ToArray()).IsEmpty();

        async Task NavigateAsync(string path)
        {
            var response = await page.GotoAsync(new Uri(scenario.Origin, path).ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(response!.Status).IsEqualTo(200);
        }

        async Task WaitForDrawerAsync(bool open)
        {
            var expanded = open ? "true" : "false";
            await page.WaitForFunctionAsync(
                "expected => document.querySelector('#ts-nav-toggle')?.getAttribute('aria-expanded') === expected && " +
                "document.querySelector('.ts-shell')?.classList.contains('nav-open') === (expected === 'true') && " +
                "document.querySelector('.ts-inset')?.hasAttribute('inert') === (expected === 'true')",
                expanded);
        }
    }
}
