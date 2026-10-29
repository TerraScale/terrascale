using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.AuthenticationManagement;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class DirectoryExportBrowserJourneyTests(DashboardRootFixture fixture)
{
    private const string Cursor = "opaque+/=%cursor";

    [Test]
    public async Task BrowserDownloadsOneBoundedPageAtATimeAndStartOverRestoresTheFirstPage()
    {
        // The producer only records what it saw and returns answers; assertions run on the test thread.
        var observed = new ConcurrentQueue<(string After, string Limit)>();
        var refuse = false;
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root, liveProjects: true, projectExtension: http =>
        {
            if (!http.Request.Path.Value!.EndsWith("/users/export", StringComparison.Ordinal)) return Task.FromResult<IResult?>(null);
            var after = http.Request.Query["after"].ToString();
            observed.Enqueue((after, http.Request.Query["limit"].ToString()));
            if (refuse) return Task.FromResult<IResult?>(Results.Json(new { error = "forbidden" }, statusCode: 403));
            var initial = after.Length == 0;
            return Task.FromResult<IResult?>(Results.Json(new
            {
                items = initial ? new[] { new { id = "01JA0000000000000000000001", email = "synthetic@example.com", email_verified = true,
                    created_at = "2026-10-01T00:00:00Z", disabled = false, linked_providers = Array.Empty<object>() } } : [],
                next = initial ? Cursor : null,
                created_before = "2026-10-25T00:00:00Z"
            }));
        });
        using var client = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(client, returnTo: "/auth/users");
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        // Carry the signed-in dashboard session into the browser without going through the hosted sign-in page.
        var session = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0].Split('=', 2);
        var cookies = new[] { new Microsoft.Playwright.Cookie { Name = session[0], Value = session[1], Url = scenario.Origin.ToString(), HttpOnly = true, Secure = scenario.Origin.Scheme == "https", SameSite = SameSiteAttribute.Lax } };

        using var playwright = await Playwright.CreateAsync();
        await using var chromium = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var output = BrowserAudit.Output(fixture.Root, "directory-export-browser");
        await using var context = await chromium.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1440, Height = 1000 }, ColorScheme = ColorScheme.Light, RecordVideoDir = output, AcceptDownloads = true });
        await context.AddCookiesAsync(cookies);
        var page = await context.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        var downloads = new ConcurrentQueue<IDownload>();
        page.PageError += (_, error) => errors.Enqueue(error);
        page.Download += (_, download) => downloads.Enqueue(download);
        await page.GotoAsync(new Uri(scenario.Origin, "/auth/users").ToString());
        await page.Locator("#auth-directory-export summary").ClickAsync();
        var form = page.Locator("#auth-export-form");
        var submit = form.Locator("[type=submit]");
        var reset = form.Locator("[type=reset]");
        var cursor = page.Locator("#auth-export-after");
        var status = page.Locator("#auth-export-status");
        var projectId = await page.Locator("#auth-export-form input[name=project_id]").InputValueAsync();
        await Shot("01-ready");
        await Assert.That(await submit.InnerTextAsync()).IsEqualTo("Download page");
        await Assert.That(await cursor.InputValueAsync()).IsEqualTo("");

        // One click, one SDK page, one download; the cursor it returns is shown for resuming.
        var first = await page.RunAndWaitForDownloadAsync(() => submit.ClickAsync());
        await Assert.That(first.SuggestedFilename).IsEqualTo($"users-{projectId}.json");
        await Assert.That(await DownloadedNext(first)).IsEqualTo(Cursor);
        await page.WaitForFunctionAsync("() => document.activeElement === document.querySelector('#auth-export-form [type=submit]')");
        await Assert.That(await cursor.InputValueAsync()).IsEqualTo(Cursor);
        await Assert.That(await submit.InnerTextAsync()).IsEqualTo("Download next page");
        await Shot("02-first-page");

        // Authorization is checked again on resume; a refused page keeps the cursor and downloads nothing.
        refuse = true;
        await submit.ClickAsync();
        await page.WaitForFunctionAsync("() => document.getElementById('auth-export-status').textContent.startsWith('Access was refused')");
        await Assert.That(await cursor.InputValueAsync()).IsEqualTo(Cursor);
        await Assert.That(await submit.IsEnabledAsync()).IsTrue();
        await Assert.That(downloads.Count).IsEqualTo(1);
        await page.WaitForFunctionAsync("() => document.activeElement === document.querySelector('#auth-export-form [type=submit]')");
        await Shot("03-access-refused");

        // Retrying with the preserved cursor reaches the final, empty page.
        refuse = false;
        var last = await page.RunAndWaitForDownloadAsync(() => submit.ClickAsync());
        await Assert.That(await DownloadedNext(last)).IsNull();
        await page.WaitForFunctionAsync("() => document.activeElement === document.querySelector('#auth-export-form [type=reset]')");
        await Assert.That(await submit.IsDisabledAsync()).IsTrue();
        await Assert.That(await submit.InnerTextAsync()).IsEqualTo("Export complete");
        await Assert.That(await cursor.InputValueAsync()).IsEqualTo("");
        await Assert.That(await status.InnerTextAsync()).Contains("Final page downloaded");
        await Shot("04-final-page");

        // Start over clears the export state; the next click begins again from the first page.
        await reset.ClickAsync();
        await page.WaitForFunctionAsync("() => document.getElementById('auth-export-status').textContent === 'Ready to download one page.'");
        await Assert.That(await submit.IsEnabledAsync()).IsTrue();
        await Assert.That(await submit.InnerTextAsync()).IsEqualTo("Download page");
        await Assert.That(await cursor.InputValueAsync()).IsEqualTo("");
        await Assert.That(await page.Locator("#auth-export-cutoff").InnerTextAsync()).IsEqualTo("");
        await Shot("05-start-over");
        var restarted = await page.RunAndWaitForDownloadAsync(() => submit.ClickAsync());
        await Assert.That(await DownloadedNext(restarted)).IsEqualTo(Cursor);

        // Producer observations, checked after the flow: every page was bounded and resumed with the exact cursor.
        var requests = observed.ToArray();
        await Assert.That(string.Join('|', requests.Select(request => request.After))).IsEqualTo($"|{Cursor}|{Cursor}|");
        await Assert.That(requests.All(request => request.Limit == "100")).IsTrue();
        await Assert.That(downloads.Count).IsEqualTo(3);
        await Assert.That(errors.ToArray()).IsEmpty();

        async Task Shot(string name)
        {
            await page.EvaluateAsync("() => document.fonts.ready");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, name + ".png"), FullPage = true, Caret = ScreenshotCaret.Hide });
        }

        static async Task<string?> DownloadedNext(IDownload download)
        {
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync((await download.PathAsync())!));
            return saved.RootElement.GetProperty("next").GetString();
        }
    }
}
