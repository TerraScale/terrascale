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


    private static bool ClearsSessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(cookie => cookie.StartsWith("tsauth_session=;", StringComparison.Ordinal) && cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
}
