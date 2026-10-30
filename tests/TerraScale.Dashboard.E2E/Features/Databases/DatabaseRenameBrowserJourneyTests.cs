using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    [Category("Slow")]
    public sealed class DatabaseRenameBrowserJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments(1440, 900)]
        [Arguments(390, 844)]
        public async Task NativeRenameSubmitsOnceShowsPendingAndRetainsRefusedDraft(int width, int height)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var patches = 0;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
            {
                if (http.Request.Method != "PATCH")
                {
                    return null;
                }

                Interlocked.Increment(ref patches);
                entered.TrySetResult();
                await release.Task.WaitAsync(http.RequestAborted);
                return Results.Json(new { error = new { code = "invalid_request", message = "A database cannot be renamed." } }, statusCode: 400);
            });
            using var client = scenario.CreateClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "rename-source", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = body.RootElement.GetProperty("id").GetString()!;
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height } });
            var page = await context.NewPageAsync();
            await BrowserAudit.SignInAsync(page, scenario.Origin, "/databases/" + id + "/settings", account.Email, account.Password);
            await page.GetByLabel("New database name", new() { Exact = true }).FillAsync("rename-draft");
            await page.GetByLabel("Type rename-source to confirm", new() { Exact = true }).FillAsync("wrong");
            var invalid = await page.RunAndWaitForResponseAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Rename database", Exact = true }).ClickAsync(),
                response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/v1/dashboard/databases/rename");
            await Assert.That(invalid.Status).IsEqualTo(422);
            await page.Locator("#rename-error").WaitForAsync();
            await Assert.That(await page.Locator("#rename-error").InnerTextAsync()).Contains("Type the current database name");
            await Assert.That(patches).IsEqualTo(0);
            await page.GetByLabel("Type rename-source to confirm", new() { Exact = true }).FillAsync("rename-source");
            var pending = new TaskCompletionSource<(string Busy, bool Disabled, bool ReadOnly, string Status)>(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.ExposeBindingAsync("recordRenamePending", (BindingSource _, string busy, bool disabled, bool readOnly, string status) =>
                pending.TrySetResult((busy, disabled, readOnly, status)));
            // Observe feedback before native navigation pauses the browser execution context.
            await page.Locator("form[aria-label='Rename database']").EvaluateAsync("""
            form => window.addEventListener('submit', event => {
                if (event.target !== form) return;
                const button = event.submitter;
                void recordRenamePending(form.getAttribute('aria-busy'), button.disabled,
                    form.querySelector('[name=name]').readOnly, form.querySelector('[role=status]').textContent);
                button.click();
            })
            """);
            var refused = page.RunAndWaitForResponseAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Rename database", Exact = true }).ClickAsync(),
                response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/v1/dashboard/databases/rename");
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var state = await pending.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Assert.That(state.Busy).IsEqualTo("true");
                await Assert.That(state.Disabled).IsTrue();
                await Assert.That(state.ReadOnly).IsTrue();
                await Assert.That(state.Status).Contains("Renaming database. Please wait for the result.");
            }
            finally { release.TrySetResult(); }
            await Assert.That((await refused).Status).IsEqualTo(400);
            await page.Locator("#rename-error").WaitForAsync();
            await Assert.That(await page.Locator("#rename-error").InnerTextAsync()).Contains("A database cannot be renamed.");
            await Assert.That(await page.GetByLabel("New database name", new() { Exact = true }).InputValueAsync()).IsEqualTo("rename-draft");
            await Assert.That(await page.GetByRole(AriaRole.Button, new() { Name = "Rename database", Exact = true }).IsEnabledAsync()).IsTrue();
            await Assert.That(await page.Locator("main").InnerTextAsync()).Contains(id).And.Contains("rename-source");
            await Assert.That(patches).IsEqualTo(1);
        }
    }
}
