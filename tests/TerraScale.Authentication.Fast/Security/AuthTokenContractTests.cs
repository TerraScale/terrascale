using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Security;

/// <summary>
/// The token contract TerraBase relies on in every region: ts-auth replicas (one per Fly region, all on one TerraBase
/// auth database) fail closed until the operator provisions the platform issuer's key (#96), then share it, so a token
/// any replica issues carries the one global issuer and validates against every replica's JWKS. Synthetic master key
/// and service client; an isolated database per test.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuthTokenContractTests(TerraBaseFixture fixture)
{
    private const string NoKeys = "no signing keys: operator provisioning required (docs/auth/signing-keys.md)";
    private const string Audience = "https://api.terrascale.test";
    private const string ServiceId = "01M4EQ2FRJ8S8B29E8TQVWNW4K";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(20);
    private static readonly string[] Regions = ["gru", "sjc", "fra"];

    [Test]
    public async Task Every_replica_fails_closed_until_provisioned_then_tokens_from_any_replica_validate_against_every_replicas_keys()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var replicas = new List<AuthTestServer>();
        try
        {
            foreach (var _ in Regions)
            {
                replicas.Add(await AuthTestServer.StartAsync(
                    settings: values =>
                    {
                        values["master-key"] = masterKey;
                        values["access-token-audiences"] = Audience;
                        values["service-clients"] = $"{ServiceId}:{ServiceSecret}";
                    },
                    services: services => target.Register(services)));
            }

            // Fail closed everywhere: not ready with the operator error, no JWKS, no token.
            foreach (var replica in replicas)
            {
                await Assert.That(await ReadinessWait.UntilAsync(async () => await ReadinessErrorAsync(replica) == NoKeys, Recovery)).IsTrue();
                using var jwks = await replica.Client.GetAsync(new Uri($"/{OidcIssuer.Slug}/.well-known/jwks.json", UriKind.Relative));
                await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                using var refused = await ClientCredentialsAsync(replica);
                await Assert.That(refused.IsSuccessStatusCode).IsFalse();
                await Assert.That(await refused.Content.ReadAsStringAsync()).DoesNotContain("access_token");
            }

            // One operator run against the shared database provisions every replica.
            await Assert.That(await SigningKeyProvisioning.RunAsync(replicas[0].App.Services, CancellationToken.None)).IsEqualTo(0);
            foreach (var replica in replicas)
            {
                await Assert.That(await ReadinessWait.ReadyWithinAsync(replica.Client, Recovery)).IsTrue();
            }

            var discovered = new List<(string Issuer, string Jwks, JsonWebKeySet Keys)>();
            foreach (var replica in replicas)
            {
                var discovery = await replica.Client.GetFromJsonAsync<JsonElement>(new Uri($"/{OidcIssuer.Slug}/.well-known/openid-configuration", UriKind.Relative));
                var keys = new JsonWebKeySet(await replica.Client.GetStringAsync(new Uri($"/{OidcIssuer.Slug}/.well-known/jwks.json", UriKind.Relative)));
                discovered.Add((discovery.GetProperty("issuer").GetString()!, discovery.GetProperty("jwks_uri").GetString()!, keys));
            }

            // TerraBase in every region trusts one auth-issuer and fetches one jwks_uri: no replica may name its own.
            await Assert.That(discovered.Select(item => item.Issuer).Distinct().ToArray()).IsEquivalentTo([OidcHarness.Issuer]);
            await Assert.That(discovered.Select(item => item.Jwks).Distinct().Count()).IsEqualTo(1);
            var kids = discovered.Select(item => string.Join(',', item.Keys.Keys.Select(key => key.Kid).Order(StringComparer.Ordinal))).Distinct().ToArray();
            await Assert.That(kids.Length).IsEqualTo(1);

            for (var issuing = 0; issuing < replicas.Count; issuing++)
            {
                using var response = await ClientCredentialsAsync(replicas[issuing]);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
                foreach (var (_, _, keys) in discovered)
                {
                    var jwt = await ValidateAsync(token, keys);
                    await Assert.That(jwt.Typ).IsEqualTo("at+jwt");
                    await Assert.That(jwt.Alg).IsEqualTo(SecurityAlgorithms.EcdsaSha256);
                    await Assert.That(keys.Keys.Select(key => key.Kid)).Contains(jwt.Kid);
                    await Assert.That(jwt.Subject).IsEqualTo(ServiceId);
                }
            }
        }
        finally
        {
            foreach (var replica in replicas)
            {
                await replica.DisposeAsync();
            }
        }
    }

    private static async Task<string?> ReadinessErrorAsync(AuthTestServer server)
    {
        using var response = await server.Client.GetAsync(new Uri("/readyz", UriKind.Relative));
        return response.StatusCode == HttpStatusCode.ServiceUnavailable
            && (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private static async Task<HttpResponseMessage> ClientCredentialsAsync(AuthTestServer server)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/{OidcIssuer.Slug}/oauth/token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ServiceId}:{ServiceSecret}")));
        return await server.Client.SendAsync(request);
    }

    // Validated as TerraBase does (UserAuthenticationHandler): asymmetric only, exact issuer and audience, kid lookup, exp required.
    private static async Task<JsonWebToken> ValidateAsync(string token, JsonWebKeySet keys)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = OidcHarness.Issuer,
            ValidAudience = Audience,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidTypes = ["at+jwt", "application/at+jwt"],
            IssuerSigningKeys = keys.GetSigningKeys(),
            TryAllIssuerSigningKeys = false,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        return result.IsValid ? (JsonWebToken)result.SecurityToken : throw new InvalidOperationException($"Token rejected: {result.Exception?.GetType().Name}");
    }
}
