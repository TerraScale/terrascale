using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Platform;

public sealed class TableMenuJourneyTests
{
    [Test, Category("Slow")]
    [Arguments(320, "grid")]
    [Arguments(375, "grid")]
    [Arguments(768, "grid")]
    [Arguments(320, "list")]
    [Arguments(375, "list")]
    [Arguments(768, "list")]
    public async Task LongDocumentsTableKeepsItsBottomRowActionsVisible(int width, string layout)
    {
        var root = DashboardE2EFixture.FindRoot();
        var source = Path.Combine(root, "src/TerraScale.Dashboard");
        var script = Regex.Match(await File.ReadAllTextAsync(Path.Combine(source, "Shared/UI/Slices/ShellDocument.cshtml")), "<script id=\"ts-table-menu-placement\"[\\s\\S]*?</script>").Value.Replace("@Model.Nonce", "fixture", StringComparison.Ordinal);
        await Assert.That(script.Length > 0).IsTrue();
        var css = "@layer base, components, features;";
        foreach (var (file, layer) in new[] { ("tokens.css", ""), ("base.css", "base"), ("controls.css", "components"), ("shell.css", "components"), ("data.css", "components"), ("overlays.css", "components"), ("../../../Features/Data/data.css", "features") })
        {
            var content = await File.ReadAllTextAsync(Path.Combine(source, "web/assets/css", file));
            css += layer.Length == 0 ? content : "@layer " + layer + " {" + content + "}";
        }
        css += ".fixture{padding:calc(100dvh - 152px) 16px 8px}#outside{position:fixed;top:8px;left:16px}";
        var rows = string.Join("", Enumerable.Range(1, 2).Select(i => "<tr><td class='ts-data-result-id'><a href='#record'>" + new string('0', 25) + i + "</a></td>" +
            (layout == "grid" ? "<td>" + (i == 1 ? "—" : new string('n', 80)) + "</td><td>" + (i == 1 ? "—" : new string('d', 300)) + "</td>" : "<td><code class='ts-data-json'>" + (i == 1 ? "{}" : "{&quot;name&quot;:&quot;" + new string('n', 80) + "&quot;,&quot;description&quot;:&quot;" + new string('d', 300) + "&quot;}") + "</code></td>") +
            "<td class='ts-table-actions'>" + Menu + "</td></tr>"));
        var html = "<!doctype html><html lang='en' data-nonce='fixture'><head><meta name='viewport' content='width=device-width, initial-scale=1'><link rel='stylesheet' href='/fixture.css'>" + script + "<script type='module' src='/datastar.js' nonce='fixture'></script></head><body><button id='outside'>Outside</button><main class='fixture'><div class='ts-main'><div class='ts-table-wrap'><table class='ts-table " + (layout == "grid" ? "ts-data-result ts-data-grid" : "ts-data-documents") + "'><thead><tr><th>ID</th>" + (layout == "grid" ? "<th>Name</th><th>Description</th>" : "<th>Document</th>") + "<th>Actions</th></tr></thead><tbody>" + rows + "</tbody></table></div></div></main></body></html>";
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/fixture.css", () => Results.Content(css, "text/css"));
        app.MapGet("/fonts/{file}", (string file) => Results.File(Path.Combine(source, "web/static/fonts", Path.GetFileName(file)), "font/woff2"));
        app.MapGet("/datastar.js", () => Results.File(Path.Combine(source, "web/assets/js/vendor/datastar.js"), "text/javascript"));
        app.MapGet("/", (HttpContext http) => { http.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'nonce-fixture'; style-src 'self'; object-src 'none'"; return Results.Content(html, "text/html"); });
        await app.StartAsync();
        var origin = app.Urls.Single();
        using var playwright = await Playwright.CreateAsync();
        foreach (var engine in new[] { playwright.Chromium, playwright.Firefox, playwright.Webkit })
        {
            if (engine.Name != "chromium" && !File.Exists(engine.ExecutablePath)) { Console.WriteLine(engine.Name + ": runtime not installed"); continue; }
            await using var browser = await engine.LaunchAsync(new() { Headless = true });
            foreach (var fallback in new[] { false, true })
            {
                var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 600 } });
                page.SetDefaultTimeout(10000);
                var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
                await page.AddInitScriptAsync("window.cspViolations = []; addEventListener('securitypolicyviolation', e => cspViolations.push(e.effectiveDirective));");
                if (fallback) await page.AddInitScriptAsync("HTMLElement.prototype.showPopover = undefined; HTMLElement.prototype.hidePopover = undefined;");
                await page.GotoAsync(origin);
                var wrap = page.Locator(".ts-table-wrap"); var menu = page.Locator("details.ts-menu").Last;
                var summary = menu.Locator("summary"); var panel = menu.Locator(".ts-menu-panel");
                var closedWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
                await Assert.That(closedWidth <= width).IsTrue();
                if (layout == "grid") await Assert.That(await wrap.EvaluateAsync<bool>("el => el.scrollWidth > el.clientWidth")).IsTrue();
                await page.Locator("#outside").FocusAsync();
                for (var i = 0; i < 4; i++) await page.Keyboard.PressAsync("Tab");
                await Assert.That(await summary.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
                await page.Keyboard.PressAsync("Enter"); await OpenAsync();
                foreach (var action in await panel.Locator("a").AllAsync())
                {
                    await page.Keyboard.PressAsync("Tab");
                    await Assert.That(await action.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
                }
                await AssertVisibleAsync();
                await Assert.That(await panel.GetAttributeAsync("popover")).IsEqualTo(fallback ? null : "manual");
                await page.Keyboard.PressAsync("Escape"); await ClosedAsync();
                await Assert.That(await summary.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
                await summary.ClickAsync(); await OpenAsync(); await AssertVisibleAsync();
                await page.SetViewportSizeAsync(width + 17, 520);
                await page.WaitForFunctionAsync("() => { const r = document.querySelector('details[open] .ts-menu-panel').getBoundingClientRect(); return r.bottom <= innerHeight && r.left >= 0 && r.right <= innerWidth; }");
                await page.Locator("#outside").ClickAsync(); await ClosedAsync();
                await Assert.That(await page.Locator("#outside").EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
                await Assert.That(await page.EvaluateAsync<string[]>("cspViolations")).IsEmpty();
                await Assert.That(errors).IsEmpty();
                Console.WriteLine($"{engine.Name} {width}px {layout} {(fallback ? "fixed fallback" : "popover")}: contained, visible, keyboard and outside dismissal PASS");
                await page.CloseAsync();

                async Task OpenAsync() => await page.WaitForFunctionAsync("() => { const m = [...document.querySelectorAll('details.ts-menu')].at(-1); return m.open && getComputedStyle(m.querySelector('.ts-menu-panel')).position === 'fixed' && m.querySelector('.ts-menu-panel').style.left !== ''; }");
                async Task ClosedAsync() => await page.WaitForFunctionAsync("() => ![...document.querySelectorAll('details.ts-menu')].at(-1).open && !document.querySelector('[popover]')");
                async Task AssertVisibleAsync()
                {
                    await Assert.That(await page.EvaluateAsync<int>("document.documentElement.scrollWidth")).IsEqualTo(closedWidth);
                    await Assert.That(await wrap.EvaluateAsync<string>("el => getComputedStyle(el).overflowX")).IsEqualTo("auto");
                    await Assert.That(await panel.EvaluateAsync<bool>("el => { const r = el.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight; }")).IsTrue();
                    await Assert.That(await panel.EvaluateAsync<bool>("el => el.getBoundingClientRect().bottom <= el.closest('details').querySelector('summary').getBoundingClientRect().top")).IsTrue();
                    await Assert.That(await panel.Locator("a").Last.EvaluateAsync<bool>("el => { const r = el.getBoundingClientRect(); return el.contains(document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2)); }")).IsTrue();
                }
            }
        }
    }

    // Same native row-menu contract as DataView: keep the panel inside details so Datastar's
    // outside-click containment and Escape focus return still operate on the original nodes.
    private const string Menu = """
        <details class="ts-menu ts-menu-end" data-on:click__outside="el.open = false" data-on:keydown__window="evt.key === 'Escape' &amp;&amp; el.open &amp;&amp; (el.open = false, el.querySelector('summary').focus())">
          <summary class="ts-icon-btn" aria-label="Document actions">…</summary>
          <div class="ts-menu-panel"><a class="ts-menu-item" href="#edit"><span>Edit</span></a><a class="ts-menu-item" href="#history"><span>History</span></a><div class="ts-menu-separator" role="separator"></div><a class="ts-menu-item ts-menu-item-danger" href="#delete"><span>Delete</span></a></div>
        </details>
        """;
}
