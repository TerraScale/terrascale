using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class FormPostBrowserTests(TerraBaseFixture fixture)
{
    private const string Callback = "https://app.example.test/callback";

    [ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
    public required BrowserFixture Chromium { get; init; }

    [Test]
    [Arguments("", "code")]
    [Arguments("&prompt=consent", "error")]
    public async Task Browser_form_post_delivers_the_result_once_to_the_registered_callback(string extra, string result)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, ownerToken) = await oidc.SignInUserAsync(new Faker().Internet.Email().ToLowerInvariant());
        var project = await oidc.CreateProjectAsync(ownerToken, await oidc.CreateOrganizationAsync(ownerToken), "Form post project");
        var client = (await oidc.OkAsync(HttpMethod.Post, $"/v1/{project}/clients", ownerToken,
            new { name = "Form post app", type = "public", redirect_uris = new[] { Callback } }, HttpStatusCode.Created)).GetProperty("client_id").GetString()!;
        var user = (await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(project, new Faker().Internet.Email()))!;
        var session = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);
        var (_, challenge) = OidcHarness.Pkce();
        var path = OidcHarness.AuthorizePath(challenge, redirectUri: Callback, clientId: client, extra: "&response_mode=form_post" + extra)
            .Replace($"/{OidcIssuer.Slug}/", $"/{project}/", StringComparison.Ordinal);

        var posts = new List<string>();
        var violations = new List<string>();
        string? policy = null;
        var browser = await Chromium.BrowserAsync();
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("**/*", async route =>
        {
            var url = new Uri(route.Request.Url);
            if (route.Request.Url == Callback && route.Request.Method == "POST")
            {
                posts.Add(route.Request.PostData ?? string.Empty);
                await route.FulfillAsync(new() { Status = 200, ContentType = "text/plain", Body = "ok" });
                return;
            }

            if (url.Host != "auth.test.invalid")
            {
                await route.AbortAsync();
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url.PathAndQuery) { Headers = { { "Cookie", $"tsauth_session_{project}={session.Token}" } } };
            using var response = await oidc.Server.Client.SendAsync(request);
            policy = string.Join(", ", response.Headers.GetValues("Content-Security-Policy"));
            var headers = response.Headers.Concat(response.Content.Headers)
                .Where(header => header.Key is not ("Content-Length" or "Transfer-Encoding"))
                .ToDictionary(header => header.Key, header => string.Join(", ", header.Value));
            await route.FulfillAsync(new() { Status = (int)response.StatusCode, Headers = headers, BodyBytes = await response.Content.ReadAsByteArrayAsync() });
        });
        var page = await context.NewPageAsync();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.Ordinal))
            {
                violations.Add(message.Text);
            }
        };

        await page.GotoAsync(OidcHarness.PublicOrigin + path);
        await page.WaitForURLAsync(Callback);

        await Assert.That(violations).IsEmpty();
        await Assert.That(posts).Count().IsEqualTo(1);
        var fields = System.Web.HttpUtility.ParseQueryString(posts[0]);
        await Assert.That(fields["state"]).IsEqualTo("state-1");
        await Assert.That(fields[result]).IsNotNull();
        await Assert.That(fields[result == "code" ? "error" : "code"]).IsNull();
        await Assert.That(policy).IsEqualTo($"default-src 'none'; script-src '{FormPostSecurityPolicy.AutoSubmitScriptHash}'; "
            + "form-action https://app.example.test; base-uri 'none'; object-src 'none'; frame-ancestors 'none'");
    }

    [Test]
    public async Task The_pinned_script_hash_matches_the_page_OpenIddict_renders()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, ownerToken) = await oidc.SignInUserAsync(new Faker().Internet.Email().ToLowerInvariant());
        var project = await oidc.CreateProjectAsync(ownerToken, await oidc.CreateOrganizationAsync(ownerToken), "Form post hash");
        var client = (await oidc.OkAsync(HttpMethod.Post, $"/v1/{project}/clients", ownerToken,
            new { name = "Form post app", type = "public", redirect_uris = new[] { Callback } }, HttpStatusCode.Created)).GetProperty("client_id").GetString()!;
        var user = (await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(project, new Faker().Internet.Email()))!;
        var session = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);
        var (_, challenge) = OidcHarness.Pkce();
        var path = OidcHarness.AuthorizePath(challenge, redirectUri: Callback, clientId: client, extra: "&response_mode=form_post")
            .Replace($"/{OidcIssuer.Slug}/", $"/{project}/", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { { "Cookie", $"tsauth_session_{project}={session.Token}" } } };
        using var response = await oidc.Client.SendAsync(request);

        var scripts = ScriptPattern().Matches(await response.Content.ReadAsStringAsync()).Select(match => match.Groups[1].Value).ToList();
        await Assert.That(scripts).Count().IsEqualTo(1);
        await Assert.That("sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(scripts[0])))).IsEqualTo(FormPostSecurityPolicy.AutoSubmitScriptHash);
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
    }

    [Test]
    [Arguments("https://app.example.test/callback?x=1", "https://app.example.test")]
    [Arguments("http://127.0.0.1:5000/cb", "http://127.0.0.1:5000")]
    [Arguments("https://bücher.example/cb", "https://xn--bcher-kva.example")]
    [Arguments("https://[::1]:8443/cb", "https:")]
    [Arguments("com.example.app:/callback", "com.example.app:")]
    [Arguments("not a uri", null)]
    public async Task Form_targets_are_valid_csp_sources(string redirect, string? expected) =>
        await Assert.That(FormPostSecurityPolicy.FormTarget(redirect)).IsEqualTo(expected);

    [GeneratedRegex("<script>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ScriptPattern();
}
