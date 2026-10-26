using System.Collections.Concurrent;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace TerraScale.Dashboard.E2E.Features.AuthenticationManagement;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class AuthenticationCopyBrowserJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task KeyboardCopyReportsClipboardOutcomesWithoutPersistingTheOnceOnlySecret()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        // Simulate clipboard outcomes without relying on OS permissions or the system clipboard.
        await context.AddInitScriptAsync("""
            globalThis.copyMode = 'success';
            Object.defineProperty(navigator, 'clipboard', { configurable: true, value: {
                writeText(value) {
                    if (copyMode === 'throw') throw new Error('Clipboard unavailable');
                    if (copyMode === 'reject') return Promise.reject(new DOMException('Denied', 'NotAllowedError'));
                    if (copyMode === 'pending') return new Promise(resolve => globalThis.finishCopy = () => {
                        globalThis.copiedValue = value;
                        resolve();
                    });
                    globalThis.copiedValue = value;
                    return Promise.resolve();
                }
            }});
            """);
        var page = await context.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        await BrowserAudit.SignInAsync(page, scenario.Origin, returnTo: "/auth/overview");
        await page.GotoAsync(new Uri(scenario.Origin, "/auth/overview").ToString());
        await Copy("auth-issuer", "issuer", "Enter");
        await Copy("auth-discovery", "discovery URL", "Space");
        await Copy("auth-sign-in-url", "hosted sign-in URL", "Enter");
        await Expect(page.Locator("#auth-issuer-copy-status")).ToHaveTextAsync("issuer copied.");

        var discovery = page.GetByRole(AriaRole.Button, new() { Name = "Copy discovery URL", Exact = true });
        var discoveryStatus = page.Locator("#auth-discovery-copy-status");
        await page.EvaluateAsync("() => globalThis.copyMode = 'pending'");
        await discovery.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(discovery).ToBeDisabledAsync();
        await Expect(discovery).ToBeFocusedAsync();
        await Expect(discoveryStatus).ToHaveTextAsync("Copying discovery URL…");
        await page.EvaluateAsync("() => globalThis.finishCopy()");
        await Expect(discoveryStatus).ToHaveTextAsync("discovery URL copied.");
        await Expect(discovery).ToBeEnabledAsync();
        await Expect(discovery).ToBeFocusedAsync();
        foreach (var mode in new[] { "reject", "throw", "missing" })
        {
            await page.EvaluateAsync("mode => { globalThis.copyMode = mode; if (mode === 'missing') Object.defineProperty(navigator, 'clipboard', { value: undefined }); }", mode);
            await discovery.FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await Expect(discoveryStatus).ToContainTextAsync("Copy failed.");
            await Expect(discoveryStatus).ToContainTextAsync("Ctrl+C or Command+C");
            await Expect(discovery).ToBeEnabledAsync();
            await Expect(page.Locator("#auth-discovery")).ToBeFocusedAsync();
            await Assert.That(await page.EvaluateAsync<string>("() => getSelection().toString()"))
                .IsEqualTo(await page.Locator("#auth-discovery").TextContentAsync());
        }

        await page.GotoAsync(new Uri(scenario.Origin, "/auth/social").ToString());
        await Copy("auth-provider-google-callback", "Google callback URL", "Space");
        await page.GotoAsync(new Uri(scenario.Origin, "/auth/clients").ToString());
        await page.Locator("#auth-client-name").FillAsync("Copy journey app");
        await page.Locator("#auth-client-type").SelectOptionAsync("confidential");
        await page.Locator("#auth-client-uris").FillAsync("https://example.com/callback");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add application", Exact = true }).ClickAsync();
        await Expect(page.Locator("#auth-client-secret")).ToBeVisibleAsync();
        await Copy("auth-created-client-id", "new client ID", "Enter");
        await Copy("auth-client-secret", "client secret", "Space");
        var secret = await page.Locator("#auth-client-secret").TextContentAsync();
        var clientId = await page.Locator("#auth-created-client-id").TextContentAsync();
        await Assert.That(await page.EvaluateAsync<bool>("secret => !JSON.stringify([localStorage, sessionStorage]).includes(secret)", secret)).IsTrue();
        await page.GotoAsync(new Uri(scenario.Origin, "/auth/clients").ToString());
        await page.ReloadAsync();
        await Expect(page.Locator("#auth-client-secret")).ToHaveCountAsync(0);
        await Assert.That((await page.ContentAsync()).Contains(secret!, StringComparison.Ordinal)).IsFalse();
        await Copy("auth-client-" + clientId, "client ID for Copy journey app", "Enter");
        await Assert.That(errors.ToArray()).IsEmpty();

        async Task Copy(string id, string label, string key)
        {
            var value = page.Locator("#" + id);
            var button = page.GetByRole(AriaRole.Button, new() { Name = "Copy " + label, Exact = true });
            await button.FocusAsync();
            await page.Keyboard.PressAsync(key);
            await Expect(page.Locator("#" + id + "-copy-status")).ToHaveTextAsync(label + " copied.");
            await Assert.That(await page.EvaluateAsync<string>("() => globalThis.copiedValue")).IsEqualTo(await value.TextContentAsync());
        }
    }
}
