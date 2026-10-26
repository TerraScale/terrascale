using System.Net;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>Discovery claims match signed standard token claims and the scope-dependent HTTP UserInfo response.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DiscoveryClaimsTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Published_standard_claims_are_emitted_without_publishing_private_claims(bool email)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        using var authorization = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge,
            scope: email ? "openid email offline_access" : "openid offline_access", nonce: "discovery-test-nonce"), session);
        using var issued = await oidc.RedeemCodeAsync(OidcHarness.Query(authorization.Headers.Location!)["code"]!, verifier);
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(issued);
        using var discoveryResponse = await oidc.Client.GetAsync($"/{OidcIssuer.Slug}/.well-known/openid-configuration");
        var discovery = await OidcHarness.JsonAsync(discoveryResponse);
        var published = discovery.GetProperty("claims_supported").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var keys = new JsonWebKeySet(await oidc.Client.GetStringAsync(OidcHarness.Local(discovery.GetProperty("jwks_uri").GetString()!)));
        var access = await ValidateAsync("access_token", OidcHarness.Issuer);
        var identity = await ValidateAsync("id_token", OidcIssuer.DashboardClientId);
        using var infoResponse = await oidc.UserInfoAsync(tokens.GetProperty("access_token").GetString()!);
        await Assert.That(infoResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var info = await OidcHarness.JsonAsync(infoResponse);
        await Assert.That(info.TryGetProperty("email", out _)).IsEqualTo(email);
        await Assert.That(info.TryGetProperty("email_verified", out _)).IsEqualTo(email);
        await Assert.That(identity.TryGetClaim("email", out _)).IsEqualTo(email);
        await Assert.That(identity.TryGetClaim("email_verified", out _)).IsEqualTo(email);
        await Assert.That(identity.GetClaim("nonce").Value).IsEqualTo("discovery-test-nonce");
        await Assert.That(info.EnumerateObject().Select(property => property.Name).ToArray())
            .IsEquivalentTo(email ? ["sub", "email", "email_verified"] : ["sub"]);

        // These are the standard claims emitted by the supported code flow, not internal authorization state.
        string[] standard = ["iss", "sub", "aud", "exp", "iat", "auth_time", "email", "email_verified", "azp", "nonce", "at_hash", "jti", "client_id", "scope"];
        string[] privateClaims = [OidcIssuer.EpochClaim, OidcIssuer.StateClaim, OidcIssuer.MfaClaim, OidcResults.PlatformRoleClaim];
        var allObserved = access.Claims.Concat(identity.Claims).Select(claim => claim.Type)
            .Concat(info.EnumerateObject().Select(property => property.Name)).Distinct(StringComparer.Ordinal).ToArray();
        await Assert.That(allObserved.Contains("ts_auth_credential_bindings", StringComparer.Ordinal)).IsFalse();
        // Exclude only known application claims and OpenIddict's private namespace. An unexpected public claim
        // must fail the comparison instead of disappearing through the expected standard-claim whitelist.
        var observed = allObserved.Where(claim => !privateClaims.Contains(claim, StringComparer.Ordinal)
            && !claim.StartsWith(OpenIddict.Abstractions.OpenIddictConstants.Claims.Prefixes.Private, StringComparison.Ordinal)).ToArray();
        await Assert.That(published).IsEquivalentTo(standard);
        await Assert.That(observed.Except(published, StringComparer.Ordinal).Any()).IsFalse();
        // The email-enabled case witnesses the whole supported union; the other case checks scope suppression above.
        if (email)
        {
            await Assert.That(published).IsEquivalentTo(observed);
        }

        async Task<JsonWebToken> ValidateAsync(string field, string audience)
        {
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(tokens.GetProperty(field).GetString()!, new TokenValidationParameters
            {
                ValidIssuer = OidcHarness.Issuer,
                ValidAudience = audience,
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                IssuerSigningKeys = keys.GetSigningKeys(),
                ClockSkew = TimeSpan.Zero,
            });
            await Assert.That(result.IsValid).IsTrue();
            return (JsonWebToken)result.SecurityToken;
        }
    }
}
