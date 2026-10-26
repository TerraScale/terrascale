using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace TerraScale.Dashboard.E2E.Features.AuthenticationManagement;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class AuthenticationConfirmationBrowserJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("clients", "clients", "delete")]
    [Arguments("users", "users", "delete")]
    [Arguments("sessions", "sessions", "revoke")]
    [Arguments("social", "providers", "delete")]
    public async Task KeyboardConfirmationCancelsOrSubmitsOneCsrfProtectedNativeMutation(string section, string family, string operation)
    {
        // Every case owns its application pair, tenant data and Chromium context.
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var cspViolations = new ConcurrentQueue<string>();
        await context.ExposeBindingAsync("recordCspViolation", (BindingSource _, string directive) => cspViolations.Enqueue(directive));
        await context.AddInitScriptAsync("""
            document.addEventListener('securitypolicyviolation', event => void recordCspViolation(event.violatedDirective));
            """);
        var page = await context.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        var path = "/auth/" + section;
        await BrowserAudit.SignInAsync(page, scenario.Origin, returnTo: path);
        await page.GotoAsync(new Uri(scenario.Origin, path).ToString());
        if (family == "providers")
        {
            await page.Locator("#auth-provider-google").GetByRole(AriaRole.Button, new() { Name = "Configure", Exact = true }).ClickAsync();
            await page.Locator("#auth-provider-google-client-id").FillAsync("confirmation-journey");
            await page.Locator("#auth-provider-google-secret").FillAsync("synthetic-test-only-secret");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save Google", Exact = true }).ClickAsync();
            await Expect(page.Locator("#auth-provider-google")).ToContainTextAsync("Configured");
        }
        var first = page.Locator($"form[action^='/api/v1/auth-management/{family}/'][action*='/{operation}']").First;
        var action = (await first.GetAttributeAsync("action"))!;
        var actionPath = new Uri(scenario.Origin, action).AbsolutePath;
        var form = page.Locator($"form[action='{action}']");
        var button = form.GetByRole(AriaRole.Button);
        var posts = new ConcurrentQueue<IRequest>();
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && new Uri(request.Url).AbsolutePath == actionPath) posts.Enqueue(request);
        };
        var dialogs = new ConcurrentQueue<string>();
        var accept = false;
        var firstDialogHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Dialog += (sender, dialog) => _ = HandleDialogAsync(dialog);
        async Task HandleDialogAsync(IDialog dialog)
        {
            dialogs.Enqueue(dialog.Type);
            if (accept) await dialog.AcceptAsync();
            else await dialog.DismissAsync();
            firstDialogHandled.TrySetResult();
        }

        await Assert.That(errors.ToArray()).IsEmpty();
        await Assert.That(cspViolations).IsEmpty();
        await button.FocusAsync();
        await Expect(button).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await firstDialogHandled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(dialogs.ToArray()).IsEquivalentTo(new[] { "confirm" });
        await Expect(button).Not.ToHaveAttributeAsync("aria-busy", "true");
        await Expect(page.Locator("body")).Not.ToHaveClassAsync(new Regex("ts-nav-pending"));
        await page.ReloadAsync();
        await Expect(form).ToBeVisibleAsync();
        await Assert.That(posts).IsEmpty().Because("Cancelling confirmation sends no mutation and preserves the record after reload.");

        // An accepted confirmation still goes through the server's CSRF validation.
        accept = true;
        await form.Locator("input[name='_csrf']").EvaluateAsync("el => el.value = 'invalid'");
        var refused = await page.RunAndWaitForResponseAsync(() => button.PressAsync("Space"), IsMutation);
        await Assert.That(refused.Status).IsEqualTo(403);
        await page.GotoAsync(new Uri(scenario.Origin, path).ToString());
        await Expect(form).ToBeVisibleAsync();
        await Assert.That(posts.Count).IsEqualTo(1);
        await Assert.That(cspViolations).IsEmpty();

        var csrf = await form.Locator("input[name='_csrf']").InputValueAsync();
        var pendingState = new TaskCompletionSource<(string Busy, int Spinners, bool NavigationPending)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.ExposeBindingAsync("recordNativePending", (BindingSource _, string busy, int spinners, bool navigationPending) =>
            pendingState.TrySetResult((busy, spinners, navigationPending)));
        await form.EvaluateAsync("""
            form => window.addEventListener('submit', event => {
                if (event.target !== form) return;
                const button = event.submitter;
                const busy = button.getAttribute('aria-busy');
                const spinners = button.querySelectorAll('.ts-btn-spinner').length;
                const navigationPending = document.body.classList.contains('ts-nav-pending');
                button.click();
                void recordNativePending(busy, spinners, navigationPending);
            })
            """);
        // Hold this request at the browser boundary to inspect the real shared pending state without sleeps.
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(new Uri(scenario.Origin, action).ToString(), async route =>
        {
            posted.TrySetResult();
            await release.Task;
            await route.ContinueAsync();
        });
        var confirmations = dialogs.Count + 1;
        var submitted = await page.RunAndWaitForResponseAsync(async () =>
        {
            var press = button.PressAsync("Space");
            try
            {
                await posted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                // Observe submit feedback before the native navigation pauses the browser execution context.
                var state = await pendingState.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Assert.That(state.Busy).IsEqualTo("true");
                await Assert.That(state.Spinners).IsEqualTo(1);
                await Assert.That(state.NavigationPending).IsTrue();
                await Assert.That(dialogs.Count).IsEqualTo(confirmations);
                await Assert.That(posts.Count).IsEqualTo(2);
            }
            finally { release.TrySetResult(); await press; }
        }, IsMutation);
        await Assert.That(submitted.Status).IsEqualTo(303);
        await Assert.That(submitted.Request.IsNavigationRequest).IsTrue();
        await Assert.That(QueryHelpers.ParseQuery(submitted.Request.PostData!)["_csrf"].ToString()).IsEqualTo(csrf);
        await Expect(form).ToHaveCountAsync(0);
        await page.ReloadAsync();
        await Expect(form).ToHaveCountAsync(0);
        await Assert.That(posts.Count).IsEqualTo(2).Because("Each accepted confirmation sends exactly one POST.");
        await Assert.That(dialogs.ToArray()).IsEquivalentTo(new[] { "confirm", "confirm", "confirm" });
        await Assert.That(errors).IsEmpty();
        await Assert.That(cspViolations).IsEmpty();

        bool IsMutation(IResponse response) => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == actionPath;
    }
}
