namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

/// <summary>
/// Instance settings end to end: branding inputs are operator-controlled and flow into
/// every hosted page, so the gates (http(s)-only logo URL, #rrggbb theme color),
/// round-trip fidelity, and the escaped render are all pinned here.
/// </summary>
[NotInParallel]
public sealed class InstanceSettingsE2eTests
{
    private static async Task<(AuthServiceFixture Service, string Slug, string Key)> InstanceAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, key) = await service.CreateInstanceAsync("brand-" + Guid.NewGuid().ToString("N")[..8]);
        return (service, slug, key);
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, string apiKey, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    /// <summary>Raw JSON body: JsonContent drops null-valued properties (Web defaults),
    /// so explicit-null PATCH probes need the literal bytes on the wire.</summary>
    private static HttpRequestMessage RawRequest(HttpMethod method, string uri, string apiKey, string jsonBody)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static async Task<JsonElement> SendAsync(HttpClient http, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Test]
    public async Task PatchRejectsLogoUrlsThatAreNotAbsoluteHttpOrHttps()
    {
        var (service, slug, key) = await InstanceAsync();

        // javascript:/data:/vbscript: are valid absolute URIs (the classic bypass of an
        // "is absolute" check); //host and /path parse as file: URIs; relatives fail outright.
        string[] hostile =
        {
            "javascript:alert(1)",
            "JAVASCRIPT:alert(1)",
            "data:text/html,<b>",
            "vbscript:msgbox(1)",
            "java\tscript:alert(1)",
            "//evil.com/x.png",
            "/logo.png",
            "logo.png",
            "ftp://cdn.test/logo.png",
            "file:///etc/passwd",
        };
        foreach (var url in hostile)
        {
            var body = await SendAsync(service.Http,
                Request(HttpMethod.Patch, $"/v1/{slug}/instance", key, new { logo_url = url }),
                HttpStatusCode.BadRequest);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_logo_url");
        }

        // None of it was stored.
        var stored = await SendAsync(service.Http, Request(HttpMethod.Get, $"/v1/{slug}/instance", key), HttpStatusCode.OK);
        await Assert.That(stored.GetProperty("logo_url").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task PatchAcceptsHttpAndHttpsLogoUrlsAndRoundTripsSnakeCase()
    {
        var (service, slug, key) = await InstanceAsync();

        foreach (var url in new[] { "https://cdn.test/logo.png", "http://cdn.test/logo.png" })
        {
            await SendAsync(service.Http,
                Request(HttpMethod.Patch, $"/v1/{slug}/instance", key, new { logo_url = url }),
                HttpStatusCode.OK);
        }

        await SendAsync(service.Http,
            Request(HttpMethod.Patch, $"/v1/{slug}/instance", key,
                new { name = "Branded Co", logo_url = "https://cdn.test/logo.png", theme_color = "#6c5ce7" }),
            HttpStatusCode.OK);

        // GET returns the branded fields exactly as stored, under the same snake_case
        // keys the PATCH response uses (Web defaults would silently rename to logoUrl).
        var stored = await SendAsync(service.Http, Request(HttpMethod.Get, $"/v1/{slug}/instance", key), HttpStatusCode.OK);
        await Assert.That(stored.GetProperty("slug").GetString()).IsEqualTo(slug);
        await Assert.That(stored.GetProperty("name").GetString()).IsEqualTo("Branded Co");
        await Assert.That(stored.GetProperty("logo_url").GetString()).IsEqualTo("https://cdn.test/logo.png");
        await Assert.That(stored.GetProperty("theme_color").GetString()).IsEqualTo("#6c5ce7");
        await Assert.That(stored.GetProperty("created_at").GetInt64()).IsGreaterThan(0L);

        // Unknown fields are ignored, not rejected; unset fields keep their stored value.
        await SendAsync(service.Http,
            Request(HttpMethod.Patch, $"/v1/{slug}/instance", key, new { name = "Renamed", bogus = true }),
            HttpStatusCode.OK);
        var afterRename = await SendAsync(service.Http, Request(HttpMethod.Get, $"/v1/{slug}/instance", key), HttpStatusCode.OK);
        await Assert.That(afterRename.GetProperty("name").GetString()).IsEqualTo("Renamed");
        await Assert.That(afterRename.GetProperty("logo_url").GetString()).IsEqualTo("https://cdn.test/logo.png");
        await Assert.That(afterRename.GetProperty("theme_color").GetString()).IsEqualTo("#6c5ce7");

        // Explicit null on the wire means "unchanged", so branding cannot be wiped by a null.
        await SendAsync(service.Http,
            RawRequest(HttpMethod.Patch, $"/v1/{slug}/instance", key, """{"logo_url":null,"theme_color":null}"""),
            HttpStatusCode.OK);
        var afterNull = await SendAsync(service.Http, Request(HttpMethod.Get, $"/v1/{slug}/instance", key), HttpStatusCode.OK);
        await Assert.That(afterNull.GetProperty("logo_url").GetString()).IsEqualTo("https://cdn.test/logo.png");
        await Assert.That(afterNull.GetProperty("theme_color").GetString()).IsEqualTo("#6c5ce7");
    }

    [Test]
    public async Task PatchEnforcesSixDigitHexThemeColor()
    {
        var (service, slug, key) = await InstanceAsync();

        string[] hostile =
        {
            "",
            "fff",
            "#fff",
            "#fffff",
            "#fffffff",
            "#ffffff00",
            "#gggggg",
            "rgb(0,0,0)",
            "#fff'><script>",
            "  #6c5ce7",
        };
        foreach (var color in hostile)
        {
            var body = await SendAsync(service.Http,
                RawRequest(HttpMethod.Patch, $"/v1/{slug}/instance", key,
                    JsonSerializer.Serialize(new { theme_color = color })),
                HttpStatusCode.BadRequest);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_theme_color");
        }

        // Only #rrggbb (case-insensitive hex) is stored — the CSS interpolation is safe by construction.
        foreach (var color in new[] { "#6c5ce7", "#ABCDEF" })
        {
            await SendAsync(service.Http,
                RawRequest(HttpMethod.Patch, $"/v1/{slug}/instance", key,
                    JsonSerializer.Serialize(new { theme_color = color })),
                HttpStatusCode.OK);
            var stored = await SendAsync(service.Http, Request(HttpMethod.Get, $"/v1/{slug}/instance", key), HttpStatusCode.OK);
            await Assert.That(stored.GetProperty("theme_color").GetString()).IsEqualTo(color);
        }
    }

    [Test]
    public async Task BrandedFunnelPagesRenderBrandingEscaped()
    {
        var (service, slug, key) = await InstanceAsync();

        // A VALID https URL carrying attribute-breakout payloads: quotes, spaces, tags.
        // Validation cannot reject it (external logos are legit), so the renderer must escape.
        const string logo = "https://cdn.test/logo \"onerror='alert(1)'.png";
        await SendAsync(service.Http,
            RawRequest(HttpMethod.Patch, $"/v1/{slug}/instance", key,
                JsonSerializer.Serialize(new { logo_url = logo, theme_color = "#123456" })),
            HttpStatusCode.OK);

        var pages = new[]
        {
            ("/ui/" + slug + "/sign-in", HttpStatusCode.OK),
            ("/ui/" + slug + "/sign-up", HttpStatusCode.OK),
            ("/ui/" + slug + "/forgot-password", HttpStatusCode.OK),
            ("/ui/" + slug + "/reset-password?token=bad", HttpStatusCode.BadRequest),
        };
        await using var page = await BrowserFixture.NewPageAsync();
        page.Dialog += (_, dialog) => throw new InvalidOperationException(
            $"unexpected script dialog ({dialog.Type}): {dialog.Message}");
        foreach (var (path, status) in pages)
        {
            // DOMContentLoaded: the branded <img> points at a dead host on purpose, and its
            // failed fetch must not stall the assertions (attributes exist at parse time).
            var response = await page.GotoAsync(
                new Uri(service.Origin, path).ToString(),
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(response!.Status).IsEqualTo((int)status);

            // Branding actually reaches the page: the logo renders as the img's src and the
            // theme color as the root --accent custom property the stylesheet consumes.
            await Assert.That(await page.Locator("img.logo-img").GetAttributeAsync("src")).IsEqualTo(logo);
            var accent = await page.EvalOnSelectorAsync(
                "html", "el => getComputedStyle(el).getPropertyValue('--accent').trim()");
            await Assert.That(accent?.ToString()).IsEqualTo("#123456");

            // And only as escaped output: the payload never became markup — the img carries
            // exactly its legit attributes (no onerror survived the quote), no script
            // element exists, and no dialog fired while the page rendered.
            var attributes = await page.Locator("img.logo-img").EvaluateAsync(
                "el => [...el.attributes].map(a => a.name).join(',')");
            await Assert.That(attributes?.ToString()).IsEqualTo("src,alt,class");
            await Assert.That(await page.Locator("script").CountAsync()).IsEqualTo(0);
        }
    }
}
