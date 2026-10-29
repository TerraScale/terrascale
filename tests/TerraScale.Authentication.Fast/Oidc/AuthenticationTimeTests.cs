using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>Primary authentication time survives user issuance, refresh and exchange; machine tokens omit it.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuthenticationTimeTests(TerraBaseFixture fixture)
{
    private const string Email = "alice@example.test";
    private const string Password = "correct horse battery";
    private const string Audience = "https://api.terrascale.tech";
    private const string ServiceId = "01M4EQ2FRJ8S8B29E8TQVWNW4K";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";
    private const string SignIn = $"/ui/{OidcIssuer.Slug}/sign-in";
    private static readonly string[] ReadScopes = ["documents:read"];

    [Test]
    [Arguments("password")]
    [Arguments("code")]
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

        Dictionary<string, string> credentials;
        if (method == "password")
        {
            credentials = new() { ["email"] = Email, ["password"] = Password, ["auth_time"] = "0" };
        }
        else
        {
            using (var start = await oidc.PostFormAsync(SignIn, new() { ["email"] = Email, ["auth_time"] = "0" }))
            {
                await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            credentials = new() { ["email"] = Email, ["code"] = await oidc.Email.NextCodeAsync(Email), ["auth_time"] = "0" };
        }

        clock.UtcNow = clock.UtcNow.AddSeconds(10);
        var authenticatedAt = clock.UtcNow.ToUnixTimeSeconds();
        using var signedIn = await oidc.PostFormAsync($"{SignIn}/{(method == "password" ? "password" : "verify")}", credentials);
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var session = OidcHarness.SessionCookie(signedIn);
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
        var tokens = await OidcHarness.JsonAsync(redeemed);

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
    [Arguments(false, 30, true)]
    [Arguments(false, 31, false)]
    [Arguments(true, 30, true)]
    [Arguments(true, 31, false)]
    public async Task Organization_and_project_exchange_preserves_authentication_time_with_bounded_replica_clock_skew(bool projectTarget, int receiverSecondsBehind, bool accepted)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(-2) };
        await using var oidc = await StartAsync(clock);
        var authenticatedAt = clock.UtcNow.ToUnixTimeSeconds();
        var (user, _, tokens) = await oidc.SignInAndRedeemAsync(Email);
        await AssertUserAuthenticationTimeAsync(oidc, tokens, authenticatedAt);
        var access = tokens.GetProperty("access_token").GetString()!;
        var organization = await oidc.CreateOrganizationAsync(access);
        var project = projectTarget ? await oidc.CreateProjectAsync(access, organization, "Production") : null;

        var replicaClock = new StoreTestClock { UtcNow = clock.UtcNow.AddSeconds(-receiverSecondsBehind) };
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
            if (accepted)
            {
                await Assert.That(exchange.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await AssertExchangedAuthenticationTimeAsync(oidc, clock, await OidcHarness.JsonAsync(exchange), user.Id, organization, project, authenticatedAt);
            }
            else
            {
                await AssertInvalidGrantWithoutCredentialsAsync(exchange);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Delayed_refresh_then_organization_and_project_exchange_on_a_replica_preserves_original_authentication_time(bool projectTarget)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(-2) };
        await using var oidc = await StartAsync(clock);
        var authenticatedAt = clock.UtcNow.ToUnixTimeSeconds();
        var (user, _, tokens) = await oidc.SignInAndRedeemAsync(Email);
        await AssertUserAuthenticationTimeAsync(oidc, tokens, authenticatedAt);
        var access = tokens.GetProperty("access_token").GetString()!;
        var organization = await oidc.CreateOrganizationAsync(access);
        var project = projectTarget ? await oidc.CreateProjectAsync(access, organization, "Production") : null;

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        using var refresh = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var refreshed = await OidcHarness.JsonAsync(refresh);
        await Assert.That(refreshed.GetProperty("refresh_token").GetString()).IsNotEqualTo(tokens.GetProperty("refresh_token").GetString());
        await AssertUserAuthenticationTimeAsync(oidc, refreshed, authenticatedAt);

        var replicaClock = new StoreTestClock { UtcNow = clock.UtcNow.AddSeconds(10) };
        await using var replica = await oidc.StartReplicaAsync(values => values["access-token-audiences"] = Audience, clock: replicaClock);
        var target = new Dictionary<string, string>
        {
            ["organization"] = organization,
            ["auth_time"] = replicaClock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };
        if (project is not null)
        {
            target["project"] = project;
        }

        using var exchange = await oidc.ExchangeAsync(refreshed.GetProperty("access_token").GetString()!, target, replica.Client);
        await Assert.That(exchange.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await AssertExchangedAuthenticationTimeAsync(oidc, clock, await OidcHarness.JsonAsync(exchange), user.Id, organization, project, authenticatedAt);
    }

    [Test]
    [Arguments(false, "original")]
    [Arguments(false, "zero")]
    [Arguments(false, "missing")]
    [Arguments(false, "malformed")]
    [Arguments(false, "numeric-string")]
    [Arguments(false, "array")]
    [Arguments(false, "array-many")]
    [Arguments(false, "object")]
    [Arguments(false, "boolean")]
    [Arguments(false, "null")]
    [Arguments(false, "negative")]
    [Arguments(false, "far-future")]
    [Arguments(false, "fractional")]
    [Arguments(false, "overflowing")]
    [Arguments(true, "original")]
    [Arguments(true, "zero")]
    [Arguments(true, "missing")]
    [Arguments(true, "malformed")]
    [Arguments(true, "numeric-string")]
    [Arguments(true, "array")]
    [Arguments(true, "array-many")]
    [Arguments(true, "object")]
    [Arguments(true, "boolean")]
    [Arguments(true, "null")]
    [Arguments(true, "negative")]
    [Arguments(true, "far-future")]
    [Arguments(true, "fractional")]
    [Arguments(true, "overflowing")]
    public async Task Exchange_requires_valid_signed_authentication_time_and_submitted_time_cannot_repair_it(bool projectTarget, string authenticationTime)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(-2) };
        await using var oidc = await StartAsync(clock);
        var authenticatedAt = clock.UtcNow.ToUnixTimeSeconds();
        var (user, _, tokens) = await oidc.SignInAndRedeemAsync(Email);
        var access = tokens.GetProperty("access_token").GetString()!;
        var organization = await oidc.CreateOrganizationAsync(access);
        var project = projectTarget ? await oidc.CreateProjectAsync(access, organization, "Production") : null;

        // Change only auth_time, retaining the real token/authorization ids and their TerraBase entries.
        var payload = JsonNode.Parse(Base64UrlEncoder.Decode(access.Split('.')[1]))!.AsObject();
        if (authenticationTime == "missing")
        {
            await Assert.That(payload.Remove("auth_time")).IsTrue();
        }
        else
        {
            payload["auth_time"] = authenticationTime switch
            {
                "original" => JsonValue.Create(authenticatedAt),
                "zero" => JsonValue.Create(0),
                "malformed" => JsonValue.Create("not-a-time"),
                "numeric-string" => JsonValue.Create(authenticatedAt.ToString(CultureInfo.InvariantCulture)),
                "array" => new JsonArray(JsonValue.Create(0)),
                "array-many" => new JsonArray(JsonValue.Create(0), JsonValue.Create(authenticatedAt)),
                "object" => new JsonObject { ["time"] = authenticatedAt },
                "boolean" => JsonValue.Create(true),
                "null" => null,
                "negative" => JsonValue.Create(-1),
                "far-future" => JsonValue.Create(authenticatedAt + 3600),
                "fractional" => JsonValue.Create(authenticatedAt + 0.5m),
                "overflowing" => JsonValue.Create(9223372036854775808UL),
                _ => throw new ArgumentOutOfRangeException(nameof(authenticationTime)),
            };
        }

        access = await SignPayloadAsync(oidc, payload);
        await Assert.That((await oidc.ValidateAsync(access, OidcHarness.Issuer)).Subject).IsEqualTo(user.Id);
        var replicaClock = new StoreTestClock { UtcNow = clock.UtcNow };
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
                target["auth_time"] = authenticatedAt.ToString(CultureInfo.InvariantCulture);
            }

            using var exchange = await oidc.ExchangeAsync(access, target, replica.Client);
            if (authenticationTime is "original" or "zero")
            {
                await Assert.That(exchange.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await AssertExchangedAuthenticationTimeAsync(oidc, clock, await OidcHarness.JsonAsync(exchange), user.Id, organization, project, authenticationTime == "zero" ? 0 : authenticatedAt);
            }
            else
            {
                await AssertInvalidGrantWithoutCredentialsAsync(exchange);
            }
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
            await AssertMachineAuthenticationTimeAbsentAsync(oidc, clock, service, ServiceId);
        }

        var (_, token) = await oidc.SignInUserAsync(Email);
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        foreach (var path in new[] { $"{Organizations}/{organization}/api-keys", $"{Organizations}/{organization}/projects/{project}/api-keys" })
        {
            var created = await oidc.OkAsync(HttpMethod.Post, path, token, new { name = "backend", scopes = ReadScopes }, HttpStatusCode.Created);
            var key = created.GetProperty("key").GetString()!;
            using var exchanged = await oidc.ApiKeyTokenAsync(key, parameters);
            await AssertMachineAuthenticationTimeAbsentAsync(oidc, clock, exchanged, created.GetProperty("api_key").GetProperty("id").GetString()!);
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

    private static async Task AssertExchangedAuthenticationTimeAsync(OidcHarness oidc, TimeProvider clock, JsonElement tokens, string userId, string organization, string? project, long authenticatedAt)
    {
        var access = tokens.GetProperty("access_token").GetString()!;
        var validated = await oidc.ValidateAsync(access, Audience, clock);
        await Assert.That(validated.Subject).IsEqualTo(userId);
        await Assert.That(validated.GetClaim("org_id").Value).IsEqualTo(organization);
        await Assert.That(validated.TryGetClaim("project_id", out _)).IsEqualTo(project is not null);
        if (project is not null)
        {
            await Assert.That(validated.GetClaim("project_id").Value).IsEqualTo(project);
        }

        await AssertNumericAuthenticationTimeAsync(access, authenticatedAt);
        await Assert.That(tokens.TryGetProperty("id_token", out _)).IsFalse();
        await Assert.That(tokens.TryGetProperty("refresh_token", out _)).IsFalse();
    }

    private static async Task AssertInvalidGrantWithoutCredentialsAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(OidcHarness.SessionCookie(response)).IsNull();
        var tokens = await OidcHarness.JsonAsync(response);
        await Assert.That(tokens.GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        await Assert.That(tokens.TryGetProperty("access_token", out _)).IsFalse();
        await Assert.That(tokens.TryGetProperty("id_token", out _)).IsFalse();
        await Assert.That(tokens.TryGetProperty("refresh_token", out _)).IsFalse();
    }

    private static async Task AssertMachineAuthenticationTimeAbsentAsync(OidcHarness oidc, TimeProvider clock, HttpResponseMessage response, string subject)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(response);
        var token = tokens.GetProperty("access_token").GetString()!;
        var access = await oidc.ValidateAsync(token, Audience, clock);
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
