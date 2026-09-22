namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Playwright;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;

/// <summary>
/// Hosted-UI XSS and CSRF posture, driven through a real headless Chromium
/// (Playwright): attacker-influenced values must render as text/attribute
/// values and never execute, the session cookie must carry SameSite=Lax (the
/// CSRF defense for every session-gated POST), and state-changing routes must
/// not accept GET. Slug-charset validation stays on the wire (operator API).
/// </summary>
[NotInParallel]
public sealed class HostedUiSecurityTests
{
    private static async Task<AuthServiceFixture> ServiceAsync() => await AuthServiceFixture.GetAsync();

    private static async Task<(AuthServiceFixture Service, string InstanceId, string Slug)> InstanceAsync()
    {
        var service = await ServiceAsync();
        var (id, slug, _) = await service.CreateInstanceAsync("secure-" + Guid.NewGuid().ToString("N")[..8]);
        return (service, id, slug);
    }

    private static string Email() => $"s{Guid.NewGuid().ToString("N")[..10]}@example.com";

    private static string Url(AuthServiceFixture service, string path) => new Uri(service.Origin, path).ToString();

    /// <summary>Fails the test if any script dialog (alert/confirm/prompt) ever fires on the page.</summary>
    private static void FailOnDialog(IPage page)
    {
        page.Dialog += (_, dialog) => throw new InvalidOperationException(
            $"unexpected script dialog ({dialog.Type}): {dialog.Message}");
    }

    /// <summary>Signs up through the real sign-up page; the browser follows the redirect to /account.</summary>
    private static async Task SignUpAsync(IPage page, string url, string email, string? name = null)
    {
        await page.GotoAsync(url);
        await page.FillAsync("#su-email", email);
        await page.FillAsync("#su-password", "password123");
        if (name is not null)
            await page.FillAsync("#su-name", name);
        await Task.WhenAll(
            page.WaitForURLAsync(
                "**/account",
                new() { WaitUntil = WaitUntilState.DOMContentLoaded }),
            page.ClickAsync("button:has-text('Create account')"));
        await page.GetByRole(
            AriaRole.Heading,
            new() { Name = "Your account", Exact = true }).WaitForAsync();
    }

    // ---- Escaping -------------------------------------------------------------------------

    [Test]
    public async Task EscEncodesHtmlAndAttributeMetacharacters()
    {
        // Attribute-safe: double quote must be encoded too (every attribute interpolation in the
        // UI is double-quoted, so a bare " is the only way out of an attribute context).
        var escaped = Ui.Esc("""<script>"&'x</script>""");
        await Assert.That(escaped).DoesNotContain("<");
        await Assert.That(escaped).DoesNotContain("\"");
        await Assert.That(escaped).DoesNotContain("&\"");
        await Assert.That(escaped).Contains("&lt;script&gt;");
    }

    [Test]
    public async Task SignUpValidationErrorEchoesNameAndEmailEscaped()
    {
        var (service, _, slug) = await InstanceAsync();
        var name = """<script>alert("x")</script>""";
        var email = $"a'b@example.com";
        await using var page = await BrowserFixture.NewPageAsync();
        FailOnDialog(page);

        // Short password re-renders the form server-side with the submitted name and
        // email. Submitting via the DOM API keeps the browser's minlength validation
        // out of the way so the server-side re-render (the thing under test) happens.
        await page.GotoAsync(Url(service, $"/ui/{slug}/sign-up"));
        await page.FillAsync("#su-email", email);
        await page.FillAsync("#su-password", "short");
        await page.FillAsync("#su-name", name);
        await page.EvalOnSelectorAsync("form", "form => form.submit()");
        await page.Locator(".error").WaitForAsync();

        // The hostile name round-trips as an attribute VALUE (decoded by the DOM) — it
        // never became markup — and no dialog fired while the page rendered.
        await Assert.That(await page.Locator(".error").IsVisibleAsync()).IsTrue();
        await Assert.That(await page.InputValueAsync("#su-name")).IsEqualTo(name);
        await Assert.That(await page.InputValueAsync("#su-email")).IsEqualTo(email);
    }

    [Test]
    public async Task SignInPageEscapesHostileNextParameter()
    {
        var (service, _, slug) = await InstanceAsync();
        var hostile = "\"><script>alert(1)</script>";
        await using var page = await BrowserFixture.NewPageAsync();
        FailOnDialog(page);

        await page.GotoAsync(Url(service, $"/ui/{slug}/sign-in?next={Uri.EscapeDataString(hostile)}"));

        // The payload survives as the hidden input's value, not as markup; no dialog fired.
        await Assert.That(await page.InputValueAsync("input[name=next]")).IsEqualTo(hostile);
        await Assert.That(await page.Locator("input[name=next]").GetAttributeAsync("value")).IsEqualTo(hostile);
    }

    [Test]
    public async Task SessionsPageEscapesAttackerChosenUserAgent()
    {
        var (service, _, slug) = await InstanceAsync();
        var hostile = """UA <script>alert(1)</script>""";
        await using var page = await BrowserFixture.NewPageAsync(new BrowserNewPageOptions { UserAgent = hostile });
        FailOnDialog(page);

        // Signing up through the hostile-UA browser records that UA on the session.
        await SignUpAsync(page, Url(service, $"/ui/{slug}/sign-up"), Email());
        await page.GotoAsync(Url(service, $"/ui/{slug}/account/sessions"));

        // The hostile UA renders as the device row's TEXT (decoded by the DOM); no dialog fired.
        var devices = await page.Locator(".row").AllTextContentsAsync();
        await Assert.That(devices.Any(d => d.Contains(hostile, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task InstanceCreationRejectsSlugsThatCouldBreakOutOfHtml()
    {
        // Every page interpolates {slug} raw (URLs, and the passkey/passcode pages splice it into
        // inline <script>), so the creation-time charset check is the single load-bearing guard.
        // This is the operator API, not a hosted page — it stays on the wire.
        var service = await ServiceAsync();
        foreach (var slug in new[] { "\"><script>alert(1)</script>", "a b", "a/b", "a\\b", "a'b" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/instances")
            {
                Content = JsonContent.Create(new { slug, name = "x" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "operator-test-key");
            using var response = await service.Http.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    // ---- CSRF -----------------------------------------------------------------------------

    [Test]
    public async Task SessionCookieIsLaxHttpOnlyAndSecure()
    {
        var (service, _, slug) = await InstanceAsync();
        await using var page = await BrowserFixture.NewPageAsync();

        await SignUpAsync(page, Url(service, $"/ui/{slug}/sign-up"), Email());

        // Lax is the CSRF defense documented on SessionCookies; losing it would expose every
        // session-gated POST route to cross-site forms.
        var cookie = (await page.Context.CookiesAsync(service.Origin.ToString()))
            .Single(c => c.Name == "tsauth_session");
        await Assert.That(cookie.SameSite).IsEqualTo(SameSiteAttribute.Lax);
        await Assert.That(cookie.HttpOnly).IsTrue();
        await Assert.That(cookie.Secure).IsTrue();
    }

    [Test]
    public async Task CrossSiteFormPostWithoutCookieCannotRevokeSessions()
    {
        var (service, _, slug) = await InstanceAsync();
        await using var page = await BrowserFixture.NewPageAsync();
        await SignUpAsync(page, Url(service, $"/ui/{slug}/sign-up"), Email());

        // The session the browser just created, resolved through the store from the
        // (HttpOnly) cookie value exactly like the service itself does.
        var cookie = (await page.Context.CookiesAsync(service.Origin.ToString()))
            .Single(c => c.Name == "tsauth_session").Value;
        var session = await service.Store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(cookie))
            ?? throw new InvalidOperationException("session missing from store");

        // A real cross-site page whose only job is a top-level form POST at the
        // session-gated routes: SameSite=Lax must keep the cookie off that request.
        foreach (var (label, path) in new[]
        {
            ("revoke-all", $"/ui/{slug}/account/sessions/revoke-all"),
            ("sign-out", $"/ui/{slug}/sign-out"),
        })
        {
            await page.RouteAsync("http://cross.example/**", route => route.FulfillAsync(new RouteFulfillOptions
            {
                ContentType = "text/html",
                Body = $$"""<form id="f" method="post" action="{{Url(service, path)}}"></form><script>f.submit()</script>""",
            }));
            await page.GotoAsync("http://cross.example/attack-" + label);
            await page.WaitForURLAsync(url => url.StartsWith(service.Origin.ToString(), StringComparison.Ordinal));

            var after = await service.Store.GetSessionAsync(session.Id);
            await Assert.That(after?.Status).IsEqualTo("active");
        }

        // And the real browser (cookie attached) still has its session.
        var account = await page.GotoAsync(Url(service, $"/ui/{slug}/account"));
        await Assert.That(account!.Status).IsEqualTo((int)HttpStatusCode.OK);
    }

    [Test]
    public async Task StateChangingUiRoutesRejectGet()
    {
        // One representative mutating route per module: none may accept GET (a GET that mutates
        // on the session cookie would be reachable from an <img>/link despite SameSite=Lax,
        // which does send the cookie on top-level GET navigations).
        var (service, _, slug) = await InstanceAsync();
        await using var page = await BrowserFixture.NewPageAsync();
        foreach (var path in new[]
        {
            $"/ui/{slug}/sign-out",                                // Users
            $"/ui/{slug}/account/password",                        // Users
            $"/ui/{slug}/account/sessions/revoke-all",             // Users
            $"/ui/{slug}/mfa/enroll/start",                        // MFA
            $"/ui/{slug}/passcode/request",                        // Passcode
            $"/ui/{slug}/passkeys/register",                       // Passkeys
            $"/ui/{slug}/passkeys/manage/pk_x/delete",             // Passkeys
            $"/ui/{slug}/admin/connections",                       // Admin
        })
        {
            var response = await page.GotoAsync(Url(service, path));
            await Assert.That(response!.Status).IsEqualTo(405);
        }
    }
}
