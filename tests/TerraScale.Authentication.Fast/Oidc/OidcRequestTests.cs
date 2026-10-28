using System.Net;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OidcRequestTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Redirect_uris_pkce_and_client_secrets_must_match_exactly()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();

        // An unregistered redirect URI is answered here, never redirected to, with the hosted error page.
        using var unregistered = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, redirectUri: "https://evil.example/callback"), session);
        await Assert.That(unregistered.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unregistered.Headers.Location).IsNull();
        await Assert.That(unregistered.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        await Assert.That(await unregistered.Content.ReadAsStringAsync()).Contains("<h1>Request refused</h1>");
        await Assert.That(unregistered.Headers.GetValues("Content-Security-Policy").Single()).Contains("script-src 'self' 'nonce-");

        // PKCE is mandatory and only S256 is accepted. OpenIddict checks these parameters before it resolves the
        // client's redirect URI, so the error is answered here rather than redirected.
        foreach (var path in new[] { OidcHarness.AuthorizePath(string.Empty).Replace("&code_challenge=&code_challenge_method=S256", string.Empty, StringComparison.Ordinal), OidcHarness.AuthorizePath(verifier, method: "plain") })
        {
            using var refused = await oidc.AuthorizeAsync(path, session);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(refused.Headers.Location).IsNull();
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("<p class=\"hint\">Error code: invalid_request</p>");
        }

        var code = await oidc.CodeAsync(session, challenge);
        using var wrongRedirect = await oidc.RedeemCodeAsync(code, verifier, OidcHarness.LoopbackRedirectUri);
        await Assert.That(wrongRedirect.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(wrongRedirect)).IsEqualTo("invalid_grant");

        var (otherVerifier, _) = OidcHarness.Pkce();
        using var wrongVerifier = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), otherVerifier);
        await Assert.That(wrongVerifier.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(wrongVerifier)).IsEqualTo("invalid_grant");

        using var wrongSecret = await oidc.TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = await oidc.CodeAsync(session, challenge), ["redirect_uri"] = OidcHarness.RedirectUri, ["code_verifier"] = verifier },
            secret: OidcHarness.ClientSecret + "x");
        await Assert.That(wrongSecret.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await OidcHarness.ErrorAsync(wrongSecret)).IsEqualTo("invalid_client");
    }

    [Test]
    public async Task Prompts_that_need_an_interactive_sign_in_answer_login_required()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var (_, challenge) = OidcHarness.Pkce();

        using var none = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, extra: "&prompt=none"), session: null);
        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(none.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcHarness.RedirectUri);
        await Assert.That(OidcHarness.Query(none.Headers.Location!)["error"]).IsEqualTo("login_required");
        await Assert.That(OidcHarness.Query(none.Headers.Location!)["state"]).IsEqualTo("state-1");

        var session = await oidc.SignInAsync("alice@example.test");
        using var login = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, extra: "&prompt=login"), session);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(login.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcHarness.RedirectUri);
        await Assert.That(OidcHarness.Query(login.Headers.Location!)["error"]).IsEqualTo("login_required");
        await Assert.That(OidcHarness.Query(login.Headers.Location!).ContainsKey("code")).IsFalse();
    }

    [Test]
    public async Task Logout_revokes_the_session_and_continues_only_to_a_registered_uri()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var (_, challenge) = OidcHarness.Pkce();
        var logout = $"/{OidcIssuer.Slug}/oauth/logout?client_id={OidcIssuer.DashboardClientId}&state=x&post_logout_redirect_uri=";

        using var unregistered = await oidc.AuthorizeAsync(logout + Uri.EscapeDataString("https://evil.example/signed-out"), session);
        await Assert.That(unregistered.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unregistered.Headers.Location).IsNull();
        await Assert.That(await unregistered.Content.ReadAsStringAsync()).Contains("<h1>Request refused</h1>");

        // A registered URI gets the hosted Signed out page, which continues there (with state) by link and meta refresh.
        using var registered = await oidc.AuthorizeAsync(logout + Uri.EscapeDataString(OidcHarness.PostLogoutRedirectUri), session);
        var signedOut = await registered.Content.ReadAsStringAsync();
        var returnTo = $"{OidcHarness.PostLogoutRedirectUri}?state=x";
        await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(registered.Headers.Location).IsNull();
        await Assert.That(signedOut).Contains("<h1>Signed out</h1>");
        await Assert.That(signedOut).Contains("You are signed out.");
        await Assert.That(signedOut).Contains($"<meta http-equiv=\"refresh\" content=\"2;url={returnTo}\">");
        await Assert.That(signedOut).Contains($"<a class=\"btn\" href=\"{returnTo}\">Continue");
        await Assert.That(ClearsSessionCookie(registered)).IsTrue();

        // The session is gone server-side, not just forgotten by the browser.
        using var after = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge), session);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(after.Headers.Location!.OriginalString).StartsWith($"/ui/{OidcIssuer.Slug}/sign-in?next=");

        clock.Advance(HostedAuth.MailCooldown);
        var second = await oidc.SignInAsync("alice@example.test");
        using var page = await oidc.AuthorizeAsync($"/{OidcIssuer.Slug}/oauth/logout", second);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var plain = await page.Content.ReadAsStringAsync();
        await Assert.That(plain).Contains("You are signed out.");
        // Without a redirect URI the page stays put.
        await Assert.That(plain).DoesNotContain("http-equiv=\"refresh\"");
        await Assert.That(ClearsSessionCookie(page)).IsTrue();
        using var afterPage = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge), second);
        await Assert.That(afterPage.Headers.Location!.OriginalString).StartsWith($"/ui/{OidcIssuer.Slug}/sign-in?next=");
    }

    [Test]
    public async Task Authorize_logout_and_userinfo_accept_form_posts()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, session, tokens) = await oidc.SignInAndRedeemAsync();
        var (_, challenge) = OidcHarness.Pkce();
        var path = OidcHarness.AuthorizePath(challenge);
        var parameters = OidcHarness.Query(new Uri(OidcHarness.PublicOrigin + path)).ToDictionary(pair => pair.Key, pair => pair.Value!);
        var authorize = $"/{OidcIssuer.Slug}/oauth/authorize";

        // Without a session the sign-in page resumes the posted request as a GET with the same parameters.
        using var signIn = await Post(authorize, parameters, cookie: null);
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var next = OidcHarness.Query(new Uri(OidcHarness.PublicOrigin + signIn.Headers.Location!.OriginalString))["next"]!;
        await Assert.That(next).StartsWith(authorize + "?");
        await Assert.That(OidcHarness.Query(new Uri(OidcHarness.PublicOrigin + next))).IsEquivalentTo(OidcHarness.Query(new Uri(OidcHarness.PublicOrigin + path)));

        using var code = await Post(authorize, parameters, session);
        await Assert.That(code.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(code.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcHarness.RedirectUri);
        await Assert.That(OidcHarness.Query(code.Headers.Location!)["code"]).IsNotNull();

        // A posted redirect URI is validated exactly, as by GET.
        using var evil = await Post(authorize, new(parameters) { ["redirect_uri"] = "https://evil.example/callback" }, session);
        await Assert.That(evil.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(evil.Headers.Location).IsNull();

        using var userInfoRequest = new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/oauth/userinfo") { Content = new FormUrlEncodedContent([]) };
        userInfoRequest.Headers.Authorization = new("Bearer", tokens.GetProperty("access_token").GetString());
        using var userInfo = await oidc.Client.SendAsync(userInfoRequest);
        await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(userInfo.Headers.CacheControl?.NoStore).IsTrue();

        using var logout = await Post($"/{OidcIssuer.Slug}/oauth/logout", new() { ["client_id"] = OidcIssuer.DashboardClientId, ["state"] = "x", ["post_logout_redirect_uri"] = OidcHarness.PostLogoutRedirectUri }, session);
        await Assert.That(logout.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await logout.Content.ReadAsStringAsync()).Contains($"href=\"{OidcHarness.PostLogoutRedirectUri}?state=x\"");
        await Assert.That(ClearsSessionCookie(logout)).IsTrue();
        using var after = await oidc.AuthorizeAsync(path, session);
        await Assert.That(after.Headers.Location!.OriginalString).StartsWith($"/ui/{OidcIssuer.Slug}/sign-in?next=");

        async Task<HttpResponseMessage> Post(string target, Dictionary<string, string> fields, string? cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new FormUrlEncodedContent(fields) };
            if (cookie is not null)
            {
                request.Headers.Add("Cookie", $"tsauth_session={cookie}");
            }

            return await oidc.Client.SendAsync(request);
        }
    }

    [Test]
    public async Task Only_the_terrascale_issuer_exists()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);

        using var other = await oidc.Client.GetAsync("/other/.well-known/openid-configuration");
        await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var otherKeys = await oidc.Client.GetAsync("/other/.well-known/jwks.json");
        await Assert.That(otherKeys.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static bool ClearsSessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(cookie => cookie.StartsWith("tsauth_session=;", StringComparison.Ordinal) && cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
}
