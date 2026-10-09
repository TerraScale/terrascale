using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Security;
using TerraScale.Auth.Tests;
using static TerraScale.Auth.Tests.PlatformCalls;

namespace TerraScale.Auth.Fast.Oidc;

/// <summary>Primary authentication time survives user issuance and refresh; exchange and machine tokens omit it.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuthenticationTimeTests(TerraBaseFixture fixture)
{
    private const string Email = "alice@example.test";
    private const string Password = "correct horse battery";
    private const string Ip = "203.0.113.7";
    private const string Audience = "https://gru.api.terrascale.tech";
    private const string ServiceId = "ts-orchestrator";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";
    private const string SignIn = $"/ui/{OidcIssuer.Slug}/sign-in";
    private static readonly string[] ReadScopes = ["documents:read"];

    [Test]
    [Arguments("password")]
    [Arguments("email-code")]
    [Arguments("hosted")]
    public async Task User_tokens_carry_numeric_primary_authentication_time_and_ignore_submitted_times_on_sign_in_and_refresh(string method)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(-2) };
        await using var oidc = await StartAsync(clock);
        if (method == "password")
        {
            await Assert.That(await oidc.Server.App.Services.GetRequiredService<Users>()
                .CreateAsync(OidcIssuer.InstanceId, Email, Passwords.Hash(Password), CancellationToken.None)).IsNotNull();
        }
        else
        {
            await oidc.CreateUserAsync(Email);
        }

        JsonElement tokens;
        long authenticatedAt;
        if (method == "hosted")
        {
            using (var start = await oidc.PostFormAsync(SignIn, new() { ["email"] = Email, ["auth_time"] = "0" }))
            {
                await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            var passcode = await oidc.Email.NextCodeAsync(Email);
            clock.UtcNow = clock.UtcNow.AddSeconds(10);
            authenticatedAt = clock.UtcNow.ToUnixTimeSeconds();
            using var verify = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = Email, ["code"] = passcode, ["auth_time"] = "0" });
            await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var session = OidcHarness.SessionCookie(verify);
            await Assert.That(session).IsNotNull();

            // Authorization and code redemption happen after the original browser authentication.
            clock.UtcNow = clock.UtcNow.AddSeconds(10);
            var (verifier, challenge) = OidcHarness.Pkce();
            using var authorize = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, extra: "&auth_time=0"), session);
            await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(authorize.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcHarness.RedirectUri);
            var code = OidcHarness.Query(authorize.Headers.Location!)["code"]!;
            clock.UtcNow = clock.UtcNow.AddSeconds(10);
            using var redeemed = await oidc.TokenAsync(new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = OidcHarness.RedirectUri,
                ["code_verifier"] = verifier,
                ["auth_time"] = "0",
            });
            await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            tokens = await OidcHarness.JsonAsync(redeemed);
        }
        else
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = method == "password" ? HeadlessGrants.Password : HeadlessGrants.EmailCode,
                ["email"] = Email,
                ["ip_address"] = Ip,
                ["scope"] = "openid email profile offline_access",
                ["auth_time"] = "0",
            };
            if (method == "password")
            {
                form["password"] = Password;
            }
            else
            {
                using var start = await oidc.HeadlessAsync("email-code/start", new { email = Email, ip_address = Ip, auth_time = 0 });
                await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
                form["code"] = await oidc.Email.NextCodeAsync(Email);
                clock.UtcNow = clock.UtcNow.AddSeconds(10);
            }

            authenticatedAt = clock.UtcNow.ToUnixTimeSeconds();
            using var signedIn = await oidc.TokenAsync(form);
            await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
            tokens = await OidcHarness.JsonAsync(signedIn);
        }

        await AssertUserAuthenticationTimeAsync(oidc, tokens, authenticatedAt);
        clock.UtcNow = clock.UtcNow.AddSeconds(10);
        using var refresh = await oidc.TokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["auth_time"] = clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        });
        await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var refreshed = await OidcHarness.JsonAsync(refresh);
        await Assert.That(refreshed.GetProperty("refresh_token").GetString()).IsNotEqualTo(tokens.GetProperty("refresh_token").GetString());
        await AssertUserAuthenticationTimeAsync(oidc, refreshed, authenticatedAt);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Organization_and_project_exchange_on_a_replica_one_second_behind_accepts_both_issuer_shapes_without_authentication_time(bool projectTarget, bool precedingIssuer)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(-2) };
        await using var oidc = await StartAsync(clock);
        var authenticatedAt = clock.UtcNow.ToUnixTimeSeconds();
        var (user, _, tokens) = await oidc.SignInAndRedeemAsync(Email);
        await AssertUserAuthenticationTimeAsync(oidc, tokens, authenticatedAt);
        var access = tokens.GetProperty("access_token").GetString()!;
        var organization = await oidc.CreateOrganizationAsync(access);
        var project = projectTarget ? await oidc.CreateProjectAsync(access, organization, "Production") : null;

        if (precedingIssuer)
        {
            // Retain the real token/authorization ids and store entries; remove only auth_time to match the preceding issuer.
            var payload = JsonNode.Parse(Base64UrlEncoder.Decode(access.Split('.')[1]))!.AsObject();
            await Assert.That(payload.Remove("auth_time")).IsTrue();
            access = await SignPayloadAsync(oidc, payload);
            await Assert.That((await oidc.ValidateAsync(access, OidcHarness.Issuer)).Subject).IsEqualTo(user.Id);
        }

        var replicaClock = new StoreTestClock { UtcNow = clock.UtcNow.AddSeconds(-1) };
        await using var replica = await oidc.StartReplicaAsync(values => values["access-token-audiences"] = Audience, clock: replicaClock);
        var target = new Dictionary<string, string> { ["organization"] = organization };
        if (project is not null)
        {
            target["project"] = project;
        }

        foreach (var submitTime in new[] { false, true })
        {
            if (submitTime)
            {
                target["auth_time"] = "0";
            }

            using var exchange = await oidc.ExchangeAsync(access, target, replica.Client);
            await Assert.That(exchange.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var exchanged = await OidcHarness.JsonAsync(exchange);
            var exchangedToken = exchanged.GetProperty("access_token").GetString()!;
            var validated = await oidc.ValidateAsync(exchangedToken, Audience);
            await Assert.That(validated.Subject).IsEqualTo(user.Id);
            await Assert.That(validated.GetClaim("org_id").Value).IsEqualTo(organization);
            await Assert.That(validated.TryGetClaim("project_id", out _)).IsEqualTo(projectTarget);
            if (project is not null)
            {
                await Assert.That(validated.GetClaim("project_id").Value).IsEqualTo(project);
            }

            var payload = JsonSerializer.Deserialize<JsonElement>(Base64UrlEncoder.Decode(exchangedToken.Split('.')[1]));
            await Assert.That(payload.TryGetProperty("auth_time", out _)).IsFalse();
            await Assert.That(exchanged.TryGetProperty("id_token", out _)).IsFalse();
            await Assert.That(exchanged.TryGetProperty("refresh_token", out _)).IsFalse();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Service_and_organization_and_project_api_key_tokens_have_no_authentication_time_even_when_submitted(bool submitTime)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(-2) };
        await using var oidc = await StartAsync(clock);
        var parameters = new Dictionary<string, string>();
        if (submitTime)
        {
            parameters["auth_time"] = clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }

        using (var service = await oidc.PostTokenAsync(new(parameters) { ["grant_type"] = "client_credentials" }, ServiceId, ServiceSecret))
        {
            await AssertMachineAuthenticationTimeAbsentAsync(oidc, service, ServiceId);
        }

        var (_, token) = await oidc.SignInUserAsync(Email);
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        foreach (var path in new[] { $"{Organizations}/{organization}/api-keys", $"{Organizations}/{organization}/projects/{project}/api-keys" })
        {
            var created = await oidc.OkAsync(HttpMethod.Post, path, token, new { name = "backend", scopes = ReadScopes }, HttpStatusCode.Created);
            var key = created.GetProperty("key").GetString()!;
            using var exchanged = await oidc.ApiKeyTokenAsync(key, parameters);
            await AssertMachineAuthenticationTimeAbsentAsync(oidc, exchanged, key[..24]);
        }
    }

    private Task<OidcHarness> StartAsync(StoreTestClock clock) => OidcHarness.StartAsync(fixture, values =>
    {
        values["access-token-audiences"] = Audience;
        values["service-clients"] = $"{ServiceId}:{ServiceSecret}";
    }, clock: clock);

    private static async Task AssertUserAuthenticationTimeAsync(OidcHarness oidc, JsonElement tokens, long expected)
    {
        var access = tokens.GetProperty("access_token").GetString()!;
        await oidc.ValidateAsync(access, Audience);
        await AssertNumericAuthenticationTimeAsync(access, expected);
        var identity = tokens.GetProperty("id_token").GetString()!;
        var keys = new JsonWebKeySet(await oidc.Client.GetStringAsync("/terrascale/.well-known/jwks.json"));
        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(identity, new TokenValidationParameters
        {
            ValidIssuer = OidcHarness.Issuer,
            ValidAudience = OidcIssuer.DashboardClientId,
            ValidTypes = ["JWT"],
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKeys = keys.GetSigningKeys(),
            ClockSkew = TimeSpan.Zero,
        });
        await Assert.That(validated.IsValid).IsTrue();
        await AssertNumericAuthenticationTimeAsync(identity, expected);
    }

    private static async Task AssertNumericAuthenticationTimeAsync(string token, long expected)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(Base64UrlEncoder.Decode(token.Split('.')[1]));
        var time = payload.GetProperty("auth_time");
        await Assert.That(time.ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(time.GetInt64()).IsEqualTo(expected);
    }

    private static async Task AssertMachineAuthenticationTimeAbsentAsync(OidcHarness oidc, HttpResponseMessage response, string subject)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(response);
        var token = tokens.GetProperty("access_token").GetString()!;
        var access = await oidc.ValidateAsync(token, Audience);
        await Assert.That(access.Subject).IsEqualTo(subject);
        var payload = JsonSerializer.Deserialize<JsonElement>(Base64UrlEncoder.Decode(token.Split('.')[1]));
        await Assert.That(payload.TryGetProperty("auth_time", out _)).IsFalse();
        await Assert.That(tokens.TryGetProperty("id_token", out _)).IsFalse();
        await Assert.That(tokens.TryGetProperty("refresh_token", out _)).IsFalse();
    }

    private static async Task<string> SignPayloadAsync(OidcHarness oidc, JsonObject payload)
    {
        var keys = await oidc.Server.App.Services.GetRequiredService<KeyRing>().GetActiveAsync(OidcIssuer.InstanceId, CancellationToken.None);
        try
        {
            return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(payload.ToJsonString(),
                new SigningCredentials(keys[0].SecurityKey, SecurityAlgorithms.EcdsaSha256), new Dictionary<string, object> { ["typ"] = "at+jwt" });
        }
        finally
        {
            foreach (var key in keys)
            {
                key.Dispose();
            }
        }
    }
}
