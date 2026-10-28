using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
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
    public async Task Prompt_none_answers_login_required_and_unsupported_prompts_are_refused()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var (_, challenge) = OidcHarness.Pkce();

        using var none = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, extra: "&prompt=none"), session: null);
        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(none.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcHarness.RedirectUri);
        await Assert.That(OidcHarness.Query(none.Headers.Location!)["error"]).IsEqualTo("login_required");
        await Assert.That(OidcHarness.Query(none.Headers.Location!)["state"]).IsEqualTo("state-1");

        // Discovery advertises only the prompts that work; the others, a repeated prompt and a malformed marker are refused.
        var discovery = JsonSerializer.Deserialize<JsonElement>(await oidc.Client.GetStringAsync($"/{OidcIssuer.Slug}/.well-known/openid-configuration"));
        await Assert.That(string.Join(' ', discovery.GetProperty("prompt_values_supported").EnumerateArray().Select(value => value.GetString()).Order()))
            .IsEqualTo("login none");
        var session = await oidc.SignInAsync("alice@example.test");
        // OpenIddict refuses a prompt value it does not support before it resolves the redirect URI, on the hosted error page.
        foreach (var extra in new[] { "&prompt=consent", "&prompt=select_account" })
        {
            using var refused = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, extra: extra), session);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(refused.Headers.Location).IsNull();
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("<p class=\"hint\">Error code: invalid_request</p>");
        }

        // A repeated prompt could hide login from the checks, so it and a repeated or malformed marker are refused.
        foreach (var extra in new[] { "&prompt=login&prompt=none", "&prompt=none&prompt=login", "&ts_login_after=soon", "&ts_login_after=1&ts_login_after=2" })
        {
            using var refused = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, extra: extra), session);
            await Assert.That(OidcHarness.Query(refused.Headers.Location!)["error"]).IsEqualTo("invalid_request");
            await Assert.That(OidcHarness.Query(refused.Headers.Location!).ContainsKey("code")).IsFalse();
        }
    }

    [Test]
    [Arguments("&prompt=login")]
    [Arguments("&max_age=0")]
    public async Task Prompt_login_and_max_age_need_one_deliberate_sign_in_after_the_request(string extra)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var user = await oidc.CreateUserAsync();
        var (verifier, challenge) = OidcHarness.Pkce();
        var session = await oidc.SignInAsync("alice@example.test");

        // A live session is not enough: the browser goes to sign-in, with a resume that keeps the demand but not prompt=login.
        using var login = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, extra: extra), session);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var signIn = login.Headers.Location!.OriginalString;
        await Assert.That(signIn).StartsWith($"/ui/{OidcIssuer.Slug}/sign-in?next=");
        await Assert.That(signIn).DoesNotContain("notice=");
        var next = Uri.UnescapeDataString(signIn[$"/ui/{OidcIssuer.Slug}/sign-in?next=".Length..]);
        await Assert.That(next).DoesNotContain("prompt=");
        await Assert.That(next).Contains("ts_login_after=");

        // The old session neither skips the sign-in form nor satisfies the resumed request, so returning without signing in loops back to the form.
        using var page = await oidc.AuthorizeAsync(signIn, session);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var replay = await oidc.AuthorizeAsync(next, session);
        await Assert.That(replay.Headers.Location!.OriginalString).StartsWith($"/ui/{OidcIssuer.Slug}/sign-in?next=");

        // One new sign-in completes it, and the code's tokens carry the new authentication time.
        var fresh = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);
        using var resumed = await oidc.AuthorizeAsync(next, fresh.Token);
        await Assert.That(resumed.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcHarness.RedirectUri);
        await Assert.That(OidcHarness.Query(resumed.Headers.Location!)["state"]).IsEqualTo("state-1");
        using var redeemed = await oidc.RedeemCodeAsync(OidcHarness.Query(resumed.Headers.Location!)["code"]!, verifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var after = long.Parse(OidcHarness.Query(new Uri(OidcHarness.PublicOrigin + next))["ts_login_after"]!, CultureInfo.InvariantCulture);
        var idToken = (await OidcHarness.JsonAsync(redeemed)).GetProperty("id_token").GetString()!;
        var authTime = new JsonWebToken(idToken).GetPayloadValue<long>("auth_time");
        await Assert.That(authTime).IsGreaterThanOrEqualTo(after / 1000);
    }

    [Test]
    public async Task Logout_revokes_the_session_and_continues_only_to_a_registered_uri()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var (_, challenge) = OidcHarness.Pkce();
        var logout = $"/{OidcIssuer.Slug}/oauth/logout?client_id={OidcIssuer.DashboardClientId}&state=x&post_logout_redirect_uri=";

        using var unregistered = await oidc.AuthorizeAsync(logout + Uri.EscapeDataString("https://evil.example/signed-out"), session);
        await Assert.That(unregistered.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unregistered.Headers.Location).IsNull();
        await Assert.That(await unregistered.Content.ReadAsStringAsync()).Contains("<h1>Request refused</h1>");

        // Without an ID token hint the session stays: the request only shows the confirmation, which posts its parameters back.
        using var unhinted = await oidc.AuthorizeAsync(logout + Uri.EscapeDataString(OidcHarness.PostLogoutRedirectUri), session);
        var confirm = await unhinted.Content.ReadAsStringAsync();
        await Assert.That(unhinted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(confirm).Contains("<h1>Sign out?</h1>");
        await Assert.That(confirm).Contains($"<form method=\"post\" action=\"/{OidcIssuer.Slug}/oauth/logout\">");
        await Assert.That(confirm).Contains($"<input type=\"hidden\" name=\"post_logout_redirect_uri\" value=\"{OidcHarness.PostLogoutRedirectUri}\">");
        await Assert.That(confirm).Contains("<input type=\"hidden\" name=\"state\" value=\"x\">");
        await Assert.That(confirm).Contains("<button type=\"submit\" name=\"confirm\" value=\"1\">");
        await Assert.That(confirm).DoesNotContain("alice@example.test");
        await Assert.That(confirm).DoesNotContain("http-equiv=\"refresh\"");
        await Assert.That(unhinted.Headers.Contains("Set-Cookie")).IsFalse();
        var fields = new Dictionary<string, string> { ["client_id"] = OidcIssuer.DashboardClientId, ["state"] = "x", ["post_logout_redirect_uri"] = OidcHarness.PostLogoutRedirectUri, ["confirm"] = "1" };

        // A cross-site page can post the confirmation, but not from the public origin; a confirm query parameter is not a post.
        foreach (var origin in new string?[] { "https://evil.example", null })
        {
            using var crossSite = await PostAsync(oidc, $"/{OidcIssuer.Slug}/oauth/logout", fields, session, origin);
            await Assert.That(await crossSite.Content.ReadAsStringAsync()).Contains("<h1>Sign out?</h1>");
            await Assert.That(crossSite.Headers.Contains("Set-Cookie")).IsFalse();
        }

        using var confirmQuery = await oidc.AuthorizeAsync(logout + Uri.EscapeDataString(OidcHarness.PostLogoutRedirectUri) + "&confirm=1", session);
        await Assert.That(await confirmQuery.Content.ReadAsStringAsync()).Contains("<h1>Sign out?</h1>");
        using var stillSignedIn = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge), session);
        await Assert.That(stillSignedIn.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcHarness.RedirectUri);

        // The confirmed post gets the hosted Signed out page, which continues to the registered URI (with state) by link and meta refresh.
        using var registered = await PostAsync(oidc, $"/{OidcIssuer.Slug}/oauth/logout", fields, session, OidcHarness.PublicOrigin);
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

        // Without a live session there is nothing to confirm. Without a redirect URI the page stays put.
        using var page = await oidc.AuthorizeAsync($"/{OidcIssuer.Slug}/oauth/logout", session);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var plain = await page.Content.ReadAsStringAsync();
        await Assert.That(plain).Contains("You are signed out.");
        await Assert.That(plain).DoesNotContain("http-equiv=\"refresh\"");
        await Assert.That(ClearsSessionCookie(page)).IsTrue();
    }

    [Test]
    public async Task Logout_with_an_id_token_hint_ends_only_its_own_users_session()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, session, tokens) = await oidc.SignInAndRedeemAsync();
        var hint = tokens.GetProperty("id_token").GetString()!;
        var (_, other, _) = await oidc.SignInAndRedeemAsync("bob@example.test");
        var (_, challenge) = OidcHarness.Pkce();
        var logout = $"/{OidcIssuer.Slug}/oauth/logout?id_token_hint={hint}&state=x&post_logout_redirect_uri={Uri.EscapeDataString(OidcHarness.PostLogoutRedirectUri)}";

        // Another user's session is not ended by this hint; the confirmation carries the hint so the client stays identified.
        using var mismatched = await oidc.AuthorizeAsync(logout, other);
        var confirm = await mismatched.Content.ReadAsStringAsync();
        await Assert.That(confirm).Contains("<h1>Sign out?</h1>");
        await Assert.That(confirm).Contains($"<input type=\"hidden\" name=\"id_token_hint\" value=\"{hint}\">");
        await Assert.That(mismatched.Headers.Contains("Set-Cookie")).IsFalse();
        using var otherAlive = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge), other);
        await Assert.That(otherAlive.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcHarness.RedirectUri);

        using var hinted = await oidc.AuthorizeAsync(logout, session);
        await Assert.That(hinted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await hinted.Content.ReadAsStringAsync()).Contains($"href=\"{OidcHarness.PostLogoutRedirectUri}?state=x\"");
        await Assert.That(ClearsSessionCookie(hinted)).IsTrue();
        using var after = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge), session);
        await Assert.That(after.Headers.Location!.OriginalString).StartsWith($"/ui/{OidcIssuer.Slug}/sign-in?next=");
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

        using var logout = await Post($"/{OidcIssuer.Slug}/oauth/logout", new() { ["id_token_hint"] = tokens.GetProperty("id_token").GetString()!, ["state"] = "x", ["post_logout_redirect_uri"] = OidcHarness.PostLogoutRedirectUri }, session);
        await Assert.That(logout.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await logout.Content.ReadAsStringAsync()).Contains($"href=\"{OidcHarness.PostLogoutRedirectUri}?state=x\"");
        await Assert.That(ClearsSessionCookie(logout)).IsTrue();
        using var after = await oidc.AuthorizeAsync(path, session);
        await Assert.That(after.Headers.Location!.OriginalString).StartsWith($"/ui/{OidcIssuer.Slug}/sign-in?next=");

        Task<HttpResponseMessage> Post(string target, Dictionary<string, string> fields, string? cookie) => PostAsync(oidc, target, fields, cookie);
    }

    private static async Task<HttpResponseMessage> PostAsync(OidcHarness oidc, string target, Dictionary<string, string> fields, string? cookie, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new FormUrlEncodedContent(fields) };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"tsauth_session={cookie}");
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await oidc.Client.SendAsync(request);
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
