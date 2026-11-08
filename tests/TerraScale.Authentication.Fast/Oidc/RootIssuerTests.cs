using System.Net;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>
/// The bare origin is no tenant's issuer: its protocol paths answer 404, so a standards client cannot start from
/// a discovery document that advertises an issuer whose keys nothing can fetch.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RootIssuerTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Root_discovery_and_jwks_answer_404_while_the_platform_issuer_serves_both()
    {
        var oidc = await fixture.SharedAsync();

        using var discovery = await oidc.Client.GetAsync("/.well-known/openid-configuration");
        await Assert.That(discovery.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var jwks = await oidc.Client.GetAsync("/.well-known/jwks.json");
        await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var token = await oidc.Client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }));
        await Assert.That(token.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        using var platform = await oidc.Client.GetAsync($"/{OidcIssuer.Slug}/.well-known/openid-configuration");
        await Assert.That(platform.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = await OidcHarness.JsonAsync(platform);
        await Assert.That(metadata.GetProperty("issuer").GetString()).IsEqualTo(OidcHarness.Issuer);
        await Assert.That(metadata.GetProperty("jwks_uri").GetString()).IsEqualTo($"{OidcHarness.Issuer}/.well-known/jwks.json");
        using var platformKeys = await oidc.Client.GetAsync(OidcHarness.Local(metadata.GetProperty("jwks_uri").GetString()!));
        await Assert.That(platformKeys.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await OidcHarness.JsonAsync(platformKeys)).GetProperty("keys").GetArrayLength()).IsGreaterThan(0);
    }
}
