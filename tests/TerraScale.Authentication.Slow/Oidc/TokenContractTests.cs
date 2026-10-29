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

namespace TerraScale.Authentication.Slow.Oidc;

/// <summary>The token shapes TerraBase's Management API accepts (TerraBase decision 0007, TS-519).</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TokenContractTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://api.terrascale.tech";
    private const string ServiceId = "01M4EQ2FRJ8S8B29E8TQVWNW4K";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";



    [Test]
    [Arguments("access-token-audiences", "http://gru.api.terrascale.tech")]
    [Arguments("access-token-audiences", "https://gru.api.terrascale.tech/?x=1")]
    [Arguments("access-token-audiences", "https://gru.api.terrascale.tech/#f")]
    // TS-598: the platform issuer as an audience would let organization and project tokens open the management API.
    [Arguments("access-token-audiences", "https://AUTH.test.invalid:443/terrascale")]
    [Arguments("service-clients", "01M4EQ2FRJ8S8B29E8TQVWNW4J:service-test-secret-0123456789abcdefghij")]
    [Arguments("service-clients", "00000000000000000000000006:service-test-secret-0123456789abcdefghij")]
    [Arguments("service-clients", "abcdef01-2345-6789-abcd-ef0123456789:service-test-secret-0123456789abcdefghij")]
    [Arguments("service-clients", "Orchestrator:service-test-secret-0123456789abcdefghij")]
    [Arguments("service-clients", "01M4EQ2FRJ8S8B29E8TQVWNW4K:short")]
    [Arguments("service-clients", "01M4EQ2FRJ8S8B29E8TQVWNW4K")]
    [Arguments("dashboard-redirect-uris", ";")]
    [Arguments("service-clients", "01M4EQ2FRJ8S8B29E8TQVWNW4K:service-test-secret-0123456789abcdefghij;01M4EQ2FRJ8S8B29E8TQVWNW4K:service-test-secret-0123456789abcdefghij")]
    public async Task Invalid_audiences_clients_and_redirect_lists_stop_startup_without_echoing_values(string key, string value)
    {
        var settings = AuthTestServer.DefaultSettings();
        settings[key] = value;
        if (key == "dashboard-redirect-uris")
        {
            settings["dashboard-client-secret"] = OidcHarness.ClientSecret;
        }

        var error = Assert.Throws<AuthConfigurationException>(() => AuthOptions.Load(settings));
        await Assert.That(error.Message).Contains($"TS_AUTH_{key.Replace('-', '_').ToUpperInvariant()}");
        await Assert.That(error.Message).DoesNotContain("service-test-secret");
        await Assert.That(error.Message).DoesNotContain("gru.api");
    }

    [Test]
    public async Task An_audience_on_this_origin_other_than_its_platform_issuer_is_allowed()
    {
        var settings = AuthTestServer.DefaultSettings();
        settings["access-token-audiences"] = "https://auth.test.invalid/api";
        // TerraBase's audience is always first; configured ones follow.
        await Assert.That(AuthOptions.Load(settings).AccessTokenAudiences).IsEquivalentTo([AuthOptions.TerraBaseAudience, "https://auth.test.invalid/api"]);
    }

    [Test]
    public async Task Audiences_are_kept_exactly_as_configured()
    {
        var settings = AuthTestServer.DefaultSettings();
        settings["access-token-audiences"] = $" {Audience} ;https://eu.api.terrascale.tech/";
        settings["service-clients"] = $"{ServiceId}:{ServiceSecret}";

        var options = AuthOptions.Load(settings);
        await Assert.That(options.AccessTokenAudiences.ToArray()).IsEquivalentTo([Audience, "https://eu.api.terrascale.tech/"]);
        await Assert.That(options.ServiceClients.Single().ClientId).IsEqualTo(ServiceId);
        await Assert.That(options.ToString()).DoesNotContain(ServiceSecret);
    }

    [Test]
    public async Task Production_defaults_issue_the_one_issuer_and_audience_terrabase_trusts_in_every_region()
    {
        // No public-origin and no access-token-audiences: what every region of ts-auth runs with.
        await using var oidc = await OidcHarness.StartAsync(fixture, values =>
        {
            values.Remove("public-origin");
            values.Remove("access-token-audiences");
            values["service-clients"] = $"{ServiceId}:{ServiceSecret}";
        });

        using var response = await ClientCredentialsAsync(oidc, ServiceId, ServiceSecret);
        var token = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).GetProperty("access_token").GetString()!;
        // The pair production TerraBase pins (TerraBaseOptions.PlatformAuthIssuer, PlatformAuthAudience); no region in either.
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "https://auth.terrascale.tech/terrascale",
            ValidAudience = "https://api.terrascale.tech",
            IssuerSigningKeys = (await KeysAsync(oidc)).GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(((JsonWebToken)result.SecurityToken).Audiences.ToArray()).IsEquivalentTo([AuthOptions.TerraBaseAudience]);
    }

    [Test]
    public async Task Refresh_at_the_lineage_budget_requires_signin_without_consuming_or_extending_the_family()
    {
        await using var oidc = await StartAsync();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var user = await oidc.CreateUserAsync();
        var session = await sessions.CreateAsync(user);
        for (var rotation = 1; rotation < AuthStore.MaxCredentialBindings - 1; rotation++)
        {
            session = (await sessions.RotateAsync(user.InstanceId, session.Token))!;
        }

        await Assert.That(session.Session.Bindings.Count).IsEqualTo(AuthStore.MaxCredentialBindings - 1);
        var (verifier, challenge) = OidcHarness.Pkce();
        using var issued = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session.Token, challenge), verifier);
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(issued);
        var before = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection, limit: 1000)).Items;
        await Assert.That(before.Any(item => item.Document.GetProperty("bindings").GetArrayLength() == AuthStore.MaxCredentialBindings)).IsTrue();
        await Assert.That(before.All(item => item.Document.GetProperty("bindings").GetArrayLength() <= AuthStore.MaxCredentialBindings)).IsTrue();
        using var rejected = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await OidcHarness.JsonAsync(rejected);
        await Assert.That(error.GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        await Assert.That(error.GetProperty("error_description").GetString()).Contains("rotation limit");
        var after = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection, limit: 1000)).Items;
        await Assert.That(after.Select(item => item.Version).Order(StringComparer.Ordinal)
            .SequenceEqual(before.Select(item => item.Version).Order(StringComparer.Ordinal))).IsTrue();
        using var live = await oidc.UserInfoAsync(tokens.GetProperty("access_token").GetString()!);
        await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var fresh = await oidc.SignInAsync(user.Email);
        var freshPkce = OidcHarness.Pkce();
        using var restarted = await oidc.RedeemCodeAsync(await oidc.CodeAsync(fresh, freshPkce.Challenge), freshPkce.Verifier);
        await Assert.That(restarted.StatusCode).IsEqualTo(HttpStatusCode.OK);
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
