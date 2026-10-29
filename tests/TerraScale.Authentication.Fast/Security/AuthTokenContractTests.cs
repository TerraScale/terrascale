using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Security;

/// <summary>
/// The token contract TerraBase relies on in every region: ts-auth replicas (across regions, all on one TerraBase
/// auth database) bootstrap the platform issuer's key at startup and share it, so a token
/// any replica issues carries the one global issuer and validates against every replica's JWKS. Synthetic master key
/// and service client; an isolated database per test.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuthTokenContractTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://api.terrascale.test";
    private const string ServiceId = "01M4EQ2FRJ8S8B29E8TQVWNW4K";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(20);
    private static readonly string[] Regions = ["gru", "sjc", "fra"];

    [Test]
    public async Task Startup_on_every_replica_provisions_shared_keys_and_tokens_validate_against_every_replicas_keys()
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

    [Test]
    public async Task Startup_provisioning_recovers_immediately_from_a_read_before_collections_exist()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync(provision: false);
        var clock = new SteppingClock(DateTimeOffset.UtcNow);
        await using var server = await AuthTestServer.StartAsync(
            settings: values => values["service-clients"] = $"{ServiceId}:{ServiceSecret}",
            services: services =>
            {
                fixture.UseStorage(services, tenant);
                services.AddSingleton<TimeProvider>(clock);
            },
            beforeStart: async services =>
            {
                // Reproduce the first readiness read arriving before startup has made the collections.
                var ring = services.GetRequiredService<KeyRing>();
                var unavailable = await Assert.ThrowsAsync<ApiException>(() => ring.CheckPublicMetadataAsync(OidcIssuer.InstanceId, CancellationToken.None));
                await Assert.That(unavailable!.Status).IsEqualTo(503);
                await SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None);
            });
        await server.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped.WaitAsync(Recovery);

        // Monotonic time has not advanced: success must clear the earlier failed-read backoff.
        using var response = await ClientCredentialsAsync(server);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        await Assert.That(token).IsNotNullOrEmpty();
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
