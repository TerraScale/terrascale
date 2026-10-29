using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DocumentBrowserRecoveryJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("collections")]
        [Arguments("query")]
        [Arguments("continuation")]
        public async Task FailedReadsOfferGetRetryWithoutClaimingEmptyAndRecoverFromFirstPage(string boundary)
        {
            var failing = 0;
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            {
                var producerPath = http.Request.Path.Value!;
                var selected = boundary != "collections" ? producerPath.EndsWith("/query", StringComparison.Ordinal)
                    : http.Request.Method == "GET" && producerPath.EndsWith("/collections", StringComparison.Ordinal);
                if (Volatile.Read(ref failing) == 1 && selected)
                {
                    return Task.FromResult<IResult?>(Results.StatusCode(503));
                }

                return Task.FromResult<IResult?>(boundary != "continuation" || !selected ? null : Results.Json(new
                {
                    items = Volatile.Read(ref failing) == 0 ? Enumerable.Range(0, 25).Select(index => new
                    { id = $"01K78D0C00000000000000{index:D4}", version = "v1", state = "value", document = new { name = "needle" } }).ToArray() : [],
                    next_cursor = Volatile.Read(ref failing) == 0 ? "opaque/+cursor" : null
                }));
            });
            using var client = host.CreateClient();
            var id = await SetupAsync(client);
            var path = $"/databases/{id}/documents/items?view=list&sort=name&order=desc" + (boundary == "continuation" ? "&q=needle" : "");
            Dictionary<string, string>? fields = null;
            if (boundary == "continuation")
            {
                var first = WebUtility.HtmlDecode(await client.GetStringAsync(path));
                var next = Regex.Match(first, "<form id=\"document-browse-next\"[^>]*action=\"([^\"]+)\"[^>]*>(.*?)</form>", RegexOptions.Singleline);
                await Assert.That(next.Success).IsTrue();
                path = next.Groups[1].Value;
                fields = Regex.Matches(next.Groups[2].Value, "type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"").ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
            }
            Interlocked.Exchange(ref failing, 1);
            using var failure = fields is null ? await client.GetAsync(path) : await HttpJourney.PostFormAsync(client, path, fields);
            var html = WebUtility.HtmlDecode(await failure.Content.ReadAsStringAsync());
            await Assert.That(html).Contains("Documents could not be loaded.").And.Contains("Document results unavailable.")
                .And.DoesNotContain("No collections yet.").And.DoesNotContain("This collection has no documents yet.")
                .And.DoesNotContain("End of collection reached.").And.DoesNotContain("Add document</span>");
            var retry = Regex.Match(html, "href=\"([^\"]+)\">Retry documents</a>");
            await Assert.That(retry.Success).IsTrue();
            await Assert.That(retry.Groups[1].Value).Contains("view=list").And.Contains("sort=name").And.Contains("order=desc")
                .And.DoesNotContain("page=").And.DoesNotContain("cursor=");
            if (boundary == "continuation")
            {
                await Assert.That(retry.Groups[1].Value).Contains("q=needle");
            }

            Interlocked.Exchange(ref failing, 2);
            using var recovered = await client.GetAsync(retry.Groups[1].Value);
            await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await recovered.Content.ReadAsStringAsync()).Contains(boundary == "continuation" ? "No documents match your search." : "This collection has no documents yet.")
                .And.Contains("End of collection reached.").And.DoesNotContain("Retry documents</a>");
        }

        [Test]
        public async Task RenderedGridOnlyOffersAddressableSortsAndNamesItsScrollRegion()
        {
            var longName = new string('z', 129);
            var atLimit = new string('a', 128);
            var payload = new Dictionary<string, object> { ["name"] = "Ada", ["a.b"] = 2, [longName] = 3, ["a/b~c"] = 4, [atLimit] = 5, [""] = 6 };
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
                Task.FromResult<IResult?>(!http.Request.Path.Value!.EndsWith("/query", StringComparison.Ordinal) ? null
                    : Results.Json(new
                    {
                        items = new object[] {
                        new { id = "01K78D0C000000000000000001", version = "v1", state = "value", document = (object)payload },
                        new { id = "01K78D0C000000000000000002", version = "v1", state = "value", document = (object)42 }
                    },
                        next_cursor = (string?)null
                    })));
            using var client = host.CreateClient();
            var id = await SetupAsync(client);
            var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/databases/{id}/documents/items?sort=name&order=desc&view=grid"));
            await Assert.That(html).Contains("role=\"region\" tabindex=\"0\" aria-label=\"Documents table\"")
                .And.Contains("This table shows the last page load.").And.Contains("Refresh documents</a>")
                .And.DoesNotContain("<span>History</span>").And.Contains("aria-describedby=\"new-collection-name-hint\"");
            var sorts = Regex.Matches(html, "href=\"([^\"]+)\"").Select(match => match.Groups[1].Value)
                .Where(path => path.Contains("sort=", StringComparison.Ordinal)).ToArray();
            await Assert.That(sorts.Any(path => path.Contains("sort=a%2Fb~c", StringComparison.Ordinal))).IsTrue();
            await Assert.That(sorts.Any(path => path.Contains("sort=" + atLimit, StringComparison.Ordinal))).IsTrue();
            await Assert.That(sorts.Any(path => path.Contains("sort=a.b", StringComparison.Ordinal)
                || path.Contains("sort=" + longName, StringComparison.Ordinal) || path.Contains("sort=value", StringComparison.Ordinal))).IsFalse();
        }

        [Test]
        [Arguments("replace")]
        [Arguments("patch")]
        public async Task RejectedJsonRetainsDraftAndOffersGuardedCloseRequestsWithAssociatedHint(string operation)
        {
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            var id = await SetupAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, $"/databases/{id}/documents/items");
            const string documentId = "01K78D0C000000000000000001";
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
            {
                ["_csrf"] = csrf,
                ["databaseId"] = id,
                ["collection"] = "items",
                ["operation"] = "create",
                ["id"] = documentId,
                ["document"] = "{\"name\":\"Ada\"}"
            });
            created.EnsureSuccessStatusCode();
            var untouched = WebUtility.HtmlDecode(await client.GetStringAsync($"/databases/{id}/documents/items?edit={documentId}"));
            var initial = Regex.Match(untouched, "<dialog id=\"edit-document\"[\\s\\S]*?</dialog>").Value;
            await Assert.That(initial).Contains("data-rejected-draft=\"false\"").And.Contains("value=\"replace\" selected");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            const string draft = "{ invalid < JSON";
            using var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
            {
                ["_csrf"] = csrf,
                ["databaseId"] = id,
                ["collection"] = "items",
                ["operation"] = operation,
                ["id"] = documentId,
                ["document"] = draft
            });
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var html = WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync());
            var editor = Regex.Match(html, "<dialog id=\"edit-document\"[\\s\\S]*?</dialog>").Value;
            await Assert.That(editor).Contains(draft).And.Contains("data-on:cancel=")
                .And.Contains("data-rejected-draft=\"true\"").And.Contains("field.value !== field.defaultValue")
                .And.Contains("option.selected !== option.defaultSelected").And.Contains($"value=\"{operation}\" selected")
                .And.Contains("Close without saving?").And.Contains("evt.preventDefault()")
                .And.Contains("patch applies JSON Merge Patch; null removes fields.")
                .And.Contains("aria-describedby=\"document-operation-hint\"").And.Contains("id=\"document-operation-hint\"");
            await Assert.That(Regex.Matches(editor, "command=\"request-close\"").Count).IsEqualTo(2);
            await Assert.That(editor).DoesNotContain("command=\"close\"");
        }

        [Test]
        [Category("Slow")]
        [Arguments("create", false, false)]
        [Arguments("replace", false, false)]
        [Arguments("create", true, false)]
        [Arguments("replace", true, false)]
        [Arguments("create", false, true)]
        [Arguments("replace", false, true)]
        [Arguments("create", true, true)]
        [Arguments("replace", true, true)]
        public async Task EditorWidgetsConsumeEscapeBeforeDirtyDialogAndFallbackKeepsGuard(string operation, bool rejectedDraft, bool fallback)
        {
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            var id = await SetupAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, $"/databases/{id}/documents/items");
            const string documentId = "01K78D0C000000000000000001";
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["operation"] = "create", ["id"] = documentId, ["document"] = "{\"name\":\"Ada\"}" });
            created.EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using var response = rejectedDraft
                ? await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
                { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["operation"] = operation, ["id"] = documentId, ["document"] = "{ rejected draft" })
                : await client.GetAsync($"/databases/{id}/documents/items" + (operation == "replace" ? $"?edit={documentId}" : ""));
            await Assert.That(response.StatusCode).IsEqualTo(rejectedDraft ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
            var page = await context.NewPageAsync();
            if (fallback)
            {
                await page.RouteAsync("**/js/**", route => new Uri(route.Request.Url).AbsolutePath.Contains("code-editor", StringComparison.Ordinal)
                ? route.AbortAsync() : route.ContinueAsync());
            }

            var url = new Uri(host.Origin, "/editor-escape-regression").ToString();
            var html = await response.Content.ReadAsStringAsync();
            await page.RouteAsync(url, route => route.FulfillAsync(new() { ContentType = "text/html", Body = html }));
            await page.AddInitScriptAsync("""
            window.discards = 0;
            window.confirm = () => { window.discards++; document.documentElement.dataset.hadActivation = String(navigator.userActivation.hasBeenActive); return false; };
            document.addEventListener('cancel', event => { document.documentElement.dataset.cancelable = String(event.cancelable); }, true);
            """);
            await page.GotoAsync(url);
            var dialogId = operation == "create" ? "add-document" : "edit-document";
            var dialog = page.Locator("#" + dialogId);
            if (rejectedDraft)
            {
                // Playwright locator/evaluate helpers grant activation; only CDP may wait before the first Escape.
                var cdp = await context.NewCDPSessionAsync(page);
                var ready = await cdp.SendAsync("Runtime.evaluate", new Dictionary<string, object>
                {
                    ["expression"] = $"new Promise(resolve => {{ const deadline = performance.now() + 10000; const ready = () => document.getElementById('{dialogId}')?.open ? resolve(true) : performance.now() > deadline ? resolve(false) : requestAnimationFrame(ready); ready(); }})",
                    ["awaitPromise"] = true,
                    ["returnByValue"] = true,
                    ["userGesture"] = false
                });
                await Assert.That(ready!.Value.GetProperty("result").GetProperty("value").GetBoolean()).IsTrue();
                await page.Keyboard.PressAsync("Escape");
                await Assert.That(await page.EvaluateAsync<int>("window.discards")).IsEqualTo(1);
                await Microsoft.Playwright.Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-had-activation", "false");
                await Microsoft.Playwright.Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-cancelable", "true");
                await Microsoft.Playwright.Assertions.Expect(dialog).ToBeVisibleAsync();
                await page.EvaluateAsync("window.discards = 0");
            }
            await dialog.EvaluateAsync("el => { if (!el.open) el.showModal(); }");
            var field = dialog.Locator("textarea[name=document]");
            var original = await field.InputValueAsync();
            if (rejectedDraft)
            {
                await Assert.That(original).IsEqualTo("{ rejected draft");
            }
            else if (operation == "replace")
            {
                // Encoded CRLF survives in defaultValue; the textarea value API normalizes it to LF.
                await Assert.That(await field.EvaluateAsync<bool>("el => el.defaultValue.includes('\\r') && !el.value.includes('\\r')")).IsTrue();
            }

            if (!fallback)
            {
                var editor = dialog.Locator("ts-code-editor[data-ready]");
                // Monaco starts after page load; match the 15s editor-ready budget of the other browser journeys.
                await Microsoft.Playwright.Assertions.Expect(editor).ToBeVisibleAsync(new() { Timeout = 15000 });
                foreach (var widget in new[] { "find", "suggest" })
                {
                    await editor.EvaluateAsync("""
                    async (el, widget) => {
                        el.editor.focus();
                        el.editor.setPosition({ lineNumber: 1, column: 2 });
                        if (widget === 'find') await el.editor.getAction('actions.find').run();
                        else el.editor.trigger('regression', 'editor.action.triggerSuggest', {});
                    }
                    """, widget);
                    var popup = page.Locator(widget == "find" ? ".find-widget[aria-hidden=false]" : ".suggest-widget.visible");
                    await Microsoft.Playwright.Assertions.Expect(popup).ToBeVisibleAsync();
                    await page.Keyboard.PressAsync("Escape");
                    await Microsoft.Playwright.Assertions.Expect(popup).ToBeHiddenAsync();
                    await Microsoft.Playwright.Assertions.Expect(dialog).ToBeVisibleAsync();
                    await Assert.That(await page.EvaluateAsync<int>("window.discards")).IsEqualTo(0);
                    await Assert.That(await field.InputValueAsync()).IsEqualTo(original);
                }
                await editor.EvaluateAsync("el => el.editor.focus()");
            }
            else
            {
                await Microsoft.Playwright.Assertions.Expect(field).ToBeVisibleAsync();
                await Assert.That(await dialog.Locator("ts-code-editor[data-ready]").CountAsync()).IsEqualTo(0);
                await field.FocusAsync();
            }
            await page.Keyboard.PressAsync("Escape");
            await Assert.That(await page.EvaluateAsync<int>("window.discards")).IsEqualTo(rejectedDraft ? 1 : 0);
            if (rejectedDraft)
            {
                await Microsoft.Playwright.Assertions.Expect(dialog).ToBeVisibleAsync();
                await Assert.That(await field.InputValueAsync()).IsEqualTo(original);
                foreach (var name in new[] { "Close", "Cancel" })
                {
                    await dialog.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).ClickAsync();
                }

                await page.Mouse.ClickAsync(1, 1);
                await Assert.That(await page.EvaluateAsync<int>("window.discards")).IsEqualTo(4);
                await Microsoft.Playwright.Assertions.Expect(dialog).ToBeVisibleAsync();
                await page.EvaluateAsync("window.confirm = () => true");
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Microsoft.Playwright.Assertions.Expect(dialog).ToBeHiddenAsync();
            }
            else
            {
                await Microsoft.Playwright.Assertions.Expect(dialog).ToBeHiddenAsync();
                await dialog.EvaluateAsync("el => el.showModal()");
                await field.EvaluateAsync("el => { el.value = 'changed'; el.dispatchEvent(new Event('input', { bubbles: true })); }");
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Microsoft.Playwright.Assertions.Expect(dialog).ToBeVisibleAsync();
                await Assert.That(await page.EvaluateAsync<int>("window.discards")).IsEqualTo(1);
                await field.EvaluateAsync("el => { el.value = el.defaultValue; el.dispatchEvent(new Event('input', { bubbles: true })); }");
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Microsoft.Playwright.Assertions.Expect(dialog).ToBeHiddenAsync();
                await Assert.That(await page.EvaluateAsync<int>("window.discards")).IsEqualTo(1);
            }
        }

        private static async Task<string> SetupAsync(HttpClient client)
        {
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "browser-recovery", ["region"] = "GRU" });
            created.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = body.RootElement.GetProperty("id").GetString()!;
            using var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "items" });
            collection.EnsureSuccessStatusCode();
            return id;
        }
    }
}
