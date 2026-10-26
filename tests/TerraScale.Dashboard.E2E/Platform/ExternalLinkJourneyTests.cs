using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardRootFixture>]
public sealed class ExternalLinkJourneyTests(DashboardRootFixture fixture)
{
    private static readonly Regex Anchors = new("<a\\b(?<attributes>[^>]*?)>(?<body>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    [Test]
    public async Task ExternalUrlsInEveryViewUseSharedNavigationAndHint()
    {
        var views = Directory.EnumerateFiles(Path.Combine(fixture.Root, "src", "TerraScale.Dashboard"), "*.cshtml", SearchOption.AllDirectories);
        var count = 0;
        foreach (var view in views)
        {
            foreach (Match anchor in Anchors.Matches(await File.ReadAllTextAsync(view)))
            {
                // Literal URLs (including URLs inside Razor expressions), plus audited dynamic destinations.
                var attributes = anchor.Groups["attributes"].Value;
                var dynamicDestinations = new[] { "HomeConnectSnippets.ApiUrl", "@info.ConsoleUrl", "href=\"@value\"", "@invoice.HostedUrl", "@invoice.PdfUrl", "@data.State.PaymentUrl", "@TsAuthGateway.RequestSignInCodeUrl" };
                if (!Regex.IsMatch(attributes, "https?://|href=[\"']//", RegexOptions.IgnoreCase) &&
                    !dynamicDestinations.Any(destination => attributes.Contains(destination, StringComparison.Ordinal))) continue;
                count++;
                await Assert.That(attributes).Contains("@(ExternalLink.Attributes)").Because(view);
                await Assert.That(anchor.Groups["body"].Value).Contains("@(ExternalLink.Hint)").Because(view);
            }
        }
        await Assert.That(count > 30).IsTrue();
    }

    [Test]
    public async Task RenderedPagesIncludeSafeExternalNavigationWithoutChangingInternalLinks()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        // Anonymous error page, including its contact link.
        using (var missing = await client.GetAsync("/missing-external-link-proof"))
            await CheckLinks(await missing.Content.ReadAsStringAsync(), scenario.Origin);
        var persona = scenario.Personas.Seeded;
        using var login = await HttpJourney.LoginAsync(client, persona.Email, persona.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        foreach (var path in new[] { "/", "/?tab=connect", "/support", "/auth", "/auth/social", "/organizations/choose", "/organization?tab=billing", "/imports", "/databases/" + ReferenceFixtures.PrimaryId + "/functions", "/databases/" + ReferenceFixtures.PrimaryId, "/missing-external-link-proof" })
        {
            using var response = await client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("<a ").Because(path);
            await CheckLinks(html, scenario.Origin);
        }
    }

    private static async Task CheckLinks(string html, Uri origin)
    {
        foreach (Match anchor in Anchors.Matches(html))
        {
            var attributes = anchor.Groups["attributes"].Value;
            var hrefMatch = Regex.Match(attributes, "\\bhref=([\"'])(.*?)\\1");
            var href = WebUtility.HtmlDecode(hrefMatch.Groups[2].Value);
            if (!Uri.TryCreate(origin, href, out var url) || url.Scheme is not ("http" or "https")) continue;
            if (url.Authority == origin.Authority)
            {
                await Assert.That(attributes.Contains("target=\"_blank\"", StringComparison.Ordinal)).IsFalse().Because(href);
                continue;
            }
            await Assert.That(attributes).Contains("target=\"_blank\"").Because(href);
            await Assert.That(attributes).Contains("rel=\"noopener noreferrer\"").Because(href);
            await Assert.That(anchor.Groups["body"].Value).Contains("opens in a new tab").Because(href);
        }
    }

    [Test, Category("Slow")]
    public async Task ClickingStatusAndDocsOpensAnotherTabAndPreservesDashboard()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var persona = scenario.Personas.Seeded;
        await BrowserAudit.SignInAsync(page, scenario.Origin, email: persona.Email, password: persona.Password);
        await page.WaitForURLAsync(url => BrowserAudit.WithoutWorkspace(url) == scenario.Origin.ToString());
        foreach (var path in new[] { "/", "/organizations/choose", "/missing-external-link-proof" })
        {
            await page.GotoAsync(new Uri(scenario.Origin, path).ToString());
            foreach (var host in new[] { "status.terrascale.tech", "docs.terrascale.tech" })
            {
                var link = page.Locator($"a[href='https://{host}']").First;
                var original = page.Url;
                // Isolate tab behavior from availability of third-party services in CI.
                await context.RouteAsync($"https://{host}/**", route => route.FulfillAsync(new() { Body = "External destination" }));
                var popup = await page.RunAndWaitForPopupAsync(() => link.ClickAsync());
                await popup.WaitForLoadStateAsync();
                await Assert.That(new Uri(popup.Url).Host).IsEqualTo(host);
                await Assert.That(await popup.EvaluateAsync<bool>("window.opener === null")).IsTrue();
                await Assert.That(page.Url).IsEqualTo(original);
                await popup.CloseAsync();
            }
        }
    }
}
