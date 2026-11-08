using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Store;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>The token shapes TerraBase's Management API accepts (TerraBase decision 0007, TS-519).</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TokenContractTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://api.terrascale.tech";

    [Test]
    public async Task User_access_tokens_are_at_jwt_for_the_terrabase_audience_with_verified_email_and_id_tokens_stay_jwt_for_the_client()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var user = await oidc.CreateUserAsync(oidc.UniqueEmail("alice"));
        var session = (await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user)).Token;
        var (verifier, challenge) = OidcHarness.Pkce();
        // Without the email scope the identity token has no email, but the access token still does.
        using var authorize = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, scope: "openid offline_access"), session);
        using var response = await oidc.RedeemCodeAsync(OidcHarness.Query(authorize.Headers.Location!)["code"]!, verifier);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var keys = await KeysAsync(oidc);

        var access = await ValidateAsync(body.GetProperty("access_token").GetString()!, keys, Audience);
        await Assert.That(access.Typ).IsEqualTo("at+jwt");
        await Assert.That(access.Alg).IsEqualTo("ES256");
        // The issuer too, so the platform management API (TS-598) accepts a sign-in token.
        await Assert.That(access.Audiences.ToArray()).IsEquivalentTo([OidcHarness.Issuer, Audience]);
        await Assert.That(access.Subject).IsEqualTo(user.Id);
        await Assert.That(access.GetClaim("email").Value).IsEqualTo(user.Email);
        await Assert.That(access.GetClaim("email_verified").Value).IsEqualTo("true");
        // The epoch lets the management API (TS-598) refuse a token from before a sign-out everywhere.
        await Assert.That(access.GetClaim(OidcIssuer.EpochClaim).Value).IsEqualTo(user.AuthEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var identity = await ValidateAsync(body.GetProperty("id_token").GetString()!, keys, OidcIssuer.DashboardClientId);
        await Assert.That(identity.Typ).IsEqualTo("JWT");
        await Assert.That(identity.Audiences.ToArray()).IsEquivalentTo([OidcIssuer.DashboardClientId]);
        await Assert.That(identity.TryGetClaim("email", out _)).IsFalse();

        // Rotated tokens keep the same shape.
        using var refreshed = await oidc.RefreshAsync(body.GetProperty("refresh_token").GetString()!);
        var rotated = JsonSerializer.Deserialize<JsonElement>(await refreshed.Content.ReadAsStringAsync());
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var second = await ValidateAsync(rotated.GetProperty("access_token").GetString()!, keys, Audience);
        await Assert.That(second.Typ).IsEqualTo("at+jwt");
        await Assert.That(second.GetClaim("email_verified").Value).IsEqualTo("true");
    }

    [Test]
    public async Task Refresh_scope_handling_keeps_the_original_authorization_and_refuses_expansion_without_consuming_the_token()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var user = await oidc.CreateUserAsync(oidc.UniqueEmail("scope"));
        var session = (await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user)).Token;
        var (verifier, challenge) = OidcHarness.Pkce();
        using var authorize = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, scope: "openid email profile offline_access"), session);
        using var redeemed = await oidc.RedeemCodeAsync(OidcHarness.Query(authorize.Headers.Location!)["code"]!, verifier);
        var original = JsonSerializer.Deserialize<JsonElement>(await redeemed.Content.ReadAsStringAsync());
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var keys = await KeysAsync(oidc);
        var refresh = original.GetProperty("refresh_token").GetString()!;

        using var omitted = await oidc.RefreshAsync(refresh);
        var full = JsonSerializer.Deserialize<JsonElement>(await omitted.Content.ReadAsStringAsync());
        await Assert.That(omitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(full.GetProperty("scope").GetString()).IsEqualTo("openid email profile offline_access");
        await Assert.That(ScopesAsync(await ValidateAsync(full.GetProperty("access_token").GetString()!, keys, Audience)))
            .IsEquivalentTo(["openid", "email", "profile", "offline_access"]);
        refresh = full.GetProperty("refresh_token").GetString()!;

        using var reduced = await oidc.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["scope"] = "openid email" });
        var subset = JsonSerializer.Deserialize<JsonElement>(await reduced.Content.ReadAsStringAsync());
        await Assert.That(reduced.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(subset.GetProperty("scope").GetString()).IsEqualTo("openid email");
        await Assert.That(ScopesAsync(await ValidateAsync(subset.GetProperty("access_token").GetString()!, keys, Audience)))
            .IsEquivalentTo(["openid", "email"]);
        refresh = subset.GetProperty("refresh_token").GetString()!;

        using var restored = await oidc.RefreshAsync(refresh);
        var whole = JsonSerializer.Deserialize<JsonElement>(await restored.Content.ReadAsStringAsync());
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(whole.GetProperty("scope").GetString()).IsEqualTo("openid email profile offline_access");
        await Assert.That(ScopesAsync(await ValidateAsync(whole.GetProperty("access_token").GetString()!, keys, Audience)))
            .IsEquivalentTo(["openid", "email", "profile", "offline_access"]);
        refresh = whole.GetProperty("refresh_token").GetString()!;

        foreach (var refused in new[] { "openid email admin", "openid bogus" })
        {
            using var expansion = await oidc.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["scope"] = refused });
            var error = JsonSerializer.Deserialize<JsonElement>(await expansion.Content.ReadAsStringAsync());
            await Assert.That(expansion.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(error.GetProperty("error").GetString()).IsEqualTo("invalid_scope");
        }

        using var after = await oidc.RefreshAsync(refresh);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static string[] ScopesAsync(JsonWebToken token) =>
        token.GetClaim("scope").Value.Split(' ');

    private static async Task<JsonWebKeySet> KeysAsync(OidcHarness oidc) =>
        new(await oidc.Client.GetStringAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json"));

    // Validated as TerraBase does: ES256 only, exact issuer and audience, published keys.
    private static async Task<JsonWebToken> ValidateAsync(string token, JsonWebKeySet keys, string audience)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = OidcHarness.Issuer,
            ValidAudience = audience,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKeys = keys.GetSigningKeys(),
            ClockSkew = TimeSpan.Zero,
        });
        return result.IsValid ? (JsonWebToken)result.SecurityToken : throw new InvalidOperationException($"Token rejected: {result.Exception?.GetType().Name}");
    }
}
