using System.Net;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>RFC 6749 section 6: access scopes may narrow; replacement refresh authority stays unchanged.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RefreshScopeContractTests(TerraBaseFixture fixture)
{
    private const string Original = "openid email offline_access";

    [Test]
    [Arguments(null)]
    [Arguments(Original)]
    [Arguments("openid")]
    public async Task Refresh_scope_bounds_access_and_preserves_successor_authority(string? requested)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, _, initial) = await oidc.SignInAndRedeemAsync();
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = initial.GetProperty("refresh_token").GetString()!,
        };
        if (requested is not null)
        {
            fields["scope"] = requested;
        }

        using var response = await oidc.TokenAsync(fields);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await OidcHarness.JsonAsync(response);
        var expected = (requested ?? Original).Split(' ');
        // RFC 6749 section 5.1 permits scope omission when it equals the requested scope.
        var reported = body.TryGetProperty("scope", out var scope) ? scope.GetString() : requested;
        await Assert.That(reported!.Split(' ')).IsEquivalentTo(expected);
        var access = await ValidateAccessAsync(oidc, body);
        await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(expected);
        using var info = await oidc.UserInfoAsync(body.GetProperty("access_token").GetString()!);
        await Assert.That(info.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await OidcHarness.JsonAsync(info)).TryGetProperty("email", out _)).IsEqualTo(expected.Contains("email", StringComparer.Ordinal));

        // The subset restricted this access token, not the original consent or replacement refresh token.
        using var successor = await oidc.RefreshAsync(body.GetProperty("refresh_token").GetString()!);
        await Assert.That(successor.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var restored = await OidcHarness.JsonAsync(successor);
        await Assert.That(restored.GetProperty("scope").GetString()!.Split(' ')).IsEquivalentTo(Original.Split(' '));
        await Assert.That((await ValidateAccessAsync(oidc, restored)).GetClaim("scope").Value.Split(' ')).IsEquivalentTo(Original.Split(' '));
        using var restoredInfo = await oidc.UserInfoAsync(restored.GetProperty("access_token").GetString()!);
        await Assert.That(restoredInfo.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await OidcHarness.JsonAsync(restoredInfo)).GetProperty("email").GetString()).IsEqualTo("alice@example.test");
    }

    [Test]
    [Arguments("openid email offline_access profile")]
    [Arguments("openid email offline_access unknown_scope")]
    public async Task Expanded_or_unknown_scopes_fail_without_consuming_refresh_authority(string requested)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, _, initial) = await oidc.SignInAndRedeemAsync();
        var before = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection, limit: 100)).Items;
        using var rejected = await oidc.TokenAsync(new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = initial.GetProperty("refresh_token").GetString()!, ["scope"] = requested,
        });
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(rejected)).IsEqualTo(requested.Contains("unknown_scope", StringComparison.Ordinal) ? "invalid_request" : "invalid_grant");
        var after = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection, limit: 100)).Items;
        await Assert.That(after.Select(item => item.Version)).IsEquivalentTo(before.Select(item => item.Version));
        using var allowed = await oidc.RefreshAsync(initial.GetProperty("refresh_token").GetString()!);
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task<JsonWebToken> ValidateAccessAsync(OidcHarness oidc, JsonElement body)
    {
        var keys = new JsonWebKeySet(await oidc.Client.GetStringAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json"));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(body.GetProperty("access_token").GetString()!, new TokenValidationParameters
        {
            ValidIssuer = OidcHarness.Issuer, ValidAudience = OidcHarness.Issuer, ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKeys = keys.GetSigningKeys(), ClockSkew = TimeSpan.Zero,
        });
        await Assert.That(result.IsValid).IsTrue();
        return (JsonWebToken)result.SecurityToken;
    }
}
