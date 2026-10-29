using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Identity;

/// <summary>
/// The sign-in code email as the hosted page sends it: subject, plain text and HTML in the browser's language, matched
/// against committed snapshots, and the HTML checked in Chromium in light and dark mode.
/// Set <c>TS_AUTH_UPDATE_SNAPSHOTS=1</c> to rewrite the snapshots after an intended change, then review the diff.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class AuthEmailTests(TerraBaseFixture fixture)
{
    [ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
    public required BrowserFixture Chromium { get; init; }

    [Test]
    [Arguments(null, "en", "{0} is your TerraScale sign-in code")]
    [Arguments("pt-BR,pt;q=0.9,en;q=0.8", "pt-BR", "{0} é o seu código de acesso para TerraScale")]
    [Arguments("fr-CH, fr;q=0.9, pt;q=0.5, en;q=0.6", "en", "{0} is your TerraScale sign-in code")]
    [Arguments("de, pt-PT;q=0.8, *;q=0.5", "pt-BR", "{0} é o seu código de acesso para TerraScale")]
    public async Task The_sign_in_code_email_is_localized_html_with_a_plain_text_alternative(string? acceptLanguage, string locale, string subject)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var message = await RequestCodeAsync(oidc, acceptLanguage);
        var code = SixDigits().Match(message.Text).Value;

        await Assert.That(code).IsNotEmpty();
        await Assert.That(message.Subject).IsEqualTo(string.Format(System.Globalization.CultureInfo.InvariantCulture, subject, code));
        await Assert.That(message.Text).Contains($"\n    {code}\n");
        await Assert.That(message.Html).Contains($"<html lang=\"{locale}\"");
        await Assert.That(message.Html).Contains($">{code}</span>");
        await Assert.That(message.Html).Contains("src=\"https://auth.test.invalid/admin/email-logo.png\"");
        await Assert.That(message.Html).Contains("alt=\"TerraScale\"");
        if (acceptLanguage is null or ['p', ..])
        {
            await MatchesSnapshotAsync($"sign_in_code.{locale}.txt", message.Text.Replace(code, "000000", StringComparison.Ordinal));
            await MatchesSnapshotAsync($"sign_in_code.{locale}.html", message.Html!.Replace(code, "000000", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task The_html_reads_well_in_light_and_dark_mode_with_the_served_logos()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var message = await RequestCodeAsync(oidc, acceptLanguage: null);
        var browser = await Chromium.BrowserAsync();

        foreach (var (scheme, visibleLogo, hiddenLogo) in new[] { (ColorScheme.Light, ".logo-light", ".logo-dark"), (ColorScheme.Dark, ".logo-dark", ".logo-light") })
        {
            await using var context = await browser.NewContextAsync(new() { ColorScheme = scheme, ViewportSize = new() { Width = 420, Height = 900 } });
            var page = await context.NewPageAsync();
            // The logos come from this server's asset route; nothing else may be fetched.
            var external = new List<string>();
            await page.RouteAsync("**/*", async route =>
            {
                var url = new Uri(route.Request.Url);
                if (url.Host == "auth.test.invalid" && url.AbsolutePath.StartsWith("/admin/email-logo", StringComparison.Ordinal))
                {
                    using var asset = await oidc.Client.GetAsync(url.AbsolutePath);
                    await Assert.That(asset.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(asset.Content.Headers.ContentType?.MediaType).IsEqualTo("image/png");
                    await route.FulfillAsync(new() { Status = 200, ContentType = "image/png", BodyBytes = await asset.Content.ReadAsByteArrayAsync() });
                    return;
                }

                external.Add(route.Request.Url);
                await route.AbortAsync();
            });
            await page.SetContentAsync(message.Html!, new() { WaitUntil = WaitUntilState.Load });

            await Assert.That(await page.Locator(visibleLogo).IsVisibleAsync()).IsTrue();
            await Assert.That(await page.Locator(hiddenLogo).IsVisibleAsync()).IsFalse();
            await Assert.That(await page.Locator(visibleLogo).EvaluateAsync<int>("image => image.naturalWidth")).IsEqualTo(299);
            await Assert.That(external).IsEmpty();

            // WCAG AA: every text style keeps at least 4.5:1 against what is behind it.
            foreach (var (text, background) in new[] { (".heading", ".card"), (".text", ".card"), (".muted", ".card"), (".code", ".code-box") })
            {
                var ratio = await page.EvaluateAsync<double>(ContrastScript, new[] { text, background });
                await Assert.That(ratio).IsGreaterThanOrEqualTo(4.5).Because($"{text} on {background} in {scheme} mode");
            }

            // Nothing overflows a phone-width screen, and the code stays on one line.
            await Assert.That(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth")).IsTrue();
            await Assert.That(await page.Locator(".code").EvaluateAsync<int>("code => code.getClientRects().length")).IsEqualTo(1);
        }
    }

    private const string ContrastScript = """
        ([text, background]) => {
            const rgb = (selector, property) => getComputedStyle(document.querySelector(selector))[property].match(/\d+/g).slice(0, 3).map(Number);
            const luminance = ([r, g, b]) => [r, g, b].map(c => { c /= 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; })
                .reduce((sum, c, i) => sum + c * [0.2126, 0.7152, 0.0722][i], 0);
            const [a, b] = [luminance(rgb(text, 'color')), luminance(rgb(background, 'backgroundColor'))];
            return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
        }
        """;

    private static async Task<EmailMessage> RequestCodeAsync(OidcHarness oidc, string? acceptLanguage)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ui/{OidcIssuer.Slug}/sign-in")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "alice@example.test", ["next"] = string.Empty }),
        };
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        if (acceptLanguage is not null)
        {
            request.Headers.Add("Accept-Language", acceptLanguage);
        }

        using var response = await oidc.Client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var (recipient, message) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo("alice@example.test");
        return message;
    }

    private static async Task MatchesSnapshotAsync(string name, string actual, [CallerFilePath] string source = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(source)!, "Snapshots", name);
        if (Environment.GetEnvironmentVariable("TS_AUTH_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }

        await Assert.That(File.Exists(path)).IsTrue().Because($"{path} is missing; run with TS_AUTH_UPDATE_SNAPSHOTS=1");
        await Assert.That(actual).IsEqualTo(await File.ReadAllTextAsync(path)).Because($"{name} changed; review it and run with TS_AUTH_UPDATE_SNAPSHOTS=1");
    }

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();
}
