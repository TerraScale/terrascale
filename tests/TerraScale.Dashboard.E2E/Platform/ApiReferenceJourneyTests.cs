using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardRootFixture>]
public sealed class ApiReferenceJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task PublicReferenceHasMetadataFreshNoncesAndTheAbsoluteProductionServer()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        string? previous = null;
        foreach (var path in new[] { "/reference/", "/reference/integration" })
        {
            using var response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await response.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("<html lang=\"en\">");
            await Assert.That(html).Contains("<title>TerraScale API reference</title>");
            var nonce = WebUtility.HtmlDecode(Regex.Match(html, "property=\"csp-nonce\" content=\"([^\"]+)\"").Groups[1].Value);
            await Assert.That(nonce.Length).IsGreaterThan(20);
            await Assert.That(nonce).IsNotEqualTo(previous);
            previous = nonce;
            var policy = response.Headers.GetValues("Content-Security-Policy").Single();
            var directives = policy.Split(';', StringSplitOptions.TrimEntries);
            await Assert.That(directives.Single(value => value.StartsWith("script-src ", StringComparison.Ordinal)))
                .IsEqualTo($"script-src 'self' 'nonce-{nonce}'");
            await Assert.That(directives.Single(value => value.StartsWith("style-src ", StringComparison.Ordinal)))
                .IsEqualTo($"style-src 'self' 'nonce-{nonce}'");
            await Assert.That(policy).DoesNotContain("unsafe-eval");
            await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
            await Assert.That(Regex.Matches(html, "<script").Count).IsEqualTo(3);
            var noncedScripts = Regex.Matches(html, "<script[^>]*nonce=\"([^\"]+)\"");
            await Assert.That(noncedScripts.Count).IsEqualTo(3);
            foreach (Match script in noncedScripts)
                await Assert.That(WebUtility.HtmlDecode(script.Groups[1].Value)).IsEqualTo(nonce);
        }
        using var icon = await client.GetAsync("/brand/logo-icon-blue.svg");
        await Assert.That(icon.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(icon.Content.Headers.ContentType!.MediaType).IsEqualTo("image/svg+xml");
        using var spec = JsonDocument.Parse(await client.GetStringAsync("/openapi/integration.json"));
        await Assert.That(spec.RootElement.GetProperty("servers")[0].GetProperty("url").GetString())
            .IsEqualTo("https://dash.terrascale.tech");
    }

    [Test]
    public async Task PublicBundlePreservesCompressionConditionalCachingAndEncodingRefusals()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var plain = await client.GetAsync("/reference/terrascale.js");
        await Assert.That(plain.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var bytes = await plain.Content.ReadAsByteArrayAsync();
        await Assert.That(plain.Headers.CacheControl!.Public && plain.Headers.CacheControl.NoCache && !plain.Headers.CacheControl.NoStore).IsTrue();
        await Assert.That(plain.Content.Headers.ContentEncoding).IsEmpty();
        using var zippedRequest = new HttpRequestMessage(HttpMethod.Get, "/reference/terrascale.js");
        zippedRequest.Headers.AcceptEncoding.ParseAdd("gzip");
        using var zipped = await client.SendAsync(zippedRequest);
        await Assert.That(zipped.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(zipped.Headers.Vary.Contains("Accept-Encoding")).IsTrue();
        await Assert.That(zipped.Content.Headers.ContentEncoding.Single()).IsEqualTo("gzip");
        var compressed = await zipped.Content.ReadAsByteArrayAsync();
        await Assert.That(compressed.Length < bytes.Length / 2).IsTrue();
        using var decoder = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        await decoder.CopyToAsync(decoded);
        await Assert.That(decoded.ToArray().SequenceEqual(bytes)).IsTrue();
        await Assert.That(zipped.Headers.ETag).IsNotEqualTo(plain.Headers.ETag);
        foreach (var encoding in new[] { "gzip", "gzip;q=0" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/reference/terrascale.js");
            request.Headers.AcceptEncoding.ParseAdd(encoding);
            request.Headers.IfNoneMatch.Add(encoding == "gzip" ? zipped.Headers.ETag! : plain.Headers.ETag!);
            using var cached = await client.SendAsync(request);
            await Assert.That(cached.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
            await Assert.That((await cached.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
        }
        using var unacceptable = new HttpRequestMessage(HttpMethod.Get, "/reference/terrascale.js");
        unacceptable.Headers.AcceptEncoding.ParseAdd("gzip;q=0, identity;q=0, *;q=0");
        using var refused = await client.SendAsync(unacceptable);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotAcceptable);
    }

    [Test, Category("Slow")]
    public async Task ReferenceStylesAndKeyboardControlsWorkUnderTheUnchangedStrictPolicy()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ColorScheme = ColorScheme.Light });
        await context.AddInitScriptAsync("""
            window.referenceViolations = [];
            addEventListener('securitypolicyviolation', e => referenceViolations.push(e.effectiveDirective));
            localStorage.setItem('scalar-color-mode', 'light');
            """);
        var page = await context.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        await page.GotoAsync(new Uri(scenario.Origin, "/reference/").ToString());
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "TerraScale Dashboard integration API", Exact = true })).ToBeVisibleAsync();
        await Expect(page).ToHaveTitleAsync("TerraScale API reference");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "en");
        await Expect(page.GetByRole(AriaRole.Button, new() { NameRegex = new("Set (dark|light) mode") })).ToHaveCountAsync(0);
        await Assert.That(await page.EvaluateAsync<bool>("""
            () => {
              const nonce = document.querySelector('meta[property="csp-nonce"]').content;
              const styles = [...document.querySelectorAll('style')];
              return styles.length >= 4 && styles.every(s => s.nonce === nonce && !!s.sheet)
                && [...document.scripts].every(s => s.nonce === nonce)
                && getComputedStyle(document.documentElement).colorScheme === 'dark'
                && getComputedStyle(document.body).backgroundColor === 'rgb(21, 21, 21)'
                && getComputedStyle(document.body).fontFamily.includes('Geist')
                && document.querySelector('link[rel="icon"]').href.includes('/brand/logo-icon-blue.svg');
            }
            """)).IsTrue();
        var search = page.GetByRole(AriaRole.Button, new() { NameRegex = new("Open Search") });
        await search.FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        await Assert.That(await page.EvaluateAsync<bool>("""
            () => { const s = getComputedStyle(document.activeElement);
              return document.activeElement.matches(':focus-visible') && s.outlineStyle === 'solid'
                && s.outlineWidth === '2px' && s.outlineColor === 'rgb(107, 147, 255)'; }
            """)).IsTrue();
        await search.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Enter search query" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Enter search query" }).FillAsync("no-such-endpoint-q15");
        await Expect(page.GetByRole(AriaRole.Option)).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("Escape");
        await Expect(search).ToBeFocusedAsync();
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Test Request") }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new()
        { Name = "Send get request to https://dash.terrascale.tech/api/v1/auth/me" })).ToBeVisibleAsync();
        await Assert.That(await page.EvaluateAsync<bool>("""
            () => [...document.querySelectorAll('style')].every(s => s.sheet &&
              s.nonce === document.querySelector('meta[property="csp-nonce"]').content)
            """)).IsTrue();
        await page.GetByRole(AriaRole.Button, new() { Name = "Close Client", Exact = true }).ClickAsync();
        foreach (var width in new[] { 1280, 390 })
        {
            await page.SetViewportSizeAsync(width, 800);
            if (width == 390)
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Open Menu", Exact = true }).ClickAsync();
                await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Introduction", Exact = true })).ToBeVisibleAsync();
            }
            var undersized = await page.EvaluateAsync<string[]>("""
                () => [...document.querySelectorAll('a[href],button,input:not([type=hidden]),select,textarea,summary,[tabindex="0"],[role=button],[role=tab],[role=menuitem],[role=option],[role=checkbox],[role=radio],[role=switch]')]
                  .filter(e => e.checkVisibility() && !e.closest('[inert]'))
                  .filter(e => { const r = e.getBoundingClientRect(); return r.width < 24 || r.height < 24; })
                  .map(e => `${e.tagName} ${e.textContent.trim().slice(0,60)}`)
                """);
            await Assert.That(undersized).IsEmpty();
        }
        await Assert.That(await page.EvaluateAsync<string[]>("() => referenceViolations")).IsEmpty();
        await Assert.That(errors.ToArray()).IsEmpty();
        // The adapter must never authorize arbitrary DOM-created styles or scripts.
        await page.EvaluateAsync("""
            () => {
              const style = document.createElement('style');
              style.textContent = 'body { --q15-unauthorized: applied; }';
              document.head.append(style);
              const script = document.createElement('script');
              script.textContent = 'window.q15Unauthorized = true';
              document.head.append(script);
            }
            """);
        await page.WaitForFunctionAsync("referenceViolations.length === 2");
        await Assert.That(await page.EvaluateAsync<bool>("""
            () => !window.q15Unauthorized && !getComputedStyle(document.body).getPropertyValue('--q15-unauthorized')
              && document.head.lastElementChild.previousElementSibling.sheet === null
            """)).IsTrue();
    }
}
