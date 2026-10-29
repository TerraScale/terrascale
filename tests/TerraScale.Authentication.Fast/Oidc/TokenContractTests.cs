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
    private const string ServiceId = "01M4EQ2FRJ8S8B29E8TQVWNW4K";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";

    [Test]
    public async Task User_access_tokens_are_at_jwt_for_the_terrabase_audience_with_verified_email_and_id_tokens_stay_jwt_for_the_client()
    {
        await using var oidc = await StartAsync();
        var user = await oidc.CreateUserAsync("alice@example.test");
        var session = await oidc.SignInAsync("alice@example.test");
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
        await Assert.That(access.GetClaim("email").Value).IsEqualTo("alice@example.test");
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
    public async Task A_service_client_gets_an_at_jwt_whose_subject_is_its_client_id_and_nothing_else()
    {
        await using var oidc = await StartAsync();

        using var response = await ClientCredentialsAsync(oidc, ServiceId, ServiceSecret);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.TryGetProperty("id_token", out _)).IsFalse();
        await Assert.That(body.TryGetProperty("refresh_token", out _)).IsFalse();

        var access = await ValidateAsync(body.GetProperty("access_token").GetString()!, await KeysAsync(oidc), Audience);
        await Assert.That(access.Typ).IsEqualTo("at+jwt");
        await Assert.That(access.Subject).IsEqualTo(ServiceId);
        await Assert.That(access.Audiences.ToArray()).IsEquivalentTo([Audience]);
        await Assert.That(access.TryGetClaim("email", out _)).IsFalse();
        await Assert.That(access.TryGetClaim("email_verified", out _)).IsFalse();

        // offline_access needs the refresh-token permission, which service clients do not have.
        using var offline = await ClientCredentialsAsync(oidc, ServiceId, ServiceSecret, "offline_access");
        await Assert.That(offline.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        using var wrongSecret = await ClientCredentialsAsync(oidc, ServiceId, ServiceSecret + "x");
        await Assert.That(wrongSecret.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ErrorAsync(wrongSecret)).IsEqualTo("invalid_client");

        // The dashboard is a user-facing client: it may not mint tokens for itself.
        using var dashboard = await ClientCredentialsAsync(oidc, OidcIssuer.DashboardClientId, OidcHarness.ClientSecret);
        await Assert.That(dashboard.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorAsync(dashboard)).IsEqualTo("unauthorized_client");

        // And a service client cannot run the browser flow.
        var (_, challenge) = OidcHarness.Pkce();
        using var authorize = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, clientId: ServiceId), session: null);
        await Assert.That(authorize.Headers.Location?.OriginalString ?? string.Empty).DoesNotStartWith($"/ui/{OidcIssuer.Slug}/sign-in");
    }






    private Task<OidcHarness> StartAsync() => OidcHarness.StartAsync(fixture, values =>
    {
        values["access-token-audiences"] = Audience;
        values["service-clients"] = $"{ServiceId}:{ServiceSecret}";
    });

    private static Task<HttpResponseMessage> ClientCredentialsAsync(OidcHarness oidc, string clientId, string secret, string? scope = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        if (scope is not null)
        {
            form["scope"] = scope;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/oauth/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        return oidc.Client.SendAsync(request);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).GetProperty("error").GetString();

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
