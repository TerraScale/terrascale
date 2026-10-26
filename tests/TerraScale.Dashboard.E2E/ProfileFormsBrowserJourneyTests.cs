using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>]
[Category("Slow")]
public sealed class ProfileFormsBrowserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingSaveAnnouncesBusyAndPreventsRepeatedSubmissionsThenRecovers(bool language)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await BrowserAudit.SignInAsync(page, fixture.Origin, returnTo: "/profile");
        var form = page.Locator(language ? "#profile-language form" : "#profile-details form");
        var button = form.GetByRole(AriaRole.Button);
        var path = "/api/v1/dashboard/profile" + (language ? "/language" : "");
        var submissions = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && new Uri(request.Url).AbsolutePath == path) Interlocked.Increment(ref submissions);
        };
        // Observe the first native submit after the app's window listeners have
        // marked it busy, then send repeat events through the same browser guards.
        await page.EvaluateAsync("""
            selector => {
                const events = [];
                window.addEventListener('submit', event => {
                    if (event.target.matches(selector)) events.push(event);
                }, true);
                window.addEventListener('submit', event => {
                    if (!event.target.matches(selector) || events.length !== 1) return;
                    const form = event.target, button = form.querySelector('button');
                    for (let attempt = 0; attempt < 2; attempt++) {
                        form.dispatchEvent(new SubmitEvent('submit', { bubbles: true, cancelable: true, submitter: button }));
                    }
                    sessionStorage.setItem('profile-submit-events', JSON.stringify(events.map(event => ({
                        prevented: event.defaultPrevented, busy: button.getAttribute('aria-busy'), disabled: button.getAttribute('aria-disabled')
                    }))));
                });
            }
            """, language ? "#profile-language form" : "#profile-details form");
        if (language) await page.Locator("#interfaceLanguage").SelectOptionAsync("pt-BR");
        else await page.Locator("#displayName").FillAsync("Browser draft");
        await button.ClickAsync();
        await Expect(page.Locator(".ts-alert-success")).ToHaveTextAsync(language ? "Language preference saved." : "Profile saved.");
        await Expect(page.Locator(language ? "#profile-language button" : "#profile-details button")).Not.ToHaveAttributeAsync("aria-busy", "true");
        var events = await page.EvaluateAsync<JsonElement>("() => JSON.parse(sessionStorage.getItem('profile-submit-events'))");
        await Assert.That(events.GetArrayLength()).IsEqualTo(3);
        await Assert.That(events[0].GetProperty("busy").GetString()).IsEqualTo("true");
        await Assert.That(events[0].GetProperty("disabled").GetString()).IsEqualTo("true");
        await Assert.That(events[0].GetProperty("prevented").GetBoolean()).IsFalse();
        foreach (var index in new[] { 1, 2 }) await Assert.That(events[index].GetProperty("prevented").GetBoolean()).IsTrue();
        await Assert.That(submissions).IsEqualTo(1);
        if (language) return;
        await page.Locator("#phoneNumber").FillAsync(new string('9', 65));
        await page.Locator("#profile-details button").ClickAsync();
        await Expect(page.Locator("#phoneNumber")).ToHaveAttributeAsync("aria-invalid", "true");
        await Expect(page.Locator("#phoneNumber")).ToHaveAttributeAsync("aria-describedby", "phoneNumber-error");
        await Expect(page.Locator("#phoneNumber-error")).ToHaveTextAsync("Enter a valid phone number.");
        await Expect(page.Locator("#phoneNumber")).ToBeFocusedAsync();
        await Expect(page.Locator("#profile-details button")).Not.ToHaveAttributeAsync("aria-busy", "true");
        await page.Locator("#phoneNumber").FillAsync("+15555550100");
        await page.Locator("#profile-details button").ClickAsync();
        await Expect(page.Locator(".ts-alert-success")).ToHaveTextAsync("Profile saved.");
        await Expect(page.Locator("#phoneNumber")).Not.ToHaveAttributeAsync("aria-invalid", "true");
    }
}
