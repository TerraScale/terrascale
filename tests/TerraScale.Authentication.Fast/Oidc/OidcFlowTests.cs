using System.Net;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OidcFlowTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_browser_signs_in_with_a_passcode_and_the_client_redeems_a_pkce_code_for_verifiable_tokens()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var user = await oidc.CreateUserAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        var authorize = OidcHarness.AuthorizePath(challenge);

        // No session: the browser is sent to the hosted sign-in page and told where to resume.
        using var anonymous = await oidc.AuthorizeAsync(authorize, session: null);
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var signIn = anonymous.Headers.Location!;
        await Assert.That(signIn.OriginalString).StartsWith($"/ui/{OidcIssuer.Slug}/sign-in?next=");
        var next = OidcHarness.Query(new Uri(new Uri(OidcHarness.PublicOrigin), signIn))["next"]!;
        await Assert.That(next).IsEqualTo(authorize);

        using var page = await oidc.Client.GetAsync(signIn);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page.Headers.GetValues("Content-Security-Policy").Single()).Contains("frame-ancestors 'none'");

        var session = await oidc.SignInAsync("Alice@Example.test", next);
        using var resumed = await oidc.AuthorizeAsync(next, session);
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var callback = resumed.Headers.Location!;
        await Assert.That(callback.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcHarness.RedirectUri);
        var parameters = OidcHarness.Query(callback);
        await Assert.That(parameters["state"]).IsEqualTo("state-1");
        await Assert.That(parameters["iss"]).IsEqualTo(OidcHarness.Issuer);

        using var token = await oidc.RedeemCodeAsync(parameters["code"]!, verifier);
        var body = JsonSerializer.Deserialize<JsonElement>(await token.Content.ReadAsStringAsync());
        await Assert.That(token.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("token_type").GetString()).IsEqualTo("Bearer");
        await Assert.That(body.TryGetProperty("refresh_token", out _)).IsTrue();

        // An independent client: discovery and JWKS fetched over HTTP, tokens checked with IdentityModel, not OpenIddict.
        var discovery = JsonSerializer.Deserialize<JsonElement>(await oidc.Client.GetStringAsync($"/{OidcIssuer.Slug}/.well-known/openid-configuration"));
        await Assert.That(discovery.GetProperty("issuer").GetString()).IsEqualTo(OidcHarness.Issuer);
        await Assert.That(discovery.GetProperty("token_endpoint").GetString()).IsEqualTo($"{OidcHarness.Issuer}/oauth/token");
        await Assert.That(discovery.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(item => item.GetString()!).ToArray()).IsEquivalentTo(["S256"]);
        await Assert.That(discovery.GetProperty("id_token_signing_alg_values_supported").EnumerateArray().Select(item => item.GetString()!).ToArray()).IsEquivalentTo(["ES256"]);
        var keys = new JsonWebKeySet(await oidc.Client.GetStringAsync(OidcHarness.Local(discovery.GetProperty("jwks_uri").GetString()!)));
        await Assert.That(keys.Keys.All(key => key.Kty == "EC" && key.Crv == "P-256" && !key.HasPrivateKey)).IsTrue();

        var handler = new JsonWebTokenHandler();
        var identity = await handler.ValidateTokenAsync(body.GetProperty("id_token").GetString(), Parameters(keys, OidcIssuer.DashboardClientId));
        await Assert.That(identity.IsValid).IsTrue();
        await Assert.That(identity.Claims["sub"]).IsEqualTo(user.Id);
        await Assert.That(identity.Claims["email"]).IsEqualTo("alice@example.test");
        await Assert.That(identity.Claims["nonce"]).IsEqualTo("nonce-1");
        await Assert.That(identity.Claims.ContainsKey(OidcIssuer.EpochClaim)).IsFalse();
        var access = await handler.ValidateTokenAsync(body.GetProperty("access_token").GetString(), Parameters(keys, audience: null));
        await Assert.That(access.IsValid).IsTrue();
        await Assert.That(((JsonWebToken)access.SecurityToken).Typ).IsEqualTo("at+jwt");

        using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, OidcHarness.Local(discovery.GetProperty("userinfo_endpoint").GetString()!));
        userInfoRequest.Headers.Authorization = new("Bearer", body.GetProperty("access_token").GetString());
        using var userInfo = await oidc.Client.SendAsync(userInfoRequest);
        var claims = JsonSerializer.Deserialize<JsonElement>(await userInfo.Content.ReadAsStringAsync());
        await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(userInfo.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(userInfo.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
        await Assert.That(claims.GetProperty("sub").GetString()).IsEqualTo(user.Id);
        await Assert.That(claims.GetProperty("email_verified").GetBoolean()).IsTrue();

        // An identity token is not an access token.
        using var withIdToken = new HttpRequestMessage(HttpMethod.Get, $"/{OidcIssuer.Slug}/oauth/userinfo");
        withIdToken.Headers.Authorization = new("Bearer", body.GetProperty("id_token").GetString());
        using var rejected = await oidc.Client.SendAsync(withIdToken);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(rejected.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(rejected.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();

        using var missingToken = await oidc.Client.GetAsync(new Uri($"/{OidcIssuer.Slug}/oauth/userinfo", UriKind.Relative));
        await Assert.That(missingToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(missingToken.Headers.CacheControl?.NoStore).IsTrue();
    }

    private static TokenValidationParameters Parameters(JsonWebKeySet keys, string? audience) => new()
    {
        ValidIssuer = OidcHarness.Issuer,
        ValidAudience = audience,
        ValidateAudience = audience is not null,
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        IssuerSigningKeys = keys.GetSigningKeys(),
        ClockSkew = TimeSpan.Zero,
    };
}
