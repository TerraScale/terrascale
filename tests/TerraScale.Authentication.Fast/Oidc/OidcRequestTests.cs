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
}
