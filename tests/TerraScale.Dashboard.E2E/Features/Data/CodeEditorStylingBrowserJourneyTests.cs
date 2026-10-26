using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class CodeEditorStylingBrowserJourneyTests(DashboardRootFixture fixture)
{
    // The editor theme resolves each --ts-* token through a probe's computed colour; mirror that here.
    private const string ResolveToken = """
        name => {
            const probe = document.createElement('span');
            probe.hidden = true;
            probe.style.color = `var(${name})`;
            document.body.append(probe);
            const color = getComputedStyle(probe).color;
            probe.remove();
            return color;
        }
        """;

    [Test]
    public async Task EditorsInDocumentDialogsAndSqlUseTheDarkTerraScaleTheme()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var output = BrowserAudit.Output(fixture.Root, "code-editor-styling");
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1440, Height = 900 }, RecordVideoDir = output });
        var page = await context.NewPageAsync();
        var failures = new ConcurrentQueue<string>();
        page.PageError += (_, error) => failures.Enqueue(error);
        page.Console += (_, message) =>
        {
            if (message.Text.StartsWith("Code editor unavailable:", StringComparison.Ordinal) ||
                message.Text.StartsWith("SQL completion unavailable:", StringComparison.Ordinal) ||
                message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)) failures.Enqueue(message.Text);
        };
        await BrowserAudit.SignInAsync(page, scenario.Origin, email: scenario.Personas.Seeded.Email, password: scenario.Personas.Seeded.Password);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
        const string database = "/databases/0Z793APJ2ECFW2F7P6J9F2RKVY";

        await NavigateAsync(database + "/documents/orders");
        // Every token the theme reads must exist; a missing one silently inherits the body ink colour.
        foreach (var token in new[] { "--ts-surface", "--ts-ink-3", "--ts-link", "--ts-syn-keyword", "--ts-syn-string", "--ts-syn-number", "--ts-syn-comment", "--ts-syn-fn", "--ts-syn-punct", "--ts-shadow" })
        {
            await Assert.That(await page.EvaluateAsync<string>("name => getComputedStyle(document.documentElement).getPropertyValue(name).trim()", token))
                .IsNotEmpty().Because(token + " must be defined in tokens.css for the editor theme.");
        }
        var surfaceToken = await ResolveAsync("--ts-surface");
        await Assert.That(DarkValue(surfaceToken)).IsTrue().Because("The editor surface must be the dark --ts-surface token, not " + surfaceToken + ".");

        var addDialog = page.Locator("dialog#add-document");
        await Assert.That(await addDialog.EvaluateAsync<bool>("dialog => dialog.open")).IsFalse();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add document", Exact = true }).ClickAsync();
        await Assert.That(await addDialog.EvaluateAsync<bool>("dialog => dialog.open")).IsTrue();
        await AssertEditorStyledAsync("dialog#add-document ts-code-editor", "add-document");
        await addDialog.Locator("#new-document-json").EvaluateAsync("(el, text) => { el.value = text; el.dispatchEvent(new Event('input', {bubbles:true})); }", "{\"customer\":\"cus_1003\",\"total\":42}");
        await Assert.That(await page.Locator("dialog#add-document ts-code-editor").EvaluateAsync<string>("host => host.editor.getValue()")).Contains("cus_1003");
        await AssertJsonSyntaxAsync("dialog#add-document ts-code-editor");
        await page.Locator("dialog#add-document ts-code-editor").EvaluateAsync("host => { host.editor.focus(); host.editor.setSelection({ startLineNumber: 1, startColumn: 2, endLineNumber: 1, endColumn: 10 }); }");
        await AssertCursorAndSelectionAsync("dialog#add-document ts-code-editor", "add-document");
        await addDialog.ScreenshotAsync(new() { Path = Path.Combine(output, "add-document.png") });

        var documentId = (await page.Locator("#documents .ts-data-doc-id").First.TextContentAsync())!.Trim();
        await NavigateAsync(database + "/documents/orders?edit=" + Uri.EscapeDataString(documentId));
        var editDialog = page.Locator("dialog#edit-document");
        await Assert.That(await editDialog.EvaluateAsync<bool>("dialog => dialog.open")).IsTrue();
        await AssertEditorStyledAsync("dialog#edit-document ts-code-editor", "edit-document");
        await AssertJsonSyntaxAsync("dialog#edit-document ts-code-editor");
        await editDialog.ScreenshotAsync(new() { Path = Path.Combine(output, "edit-document.png") });

        await NavigateAsync(database + "/sql");
        await page.Locator("#sql-editor ts-code-editor[data-ready]").WaitForAsync(new() { Timeout = 15000 });
        await page.Locator("#sql").EvaluateAsync("(el, text) => { el.value = text; el.dispatchEvent(new Event('input', {bubbles:true})); }", "SELECT 'x', 42 -- c");
        await page.WaitForFunctionAsync("() => [...document.querySelectorAll('#sql-editor .view-line span')].some(span => span.textContent.replace(/\\u00a0/g, ' ').trim() === 'SELECT')");
        await AssertEditorStyledAsync("#sql-editor ts-code-editor", "sql");

        // Each syntax token takes its --ts-syn-* colour, and the four roles stay visually distinct.
        var colors = await page.Locator("#sql-editor ts-code-editor").EvaluateAsync<Dictionary<string, string?>>("""
            host => {
                const leaves = [...host.querySelectorAll('.view-line span')].filter(span => span.childElementCount === 0);
                const colorOf = match => { const span = leaves.find(span => match(span.textContent.replace(/ /g, ' ').trim())); return span ? getComputedStyle(span).color : null; };
                return {
                    keyword: colorOf(text => text === 'SELECT'),
                    string: colorOf(text => text.startsWith("'")),
                    number: colorOf(text => text === '42'),
                    comment: colorOf(text => text.startsWith('--')),
                };
            }
            """);
        await Assert.That(colors["keyword"]).IsEqualTo(await ResolveAsync("--ts-syn-keyword"));
        await Assert.That(colors["string"]).IsEqualTo(await ResolveAsync("--ts-syn-string"));
        await Assert.That(colors["number"]).IsEqualTo(await ResolveAsync("--ts-syn-number"));
        await Assert.That(colors["comment"]).IsEqualTo(await ResolveAsync("--ts-syn-comment"));
        await Assert.That(colors.Values.Distinct().Count()).IsEqualTo(4).Because("Keyword, string, number and comment colours must differ: " + string.Join(", ", colors.Select(pair => pair.Key + "=" + pair.Value)));

        await page.GetByRole(AriaRole.Button, new() { Name = "Save query", Exact = true }).ClickAsync();
        await Assert.That(await page.Locator("dialog#save-query").EvaluateAsync<bool>("dialog => dialog.open")).IsTrue();
        await AssertEditorStyledAsync("dialog#save-query ts-code-editor", "save-query");
        await page.Locator("dialog#save-query").ScreenshotAsync(new() { Path = Path.Combine(output, "save-query.png") });
        await Assert.That(failures.ToArray()).IsEmpty();

        async Task NavigateAsync(string path)
        {
            var response = await page.GotoAsync(new Uri(scenario.Origin, path).ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(response!.Status).IsEqualTo(200);
        }
        Task<string> ResolveAsync(string token) => page.EvaluateAsync<string>(ResolveToken, token);
        async Task AssertJsonSyntaxAsync(string host)
        {
            var expected = new[] { await ResolveAsync("--ts-syn-keyword"), await ResolveAsync("--ts-syn-string"), await ResolveAsync("--ts-syn-number") };
            await page.WaitForFunctionAsync("""
                ({host, expected}) => {
                    const colors = [...document.querySelectorAll(host + ' .view-line span')].map(span => getComputedStyle(span).color);
                    return expected.every(color => colors.includes(color));
                }
                """, new { host, expected });
            await Assert.That(expected.Distinct().Count()).IsEqualTo(3);
        }
        async Task AssertEditorStyledAsync(string host, string name)
        {
            await page.Locator(host + "[data-ready]").WaitForAsync(new() { Timeout = 15000 });
            // A data-ready host proves nothing about styling: Monaco's stylesheet must be injected and applied.
            await Assert.That(await page.EvaluateAsync<bool>("() => document.getElementById('monaco-editor-core-css')?.sheet?.cssRules.length > 1000"))
                .IsTrue().Because(name + ": Monaco's core stylesheet must be admitted by CSP.");
            await Assert.That(await page.Locator(host + " .monaco-editor .overflow-guard").EvaluateAsync<string>("el => getComputedStyle(el).position"))
                .IsEqualTo("relative").Because(name + ": Monaco's core CSS must apply to the editor.");
            var surface = await page.Locator(host + " .ts-code-editor-surface").BoundingBoxAsync();
            await Assert.That(surface is { Width: > 0, Height: > 0 }).IsTrue().Because(name + ": the editor must have a size once its dialog is shown.");
            var editor = await page.Locator(host + " .monaco-editor").BoundingBoxAsync();
            await Assert.That(editor is not null && Math.Abs(editor.Width - surface!.Width) < 2 && Math.Abs(editor.Height - surface.Height) < 2)
                .IsTrue().Because(name + ": Monaco must fill its surface after a hidden dialog opens.");
            await Assert.That(await page.Locator(host).EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor")).IsEqualTo(surfaceToken)
                .Because(name + ": the host surface must use --ts-surface.");
            await Assert.That(await page.Locator(host + " .monaco-editor-background").First.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor")).IsEqualTo(surfaceToken)
                .Because(name + ": Monaco must paint the TerraScale theme background.");
            var lineNumber = page.Locator(host + " .line-numbers").First;
            var active = await lineNumber.EvaluateAsync<bool>("el => el.classList.contains('active-line-number')");
            await Assert.That(await lineNumber.EvaluateAsync<string>("el => getComputedStyle(el).color")).IsEqualTo(await ResolveAsync(active ? "--ts-ink-2" : "--ts-ink-3"))
                .Because(name + ": line numbers must use the TerraScale ink tokens.");
        }
        async Task AssertCursorAndSelectionAsync(string host, string name)
        {
            await Assert.That(await page.Locator(host + " .monaco-editor .cursor").First.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor"))
                .IsEqualTo(await ResolveAsync("--ts-link")).Because(name + ": the cursor must use the link token.");
            var selection = page.Locator(host + " .monaco-editor .selected-text").First;
            await selection.WaitForAsync(new() { Timeout = 5000 });
            var accent = (await ResolveAsync("--ts-accent"));
            await Assert.That(await selection.EvaluateAsync<bool>("(el, accent) => { const [r, g, b] = accent.match(/\\d+/g); return getComputedStyle(el).backgroundColor.startsWith(`rgba(${r}, ${g}, ${b},`); }", accent))
                .IsTrue().Because(name + ": the selection must be the accent colour at reduced alpha.");
        }
    }

    // Light-dark tokens resolve to rgb(); a dark surface has low channel values.
    private static bool DarkValue(string color) => Regex.Matches(color, @"\d+").Take(3).Select(match => int.Parse(match.Value)).Sum() < 3 * 64;
}
