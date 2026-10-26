using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using TerraScale.DotNet.TerraBase;
using static Microsoft.Playwright.Assertions;

namespace TerraScale.Dashboard.E2E.Features.FileStorage;

[ClassDataSource<DashboardRootFixture>]
public sealed class FileConfirmationBrowserJourneyTests(DashboardRootFixture fixture)
{
    [Test, Category("Slow")]
    public async Task NamedDialogsCancelSafelyAndTypedConfirmationSubmitsOnlyOnce()
    {
        const string bucketId = "01JA00000000000000000000B1", fileId = "01JA00000000000000000000F1";
        var bucketExists = true;
        var fileExists = true;
        var deletes = 0;
        var rules = new TerraBaseFileRules("/userId", new(true, false, []), new(true, false, []));
        var bucket = new TerraBaseFileBucket(bucketId, "avatars", "gru", rules);
        using var metadata = JsonDocument.Parse("{}");
        var file = new TerraBaseStoredFile(fileId, "avatar.png", "image/png", 5, Convert.ToBase64String(new byte[32]), metadata.RootElement, "ready", DateTimeOffset.UtcNow.AddMinutes(5));
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
        {
            var path = http.Request.Path.Value!;
            if (!path.Contains("/buckets", StringComparison.Ordinal)) return Task.FromResult<IResult?>(null);
            IResult result = http.Request.Method switch
            {
                "GET" when path.EndsWith("/buckets", StringComparison.Ordinal) => Results.Json(new { items = bucketExists ? new[] { bucket } : [], next = (string?)null }),
                "GET" => Results.Json(new { items = fileExists ? new[] { file } : [], next = (string?)null }),
                "DELETE" => Deleted(path),
                _ => Results.NotFound()
            };
            return Task.FromResult<IResult?>(result);
        });
        IResult Deleted(string path)
        {
            Interlocked.Increment(ref deletes);
            if (path.EndsWith("/buckets/" + bucketId, StringComparison.Ordinal)) bucketExists = false;
            else fileExists = false;
            return Results.NoContent();
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var login = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });
        await BrowserAudit.SignInAsync(login, scenario.Origin, "/storage");
        var output = Path.Combine(fixture.Root, "test-results/dotnet/forms-confirmation");
        Directory.CreateDirectory(output);
        await using var recording = await browser.NewContextAsync(new()
        { IgnoreHTTPSErrors = true, StorageState = await login.Context.StorageStateAsync(), RecordVideoDir = output, ViewportSize = new() { Width = 1440, Height = 1000 } });
        await recording.RouteAsync("**/css/terrascale.css*", async route =>
        {
            var response = await route.FetchAsync();
            await route.FulfillAsync(new() { Response = response, Body = await response.TextAsync() + "\n.ts-account-trigger,.ts-account-card{visibility:hidden!important}" });
        });
        await login.Context.CloseAsync();
        var page = await recording.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(new Uri(scenario.Origin, "/storage").ToString());
        await Shot("files-before-confirmation");
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete file", Exact = true }).First.ClickAsync();
        var dialog = page.Locator("#delete-file-" + fileId);
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog).ToContainTextAsync("avatar.png");
        await Shot("file-named-confirmation");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assert.That(deletes).IsEqualTo(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete file", Exact = true }).First.ClickAsync();
        await SubmitTwice(dialog.Locator("form"), "/api/v1/dashboard/storage/delete");
        await Expect(page.GetByText("No completed files", new() { Exact = true })).ToBeVisibleAsync();
        await Assert.That(deletes).IsEqualTo(1);
        await Shot("file-deleted-empty-state");
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete empty bucket", Exact = true }).First.ClickAsync();
        dialog = page.Locator("#delete-bucket-" + bucketId);
        await Expect(dialog).ToContainTextAsync("avatars");
        await dialog.GetByLabel("Type avatars to confirm").FillAsync("another-bucket");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Delete empty bucket", Exact = true }).ClickAsync();
        await Assert.That(await dialog.Locator("input[name=confirmName]").EvaluateAsync<bool>("el => el.validity.valid")).IsFalse();
        await Assert.That(deletes).IsEqualTo(1);
        await Shot("bucket-wrong-name-refused");
        await dialog.GetByLabel("Type avatars to confirm").FillAsync("avatars");
        await Shot("bucket-named-confirmation");
        await SubmitTwice(dialog.Locator("form"), "/api/v1/dashboard/storage/delete-bucket");
        await Expect(page.GetByText("No buckets yet", new() { Exact = true })).ToBeVisibleAsync();
        await Assert.That(deletes).IsEqualTo(2);
        await Shot("bucket-deleted-empty-state");
        await Assert.That(errors).IsEmpty();
        await page.CloseAsync();
        await recording.CloseAsync();
        await page.Video!.SaveAsAsync(Path.Combine(output, "file-and-bucket-confirmation.webm"));

        Task Shot(string name) => page.ScreenshotAsync(new() { Path = Path.Combine(output, name + ".png"), FullPage = true });
        async Task SubmitTwice(ILocator form, string action)
        {
            // Native submission reaches the real app; a same-turn second requestSubmit must be cancelled by the shared guard.
            var response = await page.RunAndWaitForResponseAsync(() => form.EvaluateAsync("form => { form.requestSubmit(); form.requestSubmit(); }"),
                response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == action);
            await Assert.That(response.Status).IsEqualTo(303);
            await page.WaitForLoadStateAsync();
        }
    }
}
